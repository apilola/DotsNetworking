using BovineLabs.Core.SingletonCollection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace DotsNetworking.SceneGraph.Components
{
    public struct SceneGraphLoadRequests : ISingletonCollection<NativeQueue<SectionAddress>>
    {
        unsafe UnsafeList<NativeQueue<SectionAddress>>* ISingletonCollection<NativeQueue<SectionAddress>>.Collections { get; set; }
        Allocator ISingletonCollection<NativeQueue<SectionAddress>>.Allocator { get; set; }
    }

    public struct SceneGraphUnloadRequests : ISingletonCollection<NativeQueue<SectionAddress>>
    {
        unsafe UnsafeList<NativeQueue<SectionAddress>>* ISingletonCollection<NativeQueue<SectionAddress>>.Collections { get; set; }
        Allocator ISingletonCollection<NativeQueue<SectionAddress>>.Allocator { get; set; }
    }
}
