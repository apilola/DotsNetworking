using Unity.Mathematics;

namespace DotsNetworking.WorldGraph.Utils
{
    public static class WorldGraphMath
    {
        /// <summary>
        /// Converts a world position to a Region, Chunk, and Local Node coordinate.
        /// Handles Isometric/Staggered Lattice spacing by finding the nearest valid lattice point.
        /// </summary>
        public static void WorldToGraph(float3 worldPos, out int3 regionKey, out int3 chunkIdxInRegion, out int2 nodeLocalIdx, out float3 nodeOffset)
        {
            // 1. Find Nearest Lattice Point (Global Grid X/Z)
            // Staggered Grid Analysis:
            // Lattice rows are at Z = row * NodeSpacingZ.
            // Nodes in a row are at X = col * NodeSize + Offset(row).
            // To find the nearest node, we check the two nearest rows (Floor and Ceil of Z)
            // and find the closest node in X for each, then pick the winner.

            float zRaw = worldPos.z / WorldGraphConstants.NodeSpacingZ;
            int rowLower = (int)math.floor(zRaw);
            int rowUpper = rowLower + 1;

            // Candidate 1: Lower Row
            float offsetLower = (rowLower & 1) != 0 ? WorldGraphConstants.NodeSize * 0.5f : 0f;
            int colLower = (int)math.round((worldPos.x - offsetLower) / WorldGraphConstants.NodeSize);
            
            float xPosLower = colLower * WorldGraphConstants.NodeSize + offsetLower;
            float zPosLower = rowLower * WorldGraphConstants.NodeSpacingZ;

            // Candidate 2: Upper Row
            float offsetUpper = (rowUpper & 1) != 0 ? WorldGraphConstants.NodeSize * 0.5f : 0f;
            int colUpper = (int)math.round((worldPos.x - offsetUpper) / WorldGraphConstants.NodeSize);

            float xPosUpper = colUpper * WorldGraphConstants.NodeSize + offsetUpper;
            float zPosUpper = rowUpper * WorldGraphConstants.NodeSpacingZ;

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
            int globalGridY = (int)math.floor(worldPos.y / WorldGraphConstants.ChunkHeight);

            // 3. Map Global Grid Coords to Region/Chunk Structure
            // Calculate Global Chunk Indices first
            int globalChunkX = (int)math.floor((float)globalGridX / WorldGraphConstants.ChunkSizeNodesX);
            // Height chunks don't use node counts, just height steps
            int globalChunkZ = (int)math.floor((float)globalGridZ / WorldGraphConstants.ChunkSizeNodesZ);

            regionKey = new int3(
                (int)math.floor((float)globalChunkX / WorldGraphConstants.RegionSizeChunksX),
                (int)math.floor((float)globalGridY / WorldGraphConstants.RegionSizeChunksY), 
                (int)math.floor((float)globalChunkZ / WorldGraphConstants.RegionSizeChunksZ)
            );

            // Chunk Index within Region
            chunkIdxInRegion = new int3(
                globalChunkX % WorldGraphConstants.RegionSizeChunksX,
                globalGridY % WorldGraphConstants.RegionSizeChunksY,
                globalChunkZ % WorldGraphConstants.RegionSizeChunksZ
            );
            
            // Handle negative modulo (C# % operator can return negative)
            if (chunkIdxInRegion.x < 0) chunkIdxInRegion.x += WorldGraphConstants.RegionSizeChunksX;
            if (chunkIdxInRegion.y < 0) chunkIdxInRegion.y += WorldGraphConstants.RegionSizeChunksY;
            if (chunkIdxInRegion.z < 0) chunkIdxInRegion.z += WorldGraphConstants.RegionSizeChunksZ;

            // Local Node Index within Chunk
            int localNodeX = globalGridX % WorldGraphConstants.ChunkSizeNodesX;
            int localNodeZ = globalGridZ % WorldGraphConstants.ChunkSizeNodesZ;

            if (localNodeX < 0) localNodeX += WorldGraphConstants.ChunkSizeNodesX;
            if (localNodeZ < 0) localNodeZ += WorldGraphConstants.ChunkSizeNodesZ;

            nodeLocalIdx = new int2(localNodeX, localNodeZ);

            // Node Offset (Visual offset from node center)
            float3 snappedPos = GraphToWorldBase(regionKey, chunkIdxInRegion, nodeLocalIdx);
            nodeOffset = worldPos - snappedPos;
        }

