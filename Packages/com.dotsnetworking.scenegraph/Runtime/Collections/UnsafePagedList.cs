using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DotsNetworking.SceneGraph.Collections
{
    public unsafe struct UnsafePagedList<T>
        where T : unmanaged
    {
        private UnsafeList<UnsafeList<T>> pages;
        private int pageSize;
        private AllocatorManager.AllocatorHandle allocator;

        public UnsafePagedList(int pageSize, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.UninitializedMemory)
        {
            if (pageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");

            this.pageSize = pageSize;
            this.allocator = allocator;

            pages = new UnsafeList<UnsafeList<T>>(1, allocator, NativeArrayOptions.ClearMemory);
            AddPage(options);
        }

        public bool IsCreated => pages.IsCreated;

        public int Length
        {
            get
            {
                if (!pages.IsCreated || pages.Length == 0)
                    return 0;

                var last = pages[pages.Length - 1];
                return ((pages.Length - 1) * pageSize) + last.Length;
            }
            set => Resize(value);
        }

        public int PageSize => pageSize;

        public int PageCount => pages.IsCreated ? pages.Length : 0;

        public int Capacity => PageCount * pageSize;

        public T this[int index]
        {
            get
            {
                CheckIndexInRange(index, Length);
                return ElementAt(index);
            }
            set
            {
                CheckIndexInRange(index, Length);
                ElementAt(index) = value;
            }
        }

        public ref T ElementAt(int index)
        {
            CheckIndexInRange(index, Length);
            var pageIndex = index / pageSize;
            var offset = index - pageIndex * pageSize;
            ref var page = ref pages.ElementAt(pageIndex);
            return ref page.ElementAt(offset);
        }

        public T* GetPtr(int index)
        {
            CheckIndexInRange(index, Length);
            var pageIndex = index / pageSize;
            var offset = index - pageIndex * pageSize;
            ref var page = ref pages.ElementAt(pageIndex);
            return page.Ptr + offset;
        }

        public long GetAddress(int index)
        {
            return (long)GetPtr(index);
        }

        public void Add(in T value)
        {
            EnsureCapacity(Length + 1);
            var index = Length;
            var pageIndex = index / pageSize;
            ref var page = ref pages.ElementAt(pageIndex);
            page.Add(value);
        }

        public void Clear()
        {
            if (!pages.IsCreated || pages.Length == 0)
                return;

            for (int i = pages.Length - 1; i >= 1; i--)
            {
                pages[i].Dispose();
            }

            pages.Length = 1;
            ref var first = ref pages.ElementAt(0);
            first.Length = 0;
        }

        public void Resize(int newLength, NativeArrayOptions options = NativeArrayOptions.UninitializedMemory)
        {
            if (newLength < 0)
                throw new ArgumentOutOfRangeException(nameof(newLength));

            var oldLength = Length;
            if (newLength == oldLength)
                return;

            if (newLength > oldLength)
            {
                EnsureCapacity(newLength);
                SetLengthInternal(newLength);
                if (options == NativeArrayOptions.ClearMemory)
                {
                    for (int i = oldLength; i < newLength; i++)
                    {
                        ElementAt(i) = default;
                    }
                }
                return;
            }

            SetLengthInternal(newLength);
        }

        public void Dispose()
        {
            if (!IsCreated)
                return;

            for (int i = pages.Length - 1; i >= 0; i--)
            {
                pages[i].Dispose();
            }

            pages.Dispose();
            pages = default;
            pageSize = 0;
            allocator = default;
        }

        private void EnsureCapacity(int requiredLength)
        {
            int requiredPages = math.max(1, (requiredLength + pageSize - 1) / pageSize);
            while (pages.Length < requiredPages)
            {
                AddPage(NativeArrayOptions.UninitializedMemory);
            }
        }

        private void AddPage(NativeArrayOptions options)
        {
            var page = new UnsafeList<T>(pageSize, allocator, options);
            pages.Add(page);
        }

        private void SetLengthInternal(int newLength)
        {
            int requiredPages = math.max(1, (newLength + pageSize - 1) / pageSize);
            for (int i = pages.Length - 1; i >= requiredPages; i--)
            {
                pages[i].Dispose();
            }

            pages.Length = requiredPages;
            for (int i = 0; i < pages.Length - 1; i++)
            {
                ref var page = ref pages.ElementAt(i);
                page.Length = pageSize;
            }

            ref var last = ref pages.ElementAt(pages.Length - 1);
            var lastLength = newLength - ((pages.Length - 1) * pageSize);
            last.Length = lastLength;
        }

        private static void CheckIndexInRange(int index, int length)
        {
            if ((uint)index >= (uint)length)
                throw new IndexOutOfRangeException($"Index {index} is out of range of '{length}'.");
        }
    }
}
