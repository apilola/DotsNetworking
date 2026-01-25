using Unity.Entities;
using Unity.Mathematics;

namespace DotsNetworking.WorldGraph
{
    public enum RegionState : byte
    {
        NeverExisted = 0,
        NotLoaded = 1,
        RequestedLoad = 2,
        Loading = 3,
        Loaded = 4,
        RequestedUnload = 5,
        Unloading = 6,
    }

    [System.Flags]
    public enum MovementFlags : ulong
    {
        None = 0,

        // --- Existence (0-11) ---
        // Arranged in Clockwise order starting from North
        N   = 1UL << 0,
        NE  = 1UL << 1,
        EN  = 1UL << 2, // East-North (Secondary)
        E   = 1UL << 3,
        ES  = 1UL << 4, // East-South (Secondary)
        SE  = 1UL << 5,
        S   = 1UL << 6,
        SW  = 1UL << 7,
        WS  = 1UL << 8, // West-South (Secondary)
        W   = 1UL << 9,
        WN  = 1UL << 10, // West-North (Secondary)
        NW  = 1UL << 11,

        // --- Verticality (12-35) ---
        // 2 bits per direction. 
        // 01 (1) = Up, 10 (2) = Down.

        N_Up     = 1UL << 12,
        N_Down   = 1UL << 13,

        NE_Up    = 1UL << 14,
        NE_Down  = 1UL << 15,

        EN_Up    = 1UL << 16,
        EN_Down  = 1UL << 17,

        E_Up     = 1UL << 18,
        E_Down   = 1UL << 19,

        ES_Up    = 1UL << 20,
        ES_Down  = 1UL << 21,

        SE_Up    = 1UL << 22,
        SE_Down  = 1UL << 23,

        // Secondary Verticality
        S_Up     = 1UL << 24,
        S_Down   = 1UL << 25,

        SW_Up    = 1UL << 26,
        SW_Down  = 1UL << 27,

        WS_Up    = 1UL << 28,
        WS_Down  = 1UL << 29,

        W_Up     = 1UL << 30,
        W_Down   = 1UL << 31,

        WN_Up    = 1UL << 32,
        WN_Down  = 1UL << 33,

        NW_Up    = 1UL << 34,
        NW_Down  = 1UL << 35,
        
        // --- Status ---
        Unreachable = 1UL << 63
    }

    public struct RegionEntry
    {
        public RegionState State;

        // Static nav data (immutable blob): only valid when State == Loaded.
        public BlobAssetReference<Region> NavBlob;

        // Pin/lock index for region-level safety (optional). If per-region pins are used,
        // this index points into a stable array/list of atomic pin words.
        public int RegionPinId;

        // Runtime slice addressing (NavRuntime / OccupancyRuntime). These are offsets into
        // flat arrays sized/allocated by WorldGraphSystem at structural sync points.
        public int BaseNodeRuntimeIndex; // base offset for nodes in this region
        public int NodeRuntimeCount;     // number of node runtime slots for this region

        // Optional for id reuse / debug
        public uint Generation;
    }

    public readonly struct NodeId : System.IEquatable<NodeId>
    {
        public readonly ulong Value;

        public NodeId(ulong value)
        {
            Value = value;
        }

        public NodeId(int worldId, int regionId, int chunkMorton, int nodeIndex)
        {
            Value = ((ulong)worldId << WorldGraphConstants.NodeIdShift_WorldId) |
                    ((ulong)regionId << WorldGraphConstants.NodeIdShift_RegionId) |
                    ((ulong)chunkMorton << WorldGraphConstants.NodeIdShift_ChunkMorton) |
                    ((ulong)nodeIndex << WorldGraphConstants.NodeIdShift_NodeIndex);
        }

        public int WorldId => (int)((Value >> WorldGraphConstants.NodeIdShift_WorldId) & ((1UL << WorldGraphConstants.NodeIdBits_WorldId) - 1));
        public int RegionId => (int)((Value >> WorldGraphConstants.NodeIdShift_RegionId) & ((1UL << WorldGraphConstants.NodeIdBits_RegionId) - 1));
        public int ChunkMorton => (int)((Value >> WorldGraphConstants.NodeIdShift_ChunkMorton) & ((1UL << WorldGraphConstants.NodeIdBits_ChunkMorton) - 1));
        public int NodeIndex => (int)((Value >> WorldGraphConstants.NodeIdShift_NodeIndex) & ((1UL << WorldGraphConstants.NodeIdBits_NodeIndex) - 1));

        public bool Equals(NodeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is NodeId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(NodeId left, NodeId right) => left.Value == right.Value;
        public static bool operator !=(NodeId left, NodeId right) => left.Value != right.Value;
        public override string ToString() => $"NodeId(W{WorldId}:R{RegionId}:{ChunkMorton}:{NodeIndex})";
    }
}
