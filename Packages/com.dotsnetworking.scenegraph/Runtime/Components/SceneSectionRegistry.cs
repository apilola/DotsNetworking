using DotsNetworking.SceneGraph.Collections;
using DotsNetworking.SceneGraph.Components;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace DotsNetworking.SceneGraph
{
    /// <summary>
    /// Runtime registry for per-section data keyed by SectionAddress.
    /// </summary>
    [NativeContainer]
    public struct SceneSectionRegistry : IComponentData
    {
        public NativeRegistry<SectionAddress> Registry;
    }

    /// <summary>
    /// Cleanup marker on the blob owner so we can detach it from the runtime section when it unloads.
    /// </summary>
    public struct SceneSectionBlobRegistered : ICleanupComponentData
    {
        public SectionAddress Address;
    }
}
