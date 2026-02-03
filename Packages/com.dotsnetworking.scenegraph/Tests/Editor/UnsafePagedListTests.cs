using System;
using DotsNetworking.SceneGraph.Collections;
using NUnit.Framework;
using Unity.Collections;

namespace DotsNetworking.SceneGraph.Tests.Editor
{
    public sealed class UnsafePagedListTests
    {
        [Test]
        public void Constructor_AllocatesFirstPage()
        {
            var list = new UnsafePagedList<int>(pageSize: 4, allocator: Allocator.Persistent);
            try
            {
                Assert.IsTrue(list.IsCreated);
                Assert.AreEqual(4, list.PageSize);
                Assert.AreEqual(1, list.PageCount);
                Assert.AreEqual(0, list.Length);
                Assert.AreEqual(4, list.Capacity);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void Add_GrowsAcrossPages()
        {
            var list = new UnsafePagedList<int>(pageSize: 3, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    list.Add(i);
                }

                Assert.AreEqual(10, list.Length);
                Assert.AreEqual(4, list.PageCount);
                for (int i = 0; i < 10; i++)
                {
                    Assert.AreEqual(i, list[i]);
                }
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void AddressesRemainStableWhenGrowing()
        {
            var list = new UnsafePagedList<int>(pageSize: 4, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    list.Add(100 + i);
                }

                var addr0 = list.GetAddress(0);
                var addr3 = list.GetAddress(3);

                for (int i = 4; i < 20; i++)
                {
                    list.Add(100 + i);
                }

                Assert.AreEqual(addr0, list.GetAddress(0));
                Assert.AreEqual(addr3, list.GetAddress(3));
                Assert.AreEqual(20, list.Length);
                Assert.GreaterOrEqual(list.PageCount, 5);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void IndexerAndElementAt_SetValue()
        {
            var list = new UnsafePagedList<int>(pageSize: 4, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    list.Add(i);
                }

                list[2] = 42;
                Assert.AreEqual(42, list[2]);

                ref var slot = ref list.ElementAt(5);
                slot = 77;
                Assert.AreEqual(77, list[5]);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void GetAddress_UsesElementStrideWithinPage()
        {
            var list = new UnsafePagedList<int>(pageSize: 8, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    list.Add(i);
                }

                var addr0 = list.GetAddress(0);
                var addr1 = list.GetAddress(1);
                var addr2 = list.GetAddress(2);
                var stride = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<int>();

                Assert.AreEqual(stride, addr1 - addr0);
                Assert.AreEqual(stride, addr2 - addr1);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void Resize_GrowWithClearMemory_InitializesDefaults()
        {
            var list = new UnsafePagedList<int>(pageSize: 3, allocator: Allocator.Persistent);
            try
            {
                list.Add(10);
                list.Add(20);

                list.Resize(7, NativeArrayOptions.ClearMemory);

                Assert.AreEqual(7, list.Length);
                Assert.AreEqual(3, list.PageSize);
                Assert.AreEqual(3, list.PageCount);
                Assert.AreEqual(0, list[2]);
                Assert.AreEqual(0, list[6]);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void Resize_Shrink_DropsPagesAndKeepsValues()
        {
            var list = new UnsafePagedList<int>(pageSize: 2, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    list.Add(100 + i);
                }

                Assert.AreEqual(3, list.PageCount);
                list.Resize(1);

                Assert.AreEqual(1, list.Length);
                Assert.AreEqual(1, list.PageCount);
                Assert.AreEqual(100, list[0]);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void Clear_KeepsSinglePage()
        {
            var list = new UnsafePagedList<int>(pageSize: 2, allocator: Allocator.Persistent);
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    list.Add(i);
                }

                Assert.GreaterOrEqual(list.PageCount, 3);
                list.Clear();

                Assert.AreEqual(0, list.Length);
                Assert.AreEqual(1, list.PageCount);
                Assert.AreEqual(2, list.Capacity);
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void OutOfRange_Throws()
        {
            var list = new UnsafePagedList<int>(pageSize: 4, allocator: Allocator.Persistent);
            try
            {
                Assert.Throws<IndexOutOfRangeException>(() => { var _ = list[0]; });
                Assert.Throws<IndexOutOfRangeException>(() => list.ElementAt(0));

                list.Add(1);
                Assert.Throws<IndexOutOfRangeException>(() => { var _ = list[2]; });
                Assert.Throws<IndexOutOfRangeException>(() => list.GetAddress(-1));
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void Dispose_ResetsState()
        {
            var list = new UnsafePagedList<int>(pageSize: 4, allocator: Allocator.Persistent);

            list.Add(1);
            list.Dispose();

            Assert.IsFalse(list.IsCreated);
            Assert.AreEqual(0, list.PageCount);
            Assert.AreEqual(0, list.Length);
        }
    }
}
