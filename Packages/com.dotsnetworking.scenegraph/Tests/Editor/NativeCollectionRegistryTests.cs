using System;
using DotsNetworking.SceneGraph.Collections;
using NUnit.Framework;
using Unity.Collections;

namespace DotsNetworking.SceneGraph.Tests.Editor
{
    public sealed class NativeRegistryTests
    {
        [Test]
        public void RegisterTypesAndKeys_AcquireReadWrite()
        {
            var registry = new NativeRegistry<int>(pageSize: 4, keyCapacity: 4, typeCapacity: 2, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterType<float>();

                registry.RegisterKey(10);
                registry.RegisterKey(20);

                using (var write = registry.AcquireWrite<int>(10))
                {
                    write.Value = 123;
                }

                using (var write = registry.AcquireWrite<float>(20))
                {
                    write.Value = 3.5f;
                }

                using (var read = registry.AcquireRead<int>(10))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(123, read.Value);
                }

                using (var read = registry.AcquireRead<float>(20))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(3.5f, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void RegisterKeysBeforeTypes_ExpandsOnRegister()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 4, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterKey(1);
                registry.RegisterKey(2);
                registry.RegisterType<int>();

                using (var read = registry.AcquireRead<int>(1))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(0, read.Value);
                }

                using (var read = registry.AcquireRead<int>(2))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(0, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void ReadHandles_IncrementRefCount()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 1, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(7);

                using (var readA = registry.AcquireRead<int>(7))
                {
                    Assert.IsTrue(readA.IsAccessible);
                    Assert.AreEqual(1, registry.GetRefCountForTesting<int>(7));

                    using (var readB = registry.AcquireRead<int>(7))
                    {
                        Assert.IsTrue(readB.IsAccessible);
                        Assert.AreEqual(2, registry.GetRefCountForTesting<int>(7));
                    }

                    Assert.AreEqual(1, registry.GetRefCountForTesting<int>(7));
                }

                Assert.AreEqual(0, registry.GetRefCountForTesting<int>(7));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void WriteHandle_SetsLockBitAndClears()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 1, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(1);

                using (var write = registry.AcquireWrite<int>(1))
                {
                    Assert.IsTrue(write.CanWrite);
                    Assert.Less(registry.GetRefCountForTesting<int>(1), 0);
                    write.Value = 55;
                }

                Assert.AreEqual(0, registry.GetRefCountForTesting<int>(1));

                using (var read = registry.AcquireRead<int>(1))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(55, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void WriteHandle_ThrowsWhenReadersActive()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 1, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(3);

                using (var read = registry.AcquireRead<int>(3))
                using (var write = registry.AcquireWrite<int>(3))
                {
                    Assert.IsFalse(write.CanWrite);
                    Assert.Throws<InvalidOperationException>(() =>
                    {
                        var _ = write.Value;
                    });
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void WriteHandle_WritesWhenNoReaders()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 1, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(9);

                using (var write = registry.AcquireWrite<int>(9))
                {
                    Assert.IsTrue(write.CanWrite);
                    write.Value = 1234;
                }

                using (var read = registry.AcquireRead<int>(9))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(1234, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void Builder_CreatesFromTypes()
        {
            var registry = NativeRegistryBuilder.Create<int>(
                Allocator.Persistent,
                pageSize: 4,
                keyCapacity: 2,
                types: new[] { typeof(int), typeof(float) });

            try
            {
                registry.RegisterKey(1);

                using (var write = registry.AcquireWrite<float>(1))
                {
                    write.Value = 2.5f;
                }

                using (var read = registry.AcquireRead<float>(1))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(2.5f, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void AcquireRead_ReturnsInaccessibleHandleWhenWriteLocked()
        {
            var registry = new NativeRegistry<int>(pageSize: 2, keyCapacity: 1, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(5);

                using (var write = registry.AcquireWrite<int>(5))
                {
                    using (var read = registry.AcquireRead<int>(5))
                    {
                        Assert.IsFalse(read.IsAccessible);
                    }
                }

                using (var read = registry.AcquireRead<int>(5))
                {
                    Assert.IsTrue(read.IsAccessible);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Test]
        public void AcquireByIndex_ReadWrite()
        {
            var registry = new NativeRegistry<int>(pageSize: 4, keyCapacity: 2, typeCapacity: 1, allocator: Allocator.Persistent);
            try
            {
                registry.RegisterType<int>();
                registry.RegisterKey(42);
                registry.RegisterKey(84);

                Assert.IsTrue(registry.TryGetIndex(84, out var index));

                using (var write = registry.AcquireWriteAt<int>(index))
                {
                    write.Value = 900;
                }

                using (var read = registry.AcquireReadAt<int>(index))
                {
                    Assert.IsTrue(read.IsAccessible);
                    Assert.AreEqual(900, read.Value);
                }
            }
            finally
            {
                registry.Dispose();
            }
        }

        
    }
}
