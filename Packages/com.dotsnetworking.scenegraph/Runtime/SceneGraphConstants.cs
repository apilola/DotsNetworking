using Unity.Mathematics;

namespace DotsNetworking.SceneGraph
{
    public static class SceneGraphConstants
    {
        // Grid Dimensions
        public const float NodeSize = 0.5f; // X-axis spacing
        public const float NodeSpacingZ = NodeSize * 0.86602540378f; // Z-axis spacing (Isometric sqrt(3)/2)
        
        // Chunk Dimensions (measured in Nodes)
        public const int ChunkSizeNodesX = 16;
        public const int ChunkSizeNodesZ = 16;
        public const int NodesPerChunk = ChunkSizeNodesX * ChunkSizeNodesZ; // 256

        // Derived World Sizes
        public const float ChunkSizeX = ChunkSizeNodesX * NodeSize; 
        public const float ChunkSizeZ = ChunkSizeNodesZ * NodeSpacingZ; 
        public const float ChunkHeight = 4.0f; // Assumed cubic for mapping

        // Section Dimensions (measured in Chunks)
        public const int SectionSizeChunksX = 32;
        public const int SectionSizeChunksY = 4;
        public const int SectionSizeChunksZ = 32;
        public const int ChunksPerSection = SectionSizeChunksX * SectionSizeChunksY * SectionSizeChunksZ; // 4096

        public const float SectionSizeX = SectionSizeChunksX * ChunkSizeX; 
        public const float SectionSizeY = SectionSizeChunksY * ChunkHeight; 
        public const float SectionSizeZ = SectionSizeChunksZ * ChunkSizeZ; 

        // SectionId Layout (30-bit morton code derived from section grid)
        // 10 bits per axis => range [-512..511] after bias.
        public const int SectionCoordBits = 10;
        public const int SectionCoordBias = 1 << (SectionCoordBits - 1);

        // Navigation Constraints
        public const float MaxSlopeHeight = .25f; // Max Y difference for a valid step

    }
}



