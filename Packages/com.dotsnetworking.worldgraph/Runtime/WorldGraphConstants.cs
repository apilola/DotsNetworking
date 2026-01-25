using Unity.Entities;
using Unity.Mathematics;

namespace DotsNetworking.WorldGraph
{
    public static class WorldGraphConstants
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

        // Region Dimensions (measured in Chunks)
        public const int RegionSizeChunksX = 32;
        public const int RegionSizeChunksY = 4;
        public const int RegionSizeChunksZ = 32;
        public const int ChunksPerRegion = RegionSizeChunksX * RegionSizeChunksY * RegionSizeChunksZ; // 4096

        public const float RegionSizeX = RegionSizeChunksX * ChunkSizeX; 
        public const float RegionSizeY = RegionSizeChunksY * ChunkHeight; 
        public const float RegionSizeZ = RegionSizeChunksZ * ChunkSizeZ; 

        // NodeId Layout (64-bit)
        // [ WorldId: 16 ] [ RegionId: 24 ] [ ChunkMorton: 15 ] [ NodeIndex: 8 ] [ Reserved: 1 ]
        public const int NodeIdBits_WorldId = 16;
        public const int NodeIdBits_RegionId = 24;
        public const int NodeIdBits_ChunkMorton = 15;
        public const int NodeIdBits_NodeIndex = 8;
        public const int NodeIdBits_Reserved = 1;

        public const int NodeIdShift_WorldId = NodeIdBits_RegionId + NodeIdBits_ChunkMorton + NodeIdBits_NodeIndex + NodeIdBits_Reserved;
        public const int NodeIdShift_RegionId = NodeIdBits_ChunkMorton + NodeIdBits_NodeIndex + NodeIdBits_Reserved;
        public const int NodeIdShift_ChunkMorton = NodeIdBits_NodeIndex + NodeIdBits_Reserved;
        public const int NodeIdShift_NodeIndex = NodeIdBits_Reserved;
        
        // Masks
        public const ulong NodeIdMask_WorldId = ((1UL << NodeIdBits_WorldId) - 1) << NodeIdShift_WorldId;
        public const ulong NodeIdMask_RegionId = ((1UL << NodeIdBits_RegionId) - 1) << NodeIdShift_RegionId;
        public const ulong NodeIdMask_ChunkMorton = ((1UL << NodeIdBits_ChunkMorton) - 1) << NodeIdShift_ChunkMorton;
        public const ulong NodeIdMask_NodeIndex = ((1UL << NodeIdBits_NodeIndex) - 1) << NodeIdShift_NodeIndex;

        // Navigation Constraints
        public const float MaxSlopeHeight = .25f; // Max Y difference for a valid step

    }
}
