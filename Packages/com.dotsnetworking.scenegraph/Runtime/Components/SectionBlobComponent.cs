using Unity.Entities;

namespace DotsNetworking.SceneGraph
{
    /// <summary>
    /// Component that holds a reference to the navigation blob asset for a section.
    /// Attached to entities that represent navigable sections of the scene graph.
    /// </summary>
    public struct SectionBlobComponent : IComponentData
    {
        /// <summary>
        /// The blob asset reference containing the navigation data for this section.
        /// </summary>
        public BlobAssetReference<Section> BlobRef;
        
        /// <summary>
        /// The section address identifying this section's location in the scene graph.
        /// </summary>
        public SectionAddress Address;
    }
}