        public static float3 GraphToWorldBase(int3 regionKey, int3 chunkIdxInRegion, int2 nodeLocalIdx)
        {
            // Reconstruct Global Grid Indices
            // Note: To get precise Global Index, we need the Region+Chunk base.
            
            long globalChunkX = (long)regionKey.x * WorldGraphConstants.RegionSizeChunksX + chunkIdxInRegion.x;
            long globalChunkZ = (long)regionKey.z * WorldGraphConstants.RegionSizeChunksZ + chunkIdxInRegion.z;
            
            long globalNodeX = globalChunkX * WorldGraphConstants.ChunkSizeNodesX + nodeLocalIdx.x;
            long globalNodeZ = globalChunkZ * WorldGraphConstants.ChunkSizeNodesZ + nodeLocalIdx.y;

            // Z Position
            // Z = GridZ * Spacing
            float worldZ = globalNodeZ * WorldGraphConstants.NodeSpacingZ;

            // X Position
            // Offset = (GridZ is Odd) ? Size/2 : 0
            float xOffset = (globalNodeZ & 1) != 0 ? WorldGraphConstants.NodeSize * 0.5f : 0f;
            float worldX = globalNodeX * WorldGraphConstants.NodeSize + xOffset;

            // Y Position
            // RegionY * RegionSizeY + ChunkY * ChunkHeight
            // Wait, chunkIdxInRegion.y accounts for region?
            // "regionKey.y * RegionSizeY" -> RegionSizeY is in Unity units? Yes.
            // Actually simpler:
            long globalChunkY = (long)regionKey.y * WorldGraphConstants.RegionSizeChunksY + chunkIdxInRegion.y;
            float worldY = globalChunkY * WorldGraphConstants.ChunkHeight;

            // Convention: Return Center of the lattice point? 
            // Or Bottom-Left? "WorldToGraph" used floor. 
            // Usually for points, we return the point itself.
            // But lets assume we want the position of the lattice vertex.
            // Nodes ARE vertices. So just return the vertex pos.
            
            return new float3(worldX, worldY, worldZ);
        }

        // --- Address Packing ---
        
        public static uint PackRegionId(int3 r) 
        {
            return (uint)(((r.x & 0xFF) << 16) | ((r.y & 0xFF) << 8) | (r.z & 0xFF)); 
        }

        public static int3 UnpackRegionId(uint rId)
        {
            int x = (int)((rId >> 16) & 0xFF);
            int y = (int)((rId >> 8) & 0xFF);
            int z = (int)(rId & 0xFF);
            if (x > 127) x -= 256;
            if (y > 127) y -= 256;
            if (z > 127) z -= 256;
            return new int3(x, y, z);
        }

        public static ulong PackChunkAddress(int3 region, int3 chunk, int worldId = 0)
        {
            uint rId = PackRegionId(region);
            ushort cId = EncodeChunkToMorton(chunk);
            return ((ulong)worldId << WorldGraphConstants.NodeIdShift_WorldId) |
                   ((ulong)rId << WorldGraphConstants.NodeIdShift_RegionId) |
                   ((ulong)cId << WorldGraphConstants.NodeIdShift_ChunkMorton);
        }

        public static ulong PackNodeAddress(int3 region, int3 chunk, int2 node, int worldId = 0)
        {
            uint rId = PackRegionId(region);
            ushort cId = EncodeChunkToMorton(chunk);
            byte nId = EncodeNodeToMorton(node);
            return PackNodeAddress(rId, cId, nId, worldId);
        }

        public static ulong PackNodeAddress(uint regionId, ushort chunkMorton, byte nodeMorton, int worldId = 0)
        {
            return ((ulong)worldId << WorldGraphConstants.NodeIdShift_WorldId) |
                   ((ulong)regionId << WorldGraphConstants.NodeIdShift_RegionId) |
                   ((ulong)chunkMorton << WorldGraphConstants.NodeIdShift_ChunkMorton) |
                   ((ulong)nodeMorton << WorldGraphConstants.NodeIdShift_NodeIndex);
        }
        
        public static void UnpackChunkAddress(ulong address, out int3 region, out int3 chunk, int worldId = 0)
        {
             UnpackChunkAddress(address, out uint rId, out ushort cMorton, worldId);
             region = UnpackRegionId(rId);
             chunk = DecodeMortonToChunk(cMorton);
        }

        public static void UnpackChunkAddress(ulong address, out uint regionId, out ushort chunkMorton, int worldId = 0)
        {
             regionId = (uint)((address & WorldGraphConstants.NodeIdMask_RegionId) >> WorldGraphConstants.NodeIdShift_RegionId);
             chunkMorton = (ushort)((address & WorldGraphConstants.NodeIdMask_ChunkMorton) >> WorldGraphConstants.NodeIdShift_ChunkMorton);
        }

        public static void UnpackNodeAddress(ulong address, out int3 region, out int3 chunk, out int2 node, int worldId = 0)
        {
             UnpackNodeAddress(address, out uint rId, out ushort cMorton, out byte nMorton, worldId);
             region = UnpackRegionId(rId);
             chunk = DecodeMortonToChunk(cMorton);
             node = DecodeMortonToNode(nMorton);
        }

        public static void UnpackNodeAddress(ulong address, out uint regionId, out ushort chunkMorton, out byte nodeMorton, int worldId = 0)
        {
             regionId = (uint)((address & WorldGraphConstants.NodeIdMask_RegionId) >> WorldGraphConstants.NodeIdShift_RegionId);
             chunkMorton = (ushort)((address & WorldGraphConstants.NodeIdMask_ChunkMorton) >> WorldGraphConstants.NodeIdShift_ChunkMorton);
             nodeMorton = (byte)((address & WorldGraphConstants.NodeIdMask_NodeIndex) >> WorldGraphConstants.NodeIdShift_NodeIndex);
        }

        // Morton Encoding for 3D Chunk Index within a Region.
        // We have 15 bits available in the NodeId.
        // Region dims: 32 (X) * 32 (Y) * 32 (Z) capability (though Y might be clamped logically).
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
