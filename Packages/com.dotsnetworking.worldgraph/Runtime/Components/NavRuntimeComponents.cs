using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace DotsNetworking.WorldGraph
{
    public struct NavRuntime : IComponentData
    {
        // Structural mapping (grows): nodeId -> occId
        public NativeParallelHashMap<ulong, int> NodeToOccId;

        // Runtime per-node atomic word (stable). Indexed by occId.
        // Packed fields (suggestion):
        // - bits 0..15  : occupancy count (optional if needed for movement rules)
        // - bits 16..23 : dynamic blocked directions mask (8 bits)
        // - bit 31      : lock bit (optional)
        public NativeList<int> OccAtomicWord;

        // Static per-node info not stored in blobs (optional). If static walkmask is in blob,
        // you may not need this.
        public NativeList<uint> Reserved;

        // Structural add queue for newly referenced nodes
        public NativeQueue<ulong> NewNodeQ;
    }

    public struct OccupancyRuntime : IComponentData
    {
        // Client: rebuilt each frame
        public NativeParallelMultiHashMap<ulong, Entity> DynamicOccupants;

        // Region-load-time static occupants (optional)
        public NativeParallelMultiHashMap<ulong, Entity> StaticOccupants;

        // Optional: per-node occupancy counts (atomic) for NPC avoidance if needed.
        // If you keep counts here, use stable storage indexed by nodeRuntimeIndex or occId.
        public NativeList<int> NodeOccCounts;
    }
}
