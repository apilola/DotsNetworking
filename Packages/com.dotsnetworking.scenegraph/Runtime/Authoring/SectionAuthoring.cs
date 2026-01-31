using BovineLabs.Core.Internal;
using System;
using System.Reflection;
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
        [SerializeField] private LazyLoadReference<TextAsset> m_BlobAsset;

        public SectionAddress Address => m_Address;
        public LazyLoadReference<TextAsset> BlobAsset => m_BlobAsset;

        public void Initialize(SectionAddress address, TextAsset blobAsset)
        {
            m_Address = address;
            m_BlobAsset = new LazyLoadReference<TextAsset> { asset = blobAsset };
        }

        /// <summary>
        /// Baker for SectionAuthoring that loads the blob from a TextAsset and registers it
        /// with proper hash-based deduplication.
        /// </summary>
        public class SectionBaker : Baker<SectionAuthoring>
        {

            // Reflection MethodInfo (Delegate creation proved unstable across Unity versions/platforms)
            private static readonly MethodInfo _tryReadInplaceMethod;

            static SectionBaker()
            {
                _tryReadInplaceMethod = typeof(BlobAssetReference<Section>)
                    .GetMethod("TryReadInplace", BindingFlags.NonPublic | BindingFlags.Static);

                if (_tryReadInplaceMethod == null)
                {
                    Debug.LogError(
                        $"Could not find BlobAssetReference<{typeof(Section).Name}>.TryReadInplace via reflection.");
                }
            }

            public override void Bake(SectionAuthoring authoring)
            {
                if (!authoring.BlobAsset.isSet)
                {
                    return;
                }

                if(!TryLoadBlob(authoring.BlobAsset.asset, out BlobAssetReference<Section> blob))
                {
                    return;
                }

                DependsOn(authoring.BlobAsset.asset);
                if (!blob.IsCreated)
                {
                    return;
                }

                // Compute a hash for deduplication
                var hash = ComputeBlobHash(blob);
                // Try to get an existing blob with the same hash
                if (!TryGetBlobAssetReference(hash, out BlobAssetReference<Section> existingBlob))
                {
                    unsafe
                    {
                        blob = BlobAssetReference<Section>.Create(blob.GetUnsafePtr(), (int) authoring.BlobAsset.asset.dataSize);
                    }
                    // Register this blob with the computed hash for deduplication
                    AddBlobAssetWithCustomHash(ref blob, hash);
                }
                else
                {
                    blob = existingBlob;
                }
                // Create the entity and add the component
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new SectionBlobComponent
                {
                    Address = authoring.Address,
                    BlobRef = blob
                });
            }

            /// <summary>
            /// Computes a hash from a BlobAssetReference using BovineLabs' internal hash extraction.
            /// </summary>
            private static unsafe Unity.Entities.Hash128 ComputeBlobHash(BlobAssetReference<Section> blobRef)
            {
                // Use BovineLabs' internal method to get the blob hash
                long lhash = BlobAssetReferenceInternal.GetHash(blobRef);

                // Convert the long hash to a Hash128 (zero-extend)
                Unity.Entities.Hash128 hash = default;
                UnsafeUtility.MemCpy(&hash, &lhash, sizeof(long));
                return hash;
            }

            private unsafe bool TryLoadBlob(TextAsset asset, out BlobAssetReference<Section> blobRef)
            {
                blobRef = default;

                if (asset == null) return false;
                if (_tryReadInplaceMethod == null) return false;

                var nativeData = asset.GetData<byte>();
                if (!nativeData.IsCreated || nativeData.Length == 0) return false;

                var ptr = (IntPtr)nativeData.GetUnsafeReadOnlyPtr();

                object[] parameters =
                {
                    ptr,
                    (long)nativeData.Length,
                    0, //Replace me with const version
                    default(BlobAssetReference<Section>),
                    0
                };

                try
                {
                    bool success = (bool)_tryReadInplaceMethod.Invoke(null, parameters);
                    if (success)
                        blobRef = (BlobAssetReference<Section>)parameters[3];

                    return success;
                }
                catch (Exception e)
                {
                    Debug.LogError($"TryReadInplace invoke failed: {e.Message}");
                    return false;
                }
            }
        }
    }
}
