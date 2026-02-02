using Unity.Entities;
using Unity.Mathematics;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph
{

    public readonly struct ChunkAddress : System.IEquatable<ChunkAddress>
    {
        public readonly EntitiesHash128 SceneGuid;
        public readonly uint SectionId;
        public readonly ushort ChunkMorton;

        public ChunkAddress(EntitiesHash128 sceneGuid, uint sectionId, ushort chunkMorton)
        {
            SceneGuid = sceneGuid;
            SectionId = sectionId;
            ChunkMorton = chunkMorton;
        }

        public bool Equals(ChunkAddress other)
        {
            return SceneGuid.Equals(other.SceneGuid) &&
                   SectionId == other.SectionId &&
                   ChunkMorton == other.ChunkMorton;
        }

        public override bool Equals(object obj) => obj is ChunkAddress other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SceneGuid.GetHashCode();
                hash = (hash * 397) ^ (int)SectionId;
                hash = (hash * 397) ^ ChunkMorton;
                return hash;
            }
        }

        public static bool operator ==(ChunkAddress left, ChunkAddress right) => left.Equals(right);
        public static bool operator !=(ChunkAddress left, ChunkAddress right) => !left.Equals(right);

        public override string ToString() => $"ChunkAddress(Scene={SceneGuid};R={SectionId};C={ChunkMorton})";
    }

    public enum SectionState : byte
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

    public struct SectionEntry
    {
        public SectionState State;

        // Static nav data (immutable blob): only valid when State == Loaded.
        public BlobAssetReference<Section> NavBlob;

        // Pin/lock index for section-level safety (optional). If per-section pins are used,
        // this index points into a stable array/list of atomic pin words.
        public int SectionPinId;

        // Runtime slice addressing (NavRuntime / OccupancyRuntime). These are offsets into
        // flat arrays sized/allocated by SceneGraphSystem at structural sync points.
        public int BaseNodeRuntimeIndex; // base offset for nodes in this section
        public int NodeRuntimeCount;     // number of node runtime slots for this section

        // Optional for id reuse / debug
        public uint Generation;
    }

    public readonly struct NodeAddress : System.IEquatable<NodeAddress>
    {
        public readonly EntitiesHash128 SceneGuid;
        public readonly uint SectionId;
        public readonly ushort ChunkMorton;
        public readonly byte NodeIndex;

        public NodeAddress(EntitiesHash128 sceneGuid, uint sectionId, ushort chunkMorton, byte nodeIndex)
        {
            SceneGuid = sceneGuid;
            SectionId = sectionId;
            ChunkMorton = chunkMorton;
            NodeIndex = nodeIndex;
        }

        public bool Equals(NodeAddress other)
        {
            return SceneGuid.Equals(other.SceneGuid) &&
                   SectionId == other.SectionId &&
                   ChunkMorton == other.ChunkMorton &&
                   NodeIndex == other.NodeIndex;
        }

        public override bool Equals(object obj) => obj is NodeAddress other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SceneGuid.GetHashCode();
                hash = (hash * 397) ^ (int)SectionId;
                hash = (hash * 397) ^ ChunkMorton;
                hash = (hash * 397) ^ NodeIndex;
                return hash;
            }
        }

        public static bool operator ==(NodeAddress left, NodeAddress right) => left.Equals(right);
        public static bool operator !=(NodeAddress left, NodeAddress right) => !left.Equals(right);

        public override string ToString()
        {
            return $"NodeId(Scene={SceneGuid};R={SectionId};C={ChunkMorton};N={NodeIndex})";
        }
    }
}



