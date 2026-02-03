using BovineLabs.Core.SingletonCollection;
using DotsNetworking.SceneGraph.Collections;
using DotsNetworking.SceneGraph.Components;
using Unity.Collections;
using Unity.Entities;

namespace DotsNetworking.SceneGraph
{
    public static class SceneGraphAPI
    {
        public static SectionAddress ToSectionAddress(in SceneSection sceneSection)
        {
            return new SectionAddress(sceneSection.SceneGUID, (uint)sceneSection.Section);
        }

        public static NativeQueue<SectionAddress> CreateLoadQueue(SceneGraphLoadRequests requests)
        {
            return requests.CreateQueue<SceneGraphLoadRequests, SectionAddress>();
        }

        public static NativeQueue<SectionAddress>.ParallelWriter CreateLoadQueueWriter(SceneGraphLoadRequests requests)
        {
            return requests.CreateQueue<SceneGraphLoadRequests, SectionAddress>().AsParallelWriter();
        }

        public static void EnqueueLoad(ref NativeQueue<SectionAddress> queue, SectionAddress address)
        {
            queue.Enqueue(address);
        }

        public static void EnqueueLoad(NativeQueue<SectionAddress>.ParallelWriter writer, SectionAddress address)
        {
            writer.Enqueue(address);
        }

        public static bool TryDequeueLoad(ref NativeQueue<SectionAddress> queue, out SectionAddress address)
        {
            return queue.TryDequeue(out address);
        }

        public static NativeQueue<SectionAddress> CreateUnloadQueue(SceneGraphUnloadRequests requests)
        {
            return requests.CreateQueue<SceneGraphUnloadRequests, SectionAddress>();
        }

        public static NativeQueue<SectionAddress>.ParallelWriter CreateUnloadQueueWriter(SceneGraphUnloadRequests requests)
        {
            return requests.CreateQueue<SceneGraphUnloadRequests, SectionAddress>().AsParallelWriter();
        }

        public static void EnqueueUnload(ref NativeQueue<SectionAddress> queue, SectionAddress address)
        {
            queue.Enqueue(address);
        }

        public static void EnqueueUnload(NativeQueue<SectionAddress>.ParallelWriter writer, SectionAddress address)
        {
            writer.Enqueue(address);
        }

        public static bool TryDequeueUnload(ref NativeQueue<SectionAddress> queue, out SectionAddress address)
        {
            return queue.TryDequeue(out address);
        }

        public static bool TryGetSectionBlobRef(
            in NativeRegistry<SectionAddress> registry,
            SectionAddress address,
            out BlobAssetReference<Section> blobRef)
        {
            blobRef = default;
            if (!registry.IsCreated)
                return false;

            if (!registry.TryGetIndex(address, out _))
                return false;

            using var handle = registry.AcquireRead<BlobAssetReference<Section>>(address);
            if (!handle.IsAccessible)
                return false;

            blobRef = handle.Value;
            return true;
        }
    }
}
