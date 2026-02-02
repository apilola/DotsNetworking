using Unity.Entities;

namespace DotsNetworking.SceneGraph.Components
{
    /// <summary>
    /// Component that holds a reference to the navigation blob asset for a section.
    /// Attached to entities that represent navigable sections of the scene graph.
    /// </summary>
    public struct SectionBlob : IComponentData
    {
        /// <summary>
        /// The blob asset reference containing the navigation data for this section.
        /// </summary>
        public BlobAssetReference<Section> BlobRef;
    }
}
