using System;
using System.Reflection;
using BovineLabs.Core.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace DotsNetworking.SceneGraph
{
    [CreateAssetMenu(menuName = "SceneGraph/Blob Asset Handler", fileName = "BlobAssetHandler")]
    [PreferBinarySerialization]
    public sealed class BlobAssetHandler : ScriptableObject, ISerializationCallbackReceiver
    {
        [HideInInspector][SerializeField] private byte[] data = Array.Empty<byte>();

        [NonSerialized] private NativeArray<byte> nativeData;
        [NonSerialized] private BlobAssetReference<Section> blobRef;

        private const int BlobVersion = 0;

        private static readonly MethodInfo TryReadInplaceMethod =
            typeof(BlobAssetReference<Section>).GetMethod("TryReadInplace", BindingFlags.NonPublic | BindingFlags.Static);

        private static object[] tryReadParams;

        public long DataSize => data?.LongLength ?? 0L;

        public NativeArray<byte> GetData()
        {
            // With proper serialization callbacks this should already be created,
            // but keep this safe for edge cases (domain reload timing, etc.)
            if (!nativeData.IsCreated)
                RebuildCachesFromManaged();

            return nativeData;
        }

        public bool IsCreated => blobRef.IsCreated;

        public ref Section Value
        {
            get
            {
                if (!blobRef.IsCreated)
                    throw new InvalidOperationException("BlobAssetHandler has no valid blob.");
                return ref blobRef.Value;
            }
        }

        public unsafe void* GetUnsafePtr()
        {
            return blobRef.GetUnsafePtr();
        }

        public bool TryCreateOwnedBlob(out BlobAssetReference<Section> blob)
        {
            var dataSource = GetData();
            if (!TryReadInplace(dataSource, out var inplace))
            {
                blob = default;
                return false;
            }

            var size = DataSize;
            if (size <= 0 || size > int.MaxValue)
            {
                blob = default;
                return false;
            }

            unsafe { blob = BlobAssetReference<Section>.Create(inplace.GetUnsafePtr(), (int)size); }
            return blob.IsCreated;
        }

        public Unity.Entities.Hash128 GetHash()
        {
            if (!IsCreated)
                return default;

            Unity.Entities.Hash128 hash = default;
            long hash64 = BlobAssetReferenceInternal.GetHash(blobRef);
            unsafe { UnsafeUtility.MemCpy(&hash, &hash64, sizeof(long)); }
            return hash;
        }

        public void UpdateData(NativeArray<byte> newData)
        {
            if (!newData.IsCreated || newData.Length == 0)
            {
                data = Array.Empty<byte>();
                ClearCaches();
                return;
            }

            EnsureManagedCapacity(newData.Length);
            newData.CopyTo(data);

            EnsureNativeCapacity(newData.Length);
            newData.CopyTo(nativeData);

            RebuildBlobRef();
        }

        public void UpdateData(byte[] newData)
        {
            if (newData == null || newData.Length == 0)
            {
                data = Array.Empty<byte>();
                ClearCaches();
                return;
            }

            EnsureManagedCapacity(newData.Length);
            Buffer.BlockCopy(newData, 0, data, 0, newData.Length);

            EnsureNativeCapacity(newData.Length);
            nativeData.CopyFrom(newData);

            RebuildBlobRef();
        }

        // --- Serialization callbacks ---

        public void OnBeforeSerialize()
        {
            // If you ever allow nativeData mutation, you could copy it back to data here.
            // With your current API (no mutating access), data is always authoritative.
        }

        public void OnAfterDeserialize()
        {
            // Rebuild runtime caches from serialized data.
            RebuildCachesFromManaged();
        }

        private void OnDisable() => ClearCaches();
        private void OnDestroy() => ClearCaches();

        private void RebuildCachesFromManaged()
        {
            ClearCaches();

            if (data == null || data.Length == 0)
                return;

            nativeData = new NativeArray<byte>(data.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            nativeData.CopyFrom(data);

            RebuildBlobRef();
        }

        private void RebuildBlobRef()
        {
            blobRef = default;

            if (nativeData.IsCreated && nativeData.Length > 0 && TryReadInplace(nativeData, out var inplace))
                blobRef = inplace;
        }

        private void ClearCaches()
        {
            if (nativeData.IsCreated)
                nativeData.Dispose();

            nativeData = default;
            blobRef = default;
        }

        private void EnsureManagedCapacity(int length)
        {
            if (data == null || data.Length != length)
                data = new byte[length];
        }

        private void EnsureNativeCapacity(int length)
        {
            if (nativeData.IsCreated && nativeData.Length == length)
                return;

            if (nativeData.IsCreated)
                nativeData.Dispose();

            nativeData = new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public static bool TryReadInplace(NativeArray<byte> nativeData, out BlobAssetReference<Section> blob)
        {
            blob = default;

            if (TryReadInplaceMethod == null)
            {
                Debug.LogError("Could not find BlobAssetReference<Section>.TryReadInplace via reflection.");
                return false;
            }

            if (!nativeData.IsCreated || nativeData.Length == 0)
                return false;

            IntPtr ptr;
            unsafe { ptr = (IntPtr)nativeData.GetUnsafeReadOnlyPtr(); }

            tryReadParams ??= new object[5];
            tryReadParams[0] = ptr;
            tryReadParams[1] = (long)nativeData.Length;
            tryReadParams[2] = BlobVersion;
            tryReadParams[3] = default(BlobAssetReference<Section>);
            tryReadParams[4] = 0;

            try
            {
                var success = (bool)TryReadInplaceMethod.Invoke(null, tryReadParams);
                if (success)
                    blob = (BlobAssetReference<Section>)tryReadParams[3];

                return success;
            }
            catch (Exception e)
            {
                Debug.LogError($"TryReadInplace invoke failed: {e}");
                return false;
            }
        }
    }
}
