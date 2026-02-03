using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace DotsNetworking.SceneGraph.Collections
{
    // README: see NativeRegistry.md in this folder for design/usage details.
    [NativeContainer]
    public unsafe struct NativeRegistry<TKey> : IDisposable
        where TKey : unmanaged, IEquatable<TKey>
    {
        internal const int WriteLockBit = NativeRegistryLocks.WriteLockBit;

        private UnsafeParallelHashMap<TKey, int> keyToIndex;
        private UnsafeParallelHashMap<int, int> typeIdToIndex;
        private UnsafeList<RegistryTypeEntry> typeEntries;
        private int keyCount;
        private int pageSize;
        private Allocator allocator;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private AtomicSafetyHandle safety;
#endif

        public NativeRegistry(int pageSize, int keyCapacity, int typeCapacity, Allocator allocator)
        {
            if (pageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");

            if (keyCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(keyCapacity));

            if (typeCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(typeCapacity));

            this.pageSize = pageSize;
            this.allocator = allocator;
            keyCount = 0;

            var initialKeyCapacity = Math.Max(1, keyCapacity);
            var initialTypeCapacity = Math.Max(1, typeCapacity);

            keyToIndex = new UnsafeParallelHashMap<TKey, int>(initialKeyCapacity, allocator);
            typeIdToIndex = new UnsafeParallelHashMap<int, int>(initialTypeCapacity, allocator);
            typeEntries = new UnsafeList<RegistryTypeEntry>(initialTypeCapacity, allocator);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            safety = AtomicSafetyHandle.Create();
#endif
        }

        public bool IsCreated => keyToIndex.IsCreated;

        public int KeyCount => keyCount;

        public int PageSize => pageSize;

        public void RegisterType<T>()
            where T : unmanaged
        {
            var typeId = NativeRegistryTypeRegistry.GetOrCreateTypeId<T>();
            RegisterTypeInternal<T>(typeId);
        }

        public void RegisterType<T>(int typeId)
            where T : unmanaged
        {
            NativeRegistryTypeRegistry.SetTypeId<T>(typeId);
            RegisterTypeInternal<T>(typeId);
        }

        public bool IsTypeRegistered<T>()
            where T : unmanaged
        {
            CheckCreated();
            CheckRead();
            var typeId = NativeRegistryTypeId<T>.Value;
            return typeId != 0 && typeIdToIndex.ContainsKey(typeId);
        }

        public int RegisterKey(TKey key)
        {
            CheckCreated();
            CheckWrite();

            if (keyToIndex.TryGetValue(key, out var existingIndex))
                return existingIndex;

            EnsureKeyCapacity(keyCount + 1);
            var index = keyCount++;
            keyToIndex.Add(key, index);

            for (int i = 0; i < typeEntries.Length; i++)
            {
                var entry = typeEntries[i];
                var ops = NativeRegistryTypeRegistry.GetOps(entry.TypeId);
                ops.Resize(entry.Storage, keyCount);
            }

            return index;
        }

        public bool TryGetIndex(TKey key, out int index)
        {
            CheckCreated();
            CheckRead();
            return keyToIndex.TryGetValue(key, out index);
        }

        public RegistryReader<T> AcquireRead<T>(TKey key)
            where T : unmanaged
        {
            CheckCreated();
            CheckRead();

            var typeId = GetTypeIdOrThrow<T>();
            var index = GetIndexOrThrow(key);
            var storage = GetStorageOrThrow<T>(typeId);

            var valuePtr = storage->Values.GetPtr(index);
            var refCountPtr = storage->RefCounts.GetPtr(index);
            var acquired = NativeRegistryLocks.AcquireReadOrFailIfLocked(refCountPtr);

            return new RegistryReader<T>(valuePtr, refCountPtr, acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                , safety
#endif
            );
        }

        public RegistryReader<T> AcquireReadAt<T>(int index)
            where T : unmanaged
        {
            CheckCreated();
            CheckRead();

            var typeId = GetTypeIdOrThrow<T>();
            var storage = GetStorageOrThrow<T>(typeId);
            var resolvedIndex = GetIndexOrThrow(index);

            var valuePtr = storage->Values.GetPtr(resolvedIndex);
            var refCountPtr = storage->RefCounts.GetPtr(resolvedIndex);
            var acquired = NativeRegistryLocks.AcquireReadOrFailIfLocked(refCountPtr);

            return new RegistryReader<T>(valuePtr, refCountPtr, acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                , safety
#endif
            );
        }

        public RegistryWriter<T> AcquireWrite<T>(TKey key)
            where T : unmanaged
        {
            CheckCreated();
            CheckWrite();

            var typeId = GetTypeIdOrThrow<T>();
            var index = GetIndexOrThrow(key);
            var storage = GetStorageOrThrow<T>(typeId);

            var valuePtr = storage->Values.GetPtr(index);
            var refCountPtr = storage->RefCounts.GetPtr(index);
            var acquired = NativeRegistryLocks.AcquireWrite(refCountPtr);

            return new RegistryWriter<T>(valuePtr, refCountPtr, acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                , safety
#endif
            );
        }

        public RegistryWriter<T> AcquireWriteAt<T>(int index)
            where T : unmanaged
        {
            CheckCreated();
            CheckWrite();

            var typeId = GetTypeIdOrThrow<T>();
            var storage = GetStorageOrThrow<T>(typeId);
            var resolvedIndex = GetIndexOrThrow(index);

            var valuePtr = storage->Values.GetPtr(resolvedIndex);
            var refCountPtr = storage->RefCounts.GetPtr(resolvedIndex);
            var acquired = NativeRegistryLocks.AcquireWrite(refCountPtr);

            return new RegistryWriter<T>(valuePtr, refCountPtr, acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                , safety
#endif
            );
        }

        public void Dispose()
        {
            if (!IsCreated)
                return;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(safety);
#endif

            for (int i = 0; i < typeEntries.Length; i++)
            {
                var entry = typeEntries[i];
                var ops = NativeRegistryTypeRegistry.GetOps(entry.TypeId);
                ops.Dispose(entry.Storage, allocator);
            }

            typeEntries.Dispose();
            typeIdToIndex.Dispose();
            keyToIndex.Dispose();

            typeEntries = default;
            typeIdToIndex = default;
            keyToIndex = default;
            pageSize = 0;
            keyCount = 0;
            allocator = default;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(safety);
            safety = default;
#endif
        }

#if UNITY_INCLUDE_TESTS
        public int GetRefCountForTesting<T>(TKey key)
            where T : unmanaged
        {
            CheckRead();
            var typeId = GetTypeIdOrThrow<T>();
            var index = GetIndexOrThrow(key);
            var storage = GetStorageOrThrow<T>(typeId);
            var refCountPtr = storage->RefCounts.GetPtr(index);
            return Volatile.Read(ref UnsafeUtility.AsRef<int>(refCountPtr));
        }
#endif

        private void RegisterTypeInternal<T>(int typeId)
            where T : unmanaged
        {
            CheckCreated();
            CheckWrite();

            if (typeIdToIndex.ContainsKey(typeId))
                return;

            EnsureTypeCapacity(typeEntries.Length + 1);

            var storage = (RegistryStorage<T>*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<RegistryStorage<T>>(),
                UnsafeUtility.AlignOf<RegistryStorage<T>>(),
                allocator);

            *storage = new RegistryStorage<T>(pageSize, allocator);

            if (keyCount > 0)
            {
                storage->Values.Resize(keyCount, NativeArrayOptions.ClearMemory);
                storage->RefCounts.Resize(keyCount, NativeArrayOptions.ClearMemory);
            }

            var entry = new RegistryTypeEntry
            {
                TypeId = typeId,
                Storage = storage,
                ElementSize = UnsafeUtility.SizeOf<T>(),
                Alignment = UnsafeUtility.AlignOf<T>()
            };

            typeIdToIndex.Add(typeId, typeEntries.Length);
            typeEntries.Add(entry);
        }

        private void EnsureKeyCapacity(int required)
        {
            if (keyToIndex.Capacity >= required)
                return;

            var newCapacity = Math.Max(required, keyToIndex.Capacity * 2);
            keyToIndex.Capacity = newCapacity;
        }

        private void EnsureTypeCapacity(int required)
        {
            if (typeIdToIndex.Capacity < required)
            {
                var newCapacity = Math.Max(required, typeIdToIndex.Capacity * 2);
                typeIdToIndex.Capacity = newCapacity;
            }

            if (typeEntries.Capacity < required)
                typeEntries.Capacity = Math.Max(required, typeEntries.Capacity * 2);
        }

        private void CheckCreated()
        {
            if (!IsCreated)
                throw new InvalidOperationException("NativeRegistry is not created.");
        }

        private void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(safety);
#endif
        }

        private void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(safety);
#endif
        }

        private int GetTypeIdOrThrow<T>()
            where T : unmanaged
        {
            var typeId = NativeRegistryTypeId<T>.Value;
            if (typeId == 0)
                throw new InvalidOperationException($"Type {typeof(T)} is not registered.");

            return typeId;
        }

        private int GetIndexOrThrow(TKey key)
        {
            if (!keyToIndex.TryGetValue(key, out var index))
                throw new KeyNotFoundException($"Key {key} is not registered.");

            return index;
        }

        private int GetIndexOrThrow(int index)
        {
            if ((uint)index >= (uint)keyCount)
                throw new IndexOutOfRangeException($"Index {index} is out of range of '{keyCount}'.");

            return index;
        }

        private RegistryStorage<T>* GetStorageOrThrow<T>(int typeId)
            where T : unmanaged
        {
            if (!typeIdToIndex.TryGetValue(typeId, out var entryIndex))
                throw new InvalidOperationException($"TypeId {typeId} is not registered.");

            ref var entry = ref typeEntries.ElementAt(entryIndex);
            if (entry.ElementSize != UnsafeUtility.SizeOf<T>())
                throw new InvalidOperationException($"TypeId {typeId} does not match {typeof(T)}.");

            return (RegistryStorage<T>*)entry.Storage;
        }

        private struct RegistryTypeEntry
        {
            public int TypeId;
            public void* Storage;
            public int ElementSize;
            public int Alignment;
        }
    }

    public static class NativeRegistryBuilder
    {
        public static NativeRegistry<TKey> Create<TKey>(
            Allocator allocator,
            int pageSize,
            int keyCapacity,
            params Type[] types)
            where TKey : unmanaged, IEquatable<TKey>
        {
            var registry = new NativeRegistry<TKey>(pageSize, keyCapacity, types?.Length ?? 0, allocator);

            if (types == null)
                return registry;

            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null)
                    throw new ArgumentNullException(nameof(types), "Type array contains a null entry.");

                registry = RegisterTypeByReflection(registry, type);
            }

            return registry;
        }

        private static NativeRegistry<TKey> RegisterTypeByReflection<TKey>(
            NativeRegistry<TKey> registry,
            Type type)
            where TKey : unmanaged, IEquatable<TKey>
        {
            var method = typeof(NativeRegistryBuilder)
                .GetMethod(nameof(RegisterTypeGeneric), BindingFlags.Static | BindingFlags.NonPublic);

            if (method == null)
                throw new MissingMethodException(nameof(NativeRegistryBuilder), nameof(RegisterTypeGeneric));

            try
            {
                var generic = method.MakeGenericMethod(typeof(TKey), type);
                return (NativeRegistry<TKey>)generic.Invoke(null, new object[] { registry });
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Type {type} must be unmanaged to register.", nameof(type), ex);
            }
        }

        private static NativeRegistry<TKey> RegisterTypeGeneric<TKey, T>(
            NativeRegistry<TKey> registry)
            where TKey : unmanaged, IEquatable<TKey>
            where T : unmanaged
        {
            registry.RegisterType<T>();
            return registry;
        }
    }

    public unsafe struct RegistryReader<T> : IDisposable
        where T : unmanaged
    {
        private T* valuePtr;
        private int* refCountPtr;
        private byte acquired;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private AtomicSafetyHandle safety;
#endif

        internal RegistryReader(T* valuePtr, int* refCountPtr, bool acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            , AtomicSafetyHandle safety
#endif
        )
        {
            this.valuePtr = valuePtr;
            this.refCountPtr = refCountPtr;
            this.acquired = acquired ? (byte)1 : (byte)0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            this.safety = safety;
#endif
        }

        public bool IsAccessible => acquired != 0;

        public ref readonly T Value
        {
            get
            {
                if (acquired == 0)
                    throw new InvalidOperationException("Handle is not accessible.");
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(safety);
#endif
                return ref UnsafeUtility.AsRef<T>(valuePtr);
            }
        }

        public void Dispose()
        {
            if (acquired == 0)
                return;
            NativeRegistryLocks.ReleaseRead(refCountPtr);
        }
    }

    public unsafe struct RegistryWriter<T> : IDisposable
        where T : unmanaged
    {
        private T* valuePtr;
        private int* refCountPtr;
        private byte acquired;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private AtomicSafetyHandle safety;
#endif

        internal RegistryWriter(T* valuePtr, int* refCountPtr, bool acquired
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            , AtomicSafetyHandle safety
#endif
        )
        {
            this.valuePtr = valuePtr;
            this.refCountPtr = refCountPtr;
            this.acquired = acquired ? (byte)1 : (byte)0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            this.safety = safety;
#endif
        }

        public bool IsAccessible => acquired != 0;

        public bool CanWrite
        {
            get
            {
                if (acquired == 0)
                    return false;

                var count = Volatile.Read(ref UnsafeUtility.AsRef<int>(refCountPtr));
                if (count == NativeRegistryLocks.WriteLockBit)
                    return true;

                if (count == NativeRegistryLocks.WriteIntentBit)
                    return NativeRegistryLocks.TryPromoteWriteLock(refCountPtr);

                return false;
            }
        }

        public ref T Value
        {
            get
            {
                if (!CanWrite)
                    throw new InvalidOperationException("Handle is not writable.");
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(safety);
#endif
                return ref UnsafeUtility.AsRef<T>(valuePtr);
            }
        }

        public void Dispose()
        {
            if (acquired == 0)
                return;
            NativeRegistryLocks.ReleaseWrite(refCountPtr);
        }
    }

    internal static unsafe class NativeRegistryLocks
    {
        public const int WriteLockBit = int.MinValue;
        public const int WriteIntentBit = 1 << 30;
        private const int WriterMask = WriteLockBit | WriteIntentBit;

        public static bool AcquireReadOrFailIfLocked(int* refCountPtr)
        {
            ref var count = ref UnsafeUtility.AsRef<int>(refCountPtr);
            while (true)
            {
                var observed = Volatile.Read(ref count);
                if ((observed & WriterMask) != 0)
                    return false;

                var next = observed + 1;
                if (Interlocked.CompareExchange(ref count, next, observed) == observed)
                    return true;
            }
        }

        public static void ReleaseRead(int* refCountPtr)
        {
            ref var count = ref UnsafeUtility.AsRef<int>(refCountPtr);
            Interlocked.Decrement(ref count);
        }

        public static bool AcquireWrite(int* refCountPtr)
        {
            ref var count = ref UnsafeUtility.AsRef<int>(refCountPtr);
            while (true)
            {
                var observed = Volatile.Read(ref count);
                if ((observed & WriterMask) != 0)
                    return false;

                var next = observed | WriteIntentBit;
                if (Interlocked.CompareExchange(ref count, next, observed) != observed)
                    continue;

                return true;
            }
        }

        public static bool TryPromoteWriteLock(int* refCountPtr)
        {
            ref var count = ref UnsafeUtility.AsRef<int>(refCountPtr);
            return Interlocked.CompareExchange(ref count, WriteLockBit, WriteIntentBit) == WriteIntentBit;
        }

        public static void ReleaseWrite(int* refCountPtr)
        {
            ref var count = ref UnsafeUtility.AsRef<int>(refCountPtr);
            while (true)
            {
                var observed = Volatile.Read(ref count);
                if ((observed & WriteLockBit) != 0)
                {
                    Interlocked.Exchange(ref count, 0);
                    return;
                }

                if ((observed & WriteIntentBit) != 0)
                {
                    var next = observed & ~WriteIntentBit;
                    if (Interlocked.CompareExchange(ref count, next, observed) == observed)
                        return;

                    continue;
                }

                return;
            }
        }
    }

    internal unsafe struct RegistryStorage<T>
        where T : unmanaged
    {
        public UnsafePagedList<T> Values;
        public UnsafePagedList<int> RefCounts;

        public RegistryStorage(int pageSize, Allocator allocator)
        {
            Values = new UnsafePagedList<T>(pageSize, allocator, NativeArrayOptions.ClearMemory);
            RefCounts = new UnsafePagedList<int>(pageSize, allocator, NativeArrayOptions.ClearMemory);
        }

        public void Dispose()
        {
            Values.Dispose();
            RefCounts.Dispose();
        }
    }

    internal unsafe interface INativeRegistryTypeOps
    {
        void Resize(void* storage, int length);
        void Dispose(void* storage, Allocator allocator);
    }

    internal sealed unsafe class NativeRegistryTypeOps<T> : INativeRegistryTypeOps
        where T : unmanaged
    {
        public void Resize(void* storage, int length)
        {
            var typed = (RegistryStorage<T>*)storage;
            typed->Values.Resize(length, NativeArrayOptions.ClearMemory);
            typed->RefCounts.Resize(length, NativeArrayOptions.ClearMemory);
        }

        public void Dispose(void* storage, Allocator allocator)
        {
            var typed = (RegistryStorage<T>*)storage;
            typed->Dispose();
            UnsafeUtility.Free(storage, allocator);
        }
    }

    internal static class NativeRegistryTypeRegistry
    {
        private static readonly Dictionary<int, INativeRegistryTypeOps> Ops = new Dictionary<int, INativeRegistryTypeOps>();
        private static int nextTypeId;

        public static int GetOrCreateTypeId<T>()
            where T : unmanaged
        {
            var current = NativeRegistryTypeId<T>.Value;
            if (current != 0)
                return current;

            var typeId = Interlocked.Increment(ref nextTypeId);
            NativeRegistryTypeId<T>.Value = typeId;
            EnsureOpsRegistered<T>(typeId);
            return typeId;
        }

        public static void SetTypeId<T>(int typeId)
            where T : unmanaged
        {
            if (typeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(typeId));

            var current = NativeRegistryTypeId<T>.Value;
            if (current != 0 && current != typeId)
                throw new InvalidOperationException($"Type {typeof(T)} already registered with a different id.");

            NativeRegistryTypeId<T>.Value = typeId;
            EnsureOpsRegistered<T>(typeId);
        }

        public static INativeRegistryTypeOps GetOps(int typeId)
        {
            return Ops[typeId];
        }

        private static void EnsureOpsRegistered<T>(int typeId)
            where T : unmanaged
        {
            if (!Ops.ContainsKey(typeId))
                Ops.Add(typeId, new NativeRegistryTypeOps<T>());
        }
    }

    internal static class NativeRegistryTypeId<T>
        where T : unmanaged
    {
        public static int Value;
    }
}
