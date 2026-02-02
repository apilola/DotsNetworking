using BovineLabs.Core.Extensions;
using DotsNetworking.SceneGraph.Components;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace DotsNetworking.SceneGraph.Authoring
{
    /// <summary>
    /// Authoring component for a navigation section.
    /// Created as a child of SceneGraphBakeAuthoring during the baking process.
    /// Each instance represents a single navigation section with its blob asset.
    /// </summary>
    public sealed class SectionAuthoring : MonoBehaviour
    {
        [Tooltip("The section address identifying this section's location in the scene graph.")]
        [SerializeField] private SectionAddress m_Address;
        
        [Tooltip("The blob asset containing the navigation data for this section.")]
        [SerializeField] private LazyLoadReference<BlobAssetHandler> m_BlobAsset;

        public SectionAddress Address => m_Address;
        public LazyLoadReference<BlobAssetHandler> BlobAsset => m_BlobAsset;

        public void Initialize(SectionAddress address, BlobAssetHandler blobAsset)
        {
            m_Address = address;
            m_BlobAsset = new LazyLoadReference<BlobAssetHandler> { asset = blobAsset };
        }

        /// <summary>
        /// Baker for SectionAuthoring that loads the blob from a BlobAssetHandler and registers it
        /// with proper hash-based deduplication.
        /// </summary>
        public class SectionBaker : Baker<SectionAuthoring>
        {

            public override void Bake(SectionAuthoring authoring)
            {
                if (!authoring.BlobAsset.isSet)
                {
                    return;
                }

                var asset = authoring.BlobAsset.asset;
                if (asset == null)
                {
                    return;
                }

                DependsOn(asset);
                if (!asset.IsCreated)
                {
                    Debug.LogError($"Blob asset for section at address {authoring.Address} is not created.");
                    return;
                }

                var hash = asset.GetHash();
                BlobAssetReference<Section> blob;
                // Try to get an existing blob with the same hash
                if (!TryGetBlobAssetReference(hash, out BlobAssetReference<Section> existingBlob))
                {
                    unsafe
                    {
                        blob = BlobAssetReference<Section>.Create(asset.GetUnsafePtr(),(int) asset.DataSize);
                    }
                    AddBlobAssetWithCustomHash(ref blob, hash);
                }
                else
                {
                    blob = existingBlob;
                }

                // Create the entity and add the component
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, authoring.Address);
                AddComponent(entity, new Components.SectionBlob
                {
                    BlobRef = blob
                });
            }
        }
    }
}
