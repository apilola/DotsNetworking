using Unity.Entities;
using Unity.Mathematics;
using DotsNetworking.SceneGraph;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph.Utils
{
    public static class SceneGraphMath
    {
        /// <summary>
        /// Converts a world position to a Section, Chunk, and Local Node coordinate.
        /// Handles Isometric/Staggered Lattice spacing by finding the nearest valid lattice point.
        /// </summary>
        public static void WorldToGraph(float3 worldPos, out int3 sectionKey, out int3 chunkIdxInSection, out int2 nodeLocalIdx, out float3 nodeOffset)
        {
            // 1. Find Nearest Lattice Point (Global Grid X/Z)
            // Staggered Grid Analysis:
            // Lattice rows are at Z = row * NodeSpacingZ.
            // Nodes in a row are at X = col * NodeSize + Offset(row).
            // To find the nearest node, we check the two nearest rows (Floor and Ceil of Z)
            // and find the closest node in X for each, then pick the winner.

            float zRaw = worldPos.z / SceneGraphConstants.NodeSpacingZ;
            int rowLower = (int)math.floor(zRaw);
            int rowUpper = rowLower + 1;

            // Candidate 1: Lower Row
            float offsetLower = (rowLower & 1) != 0 ? SceneGraphConstants.NodeSize * 0.5f : 0f;
            int colLower = (int)math.round((worldPos.x - offsetLower) / SceneGraphConstants.NodeSize);
            
            float xPosLower = colLower * SceneGraphConstants.NodeSize + offsetLower;
            float zPosLower = rowLower * SceneGraphConstants.NodeSpacingZ;

            // Candidate 2: Upper Row
            float offsetUpper = (rowUpper & 1) != 0 ? SceneGraphConstants.NodeSize * 0.5f : 0f;
            int colUpper = (int)math.round((worldPos.x - offsetUpper) / SceneGraphConstants.NodeSize);

            float xPosUpper = colUpper * SceneGraphConstants.NodeSize + offsetUpper;
            float zPosUpper = rowUpper * SceneGraphConstants.NodeSpacingZ;

            // Distance Check (Squared, 2D)
            float distSqLower = math.distancesq(new float2(worldPos.x, worldPos.z), new float2(xPosLower, zPosLower));
            float distSqUpper = math.distancesq(new float2(worldPos.x, worldPos.z), new float2(xPosUpper, zPosUpper));

            int globalGridX, globalGridZ;
            if (distSqLower < distSqUpper)
            {
                globalGridX = colLower;
                globalGridZ = rowLower;
            }
            else
            {
                globalGridX = colUpper;
                globalGridZ = rowUpper;
            }
            
            // 2. Determine Y (Chunk)
            int globalGridY = (int)math.floor(worldPos.y / SceneGraphConstants.ChunkHeight);

            // 3. Map Global Grid Coords to Section/Chunk Structure
            // Calculate Global Chunk Indices first
            int globalChunkX = (int)math.floor((float)globalGridX / SceneGraphConstants.ChunkSizeNodesX);
            // Height chunks don't use node counts, just height steps
            int globalChunkZ = (int)math.floor((float)globalGridZ / SceneGraphConstants.ChunkSizeNodesZ);

            sectionKey = new int3(
                (int)math.floor((float)globalChunkX / SceneGraphConstants.SectionSizeChunksX),
                (int)math.floor((float)globalGridY / SceneGraphConstants.SectionSizeChunksY), 
                (int)math.floor((float)globalChunkZ / SceneGraphConstants.SectionSizeChunksZ)
            );

            // Chunk Index within Section
            chunkIdxInSection = new int3(
                globalChunkX % SceneGraphConstants.SectionSizeChunksX,
                globalGridY % SceneGraphConstants.SectionSizeChunksY,
                globalChunkZ % SceneGraphConstants.SectionSizeChunksZ
            );
            
            // Handle negative modulo (C# % operator can return negative)
            if (chunkIdxInSection.x < 0) chunkIdxInSection.x += SceneGraphConstants.SectionSizeChunksX;
            if (chunkIdxInSection.y < 0) chunkIdxInSection.y += SceneGraphConstants.SectionSizeChunksY;
            if (chunkIdxInSection.z < 0) chunkIdxInSection.z += SceneGraphConstants.SectionSizeChunksZ;

            // Local Node Index within Chunk
            int localNodeX = globalGridX % SceneGraphConstants.ChunkSizeNodesX;
            int localNodeZ = globalGridZ % SceneGraphConstants.ChunkSizeNodesZ;

            if (localNodeX < 0) localNodeX += SceneGraphConstants.ChunkSizeNodesX;
            if (localNodeZ < 0) localNodeZ += SceneGraphConstants.ChunkSizeNodesZ;

            nodeLocalIdx = new int2(localNodeX, localNodeZ);

            // Node Offset (Visual offset from node center)
            float3 snappedPos = GraphToWorldBase(sectionKey, chunkIdxInSection, nodeLocalIdx);
            nodeOffset = worldPos - snappedPos;
        }

        public static float3 GraphToWorldBase(int3 sectionKey, int3 chunkIdxInSection, int2 nodeLocalIdx)
        {
            // Reconstruct Global Grid Indices
            // Note: To get precise Global Index, we need the Section+Chunk base.
            
            long globalChunkX = (long)sectionKey.x * SceneGraphConstants.SectionSizeChunksX + chunkIdxInSection.x;
            long globalChunkZ = (long)sectionKey.z * SceneGraphConstants.SectionSizeChunksZ + chunkIdxInSection.z;
            
            long globalNodeX = globalChunkX * SceneGraphConstants.ChunkSizeNodesX + nodeLocalIdx.x;
            long globalNodeZ = globalChunkZ * SceneGraphConstants.ChunkSizeNodesZ + nodeLocalIdx.y;

            // Z Position
            // Z = GridZ * Spacing
            float worldZ = globalNodeZ * SceneGraphConstants.NodeSpacingZ;

            // X Position
            // Offset = (GridZ is Odd) ? Size/2 : 0
            float xOffset = (globalNodeZ & 1) != 0 ? SceneGraphConstants.NodeSize * 0.5f : 0f;
            float worldX = globalNodeX * SceneGraphConstants.NodeSize + xOffset;

            // Y Position
            // SectionY * SectionSizeY + ChunkY * ChunkHeight
            // Wait, chunkIdxInSection.y accounts for section?
            // "sectionKey.y * SectionSizeY" -> SectionSizeY is in Unity units? Yes.
            // Actually simpler:
            long globalChunkY = (long)sectionKey.y * SceneGraphConstants.SectionSizeChunksY + chunkIdxInSection.y;
            float worldY = globalChunkY * SceneGraphConstants.ChunkHeight;

            // Convention: Return Center of the lattice point? 
            // Or Bottom-Left? "WorldToGraph" used floor. 
            // Usually for points, we return the point itself.
            // But lets assume we want the position of the lattice vertex.
            // Nodes ARE vertices. So just return the vertex pos.
            
            return new float3(worldX, worldY, worldZ);
        }

        // --- Address Packing ---

        public static uint PackSectionId(int3 r)
        {
            uint3 biased = new uint3(
                (uint)(r.x + SceneGraphConstants.SectionCoordBias),
                (uint)(r.y + SceneGraphConstants.SectionCoordBias),
                (uint)(r.z + SceneGraphConstants.SectionCoordBias));

            return Morton.EncodeMorton32(biased);
        }

        public static int3 UnpackSectionId(uint rId)
        {
            uint3 coords = Morton.DecodeMorton32(rId);
            return new int3(
                (int)coords.x - SceneGraphConstants.SectionCoordBias,
                (int)coords.y - SceneGraphConstants.SectionCoordBias,
                (int)coords.z - SceneGraphConstants.SectionCoordBias);
        }

        public static NodeAddress PackNodeId(EntitiesHash128 sceneGuid, int3 section, int3 chunk, int2 node)
        {
            return PackNodeId(sceneGuid, PackSectionId(section), EncodeChunkToMorton(chunk), EncodeNodeToMorton(node));
        }

        public static NodeAddress PackNodeId(EntitiesHash128 sceneGuid, uint sectionId, ushort chunkMorton, byte nodeMorton)
        {
            return new NodeAddress(sceneGuid, sectionId, chunkMorton, nodeMorton);
        }

        public static ChunkAddress GetChunkAddress(EntitiesHash128 sceneGuid, int3 section, int3 chunk)
        {
            uint sectionId = PackSectionId(section);
            ushort chunkMorton = EncodeChunkToMorton(chunk);
            return new ChunkAddress(sceneGuid, sectionId, chunkMorton);
        }

        // Morton Encoding for 3D Chunk Index within a Section.
        // We have 15 bits available for the chunk morton.
        // Section dims: 32 (X) * 32 (Y) * 32 (Z) capability (though Y might be clamped logically).
        // X, Y, Z range 0-31 (5 bits each).
        // 5 + 5 + 5 = 15 bits.
        //
        // Strategy: Standard 3D Morton packing.
        
        public static ushort EncodeChunkToMorton(int3 chunkIdx)
        {
            // Standard 3D Morton Encoding (5 bits per axis = 15 bits total)
            return (ushort)Morton.EncodeMorton32(new uint3((uint)chunkIdx.x, (uint)chunkIdx.y, (uint)chunkIdx.z));
        }

        public static int3 DecodeMortonToChunk(ushort morton)
        {
             // Standard 3D Morton Decoding
             uint3 coords = Morton.DecodeMorton32(morton);
             return new int3((int)coords.x, (int)coords.y, (int)coords.z);
        }

        /// <summary>
        /// Encodes the local node 2D index (0-15, 0-15) into an 8-bit Morton code.
        /// </summary>
        public static byte EncodeNodeToMorton(int2 nodeIdx)
        {
            // 16x16 nodes => 4 bits for X, 4 bits for Z. Total 8 bits.
            return (byte)Morton.EncodeMorton2D(new uint2((uint)nodeIdx.x, (uint)nodeIdx.y));
        }

        public static int2 DecodeMortonToNode(byte morton)
        {
            uint2 coords = Morton.DecodeMorton2D(morton);
            return new int2((int)coords.x, (int)coords.y);
        }
    }
}



