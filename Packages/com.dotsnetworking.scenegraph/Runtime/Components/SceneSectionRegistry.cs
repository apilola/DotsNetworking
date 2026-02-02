using DotsNetworking.SceneGraph.Components;
using Unity.Collections;
using Unity.Entities;

namespace DotsNetworking.SceneGraph
{
    /// <summary>
    /// Runtime registry for mapping section addresses to their entities.
    /// </summary>
    public struct SceneSectionRegistry : IComponentData
    {
        public NativeParallelHashMap<SectionAddress, Entity> Map;
    }

    /// <summary>
    /// Cleanup marker on the blob owner so we can detach it from the runtime section when it unloads.
    /// </summary>
    public struct SceneSectionBlobRegistered : ICleanupComponentData
    {
        public SectionAddress Address;
        public Entity RegistryEntity;
    }
}
