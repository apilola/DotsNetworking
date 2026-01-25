using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace DotsNetworking.WorldGraph
{
    public struct WorldGraph : IComponentData
    {
        // Region addressing
        public NativeParallelHashMap<int3, int> RegionIdByKey; // regionKey -> regionId

        // Region registry
        public NativeList<RegionEntry> RegionsById;            // regionId -> RegionEntry

        // Region-level pin words (optional) (lock bit + pin count). Stable storage.
        public NativeList<int> RegionPinWords;

        // Optional: free-list for region id reuse
        public NativeQueue<int> FreeRegionIds;
    }

    public struct WorldGraphRequests : IComponentData
    {
        public NativeQueue<int3> RequestLoadQ;   // regionKey
        public NativeQueue<int3> RequestUnloadQ; // regionKey

        // Optional dedupe sets
        public NativeParallelHashSet<int3> PendingLoad;
        public NativeParallelHashSet<int3> PendingUnload;
    }

    public enum UnloadDeferReason : byte
    {
        StillPinned = 1,
        StillLoading = 2,
        Locked = 3,
    }

    public struct WorldGraphFeedback : IComponentData
    {
        public NativeQueue<int3> UnloadDeferredQ; // regionKey (or regionId)
        // Optionally include reason in a parallel queue of the same length:
        public NativeQueue<UnloadDeferReason> UnloadDeferredReasonQ;
    }
}
