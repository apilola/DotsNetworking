namespace DotsNetworking.SceneGraph
{
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;

    public struct Section
    {
        public int MortonCode; //3D morton code of section within world
        
        // Sparse Data Storage
        public BlobArray<Chunk> Chunks;
        
        // Acceleration Structure: O(1) Lookup
        // Maps 15-bit Morton Code -> Index in 'Chunks' array.
        // Value is -1 if chunk does not exist.
        // Dimensions: 32x32x32 = 32768 entries. 
        // Size: ~64KB per section.
        public BlobArray<short> ChunkLookup;
    }

    public struct Chunk
    {
        public ushort MortonCode; // 15-bit Morton code (3D) within section
        public BlobArray<Node> Nodes; // Dense array (256 nodes), indexed by local Morton code

        // world origin of chunk can be extracted from section morton code + chunk morton code
    }

    public struct Node
    {
        // MortonCode is implicit by array index (0..255)
        
        public float Y; // height of this node
        
        // 12-way movement support (6 primary + 6 secondary)
        // See MovementFlags in SceneGraphTypes.cs for layout
        public MovementFlags ExitMask; 
    }
}


