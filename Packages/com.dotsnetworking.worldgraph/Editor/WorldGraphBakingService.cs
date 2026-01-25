using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using DotsNetworking.WorldGraph.Utils;
using Unity.Entities.Serialization;

namespace DotsNetworking.WorldGraph.Editor
{
    public static class WorldGraphBakingService
    {
        public static void BakeScene(LayerMask geometryLayer, LayerMask obstacleLayer)
        {
            int worldId = WorldGraphEditorSettings.instance.GetCurrentSceneWorldId();
            
            // 1. Calculate World Bounds based on Geometry Layer
            // This can be expensive if we search every collider, but it's an editor tool.
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            Bounds worldBounds = new Bounds();
            bool hasBounds = false;

            int geomeLayerVal = geometryLayer.value;
            foreach (var col in colliders)
            {
                if (((1 << col.gameObject.layer) & geomeLayerVal) != 0)
                {
                    if (!hasBounds)
                    {
                        worldBounds = col.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        worldBounds.Encapsulate(col.bounds);
                    }
                }
            }

            if (!hasBounds)
            {
                Debug.LogWarning("No geometry found to bake!");
                return;
            }

            // 2. Determine affected Regions
            // We need to convert Min/Max world points to Region Keys.
            // Note: Region 0,0,0 is at 0,0,0 world (usually). 
            // Min Key
            WorldGraphMath.WorldToGraph(worldBounds.min, out int3 minRegion, out _, out _, out _);
            // Max Key
            WorldGraphMath.WorldToGraph(worldBounds.max, out int3 maxRegion, out _, out _, out _);

            int totalRegions = (maxRegion.x - minRegion.x + 1) * (maxRegion.y - minRegion.y + 1) * (maxRegion.z - minRegion.z + 1);
            if (totalRegions > 100)
            {
                bool confirm = EditorUtility.DisplayDialog("Bulk Bake Warning", 
                    $"This will bake {totalRegions} regions. It might take a while. Continue?", "Yes", "Cancel");
                if (!confirm) return;
            }

            var scannedRegions = new Dictionary<int3, RegionBakeData>();
            var blobCache = new Dictionary<ulong, BlobAssetHandle<Region>>();
            var bakedChunkCache = new Dictionary<ulong, EditorChunkHeights>();

            // 3. Scan geometry for all regions first
            int processed = 0;
            try 
            {
                for (int x = minRegion.x; x <= maxRegion.x; x++)
                {
                    for (int y = minRegion.y; y <= maxRegion.y; y++)
                    {
                        for (int z = minRegion.z; z <= maxRegion.z; z++)
                        {
                            int3 regionKey = new int3(x, y, z);
                            if (!RegionHasGeometry(regionKey, geometryLayer))
                            {
                                TryDeleteRegionAsset(regionKey, worldId);
                                processed++;
                                continue;
                            }

                            EditorUtility.DisplayProgressBar("Baking World", $"Scanning Region {regionKey}", (float)processed / totalRegions);
                            var activeChunks = ScanRegionGeometry(regionKey, geometryLayer, obstacleLayer);
                            if (activeChunks.Count > 0)
                            {
                                scannedRegions[regionKey] = new RegionBakeData(regionKey, activeChunks);
                            }
                            else
                            {
                                TryDeleteRegionAsset(regionKey, worldId);
                            }
                            processed++;
                        }
                    }
                }

                // 4. Build connectivity after all geometry is scanned
                int baked = 0;
                int totalToBake = scannedRegions.Count;
                foreach (var kvp in scannedRegions)
                {
                    EditorUtility.DisplayProgressBar("Baking World", $"Building Region {kvp.Key}", totalToBake == 0 ? 1f : (float)baked / totalToBake);
                    BuildAndWriteRegion(kvp.Value, scannedRegions, blobCache, bakedChunkCache, worldId);
                    baked++;
                }
            }
            finally
            {
                foreach (var kvp in blobCache)
                {
                    NavigationAssetProvider.Release(kvp.Key);
                }
                EditorUtility.ClearProgressBar();
            }
        }

        public static void BakeRegion(int3 regionKey, LayerMask geometryLayer, LayerMask obstacleLayer, int worldId = 0)
        {
            Debug.Log($"Baking Region {regionKey} for World ID {worldId}");
            if (!RegionHasGeometry(regionKey, geometryLayer))
            {
                TryDeleteRegionAsset(regionKey, worldId);
                return;
            }

            var blobCache = new Dictionary<ulong, BlobAssetHandle<Region>>();
            var bakedChunkCache = new Dictionary<ulong, EditorChunkHeights>();

            try
            {
                EditorUtility.DisplayProgressBar("Baking Region", "Scanning Geometry...", 0f);
                var activeChunks = ScanRegionGeometry(regionKey, geometryLayer, obstacleLayer);
                if (activeChunks.Count == 0)
                {
                    TryDeleteRegionAsset(regionKey, worldId);
                    return;
                }

                var scannedRegions = new Dictionary<int3, RegionBakeData>
                {
                    [regionKey] = new RegionBakeData(regionKey, activeChunks)
                };

                EditorUtility.DisplayProgressBar("Baking Region", "Building Blob...", 0.5f);
                BuildAndWriteRegion(scannedRegions[regionKey], scannedRegions, blobCache, bakedChunkCache, worldId);
            }
            finally
            {
                foreach (var kvp in blobCache)
                {
                    NavigationAssetProvider.Release(kvp.Key);
                }
                EditorUtility.ClearProgressBar();
            }
        }
        
        // --- Helper Structs & Methods ---

        private struct ChunkBakeData
        {
            public float[] Heights; // Indexed by Morton Node Index (0-255)
        }

        private sealed class RegionBakeData
        {
            public int3 RegionKey;
            public Dictionary<ushort, EditorChunkHeights> ActiveChunks;

            public RegionBakeData(int3 regionKey, Dictionary<ushort, EditorChunkHeights> activeChunks)
            {
                RegionKey = regionKey;
                ActiveChunks = activeChunks;
            }
        }

        private static string GetRegionAssetPath(int3 regionKey, int worldId)
        {
            string folderPath = $"Assets/Resources/Data/World_{worldId}";
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            return $"{folderPath}/Region_{regionKey.x}_{regionKey.y}_{regionKey.z}.navblob";
        }

        private static void TryDeleteRegionAsset(int3 regionKey, int worldId)
        {
            string assetPath = GetRegionAssetPath(regionKey, worldId);
            if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private static bool RegionHasGeometry(int3 regionKey, LayerMask geometryLayer)
        {
            float3 rOrigin = new float3(
                regionKey.x * WorldGraphConstants.RegionSizeX,
                regionKey.y * WorldGraphConstants.RegionSizeY,
                regionKey.z * WorldGraphConstants.RegionSizeZ);

            Vector3 rCenter = rOrigin + new float3(
                WorldGraphConstants.RegionSizeX * 0.5f,
                WorldGraphConstants.RegionSizeY * 0.5f,
                WorldGraphConstants.RegionSizeZ * 0.5f);

            Vector3 rHalfExtents = new Vector3(
                WorldGraphConstants.RegionSizeX * 0.5f,
                WorldGraphConstants.RegionSizeY * 0.5f,
                WorldGraphConstants.RegionSizeZ * 0.5f);

            return Physics.CheckBox(rCenter, rHalfExtents + Vector3.one * 0.1f, Quaternion.identity, geometryLayer);
        }

        private static Dictionary<ushort, EditorChunkHeights> ScanRegionGeometry(int3 regionKey, LayerMask geometryLayer, LayerMask obstacleLayer)
        {
            var activeChunks = new Dictionary<ushort, EditorChunkHeights>();
            var validChunkMortons = new List<ushort>();

            for (int cz = 0; cz < WorldGraphConstants.RegionSizeChunksZ; cz++)
            {
                for (int cy = 0; cy < WorldGraphConstants.RegionSizeChunksY; cy++)
                {
                    for (int cx = 0; cx < WorldGraphConstants.RegionSizeChunksX; cx++)
                    {
                        int3 cIdx = new int3(cx, cy, cz);
                        float3 origin = WorldGraphMath.GraphToWorldBase(regionKey, cIdx, int2.zero);
                        Vector3 center = origin + new float3(WorldGraphConstants.ChunkSizeX * 0.5f, WorldGraphConstants.ChunkHeight * 0.5f, WorldGraphConstants.ChunkSizeZ * 0.5f);
                        Vector3 halfExtents = new Vector3(WorldGraphConstants.ChunkSizeX * 0.5f, WorldGraphConstants.ChunkHeight * 0.5f, WorldGraphConstants.ChunkSizeZ * 0.5f);

                        if (!Physics.CheckBox(center, halfExtents + Vector3.one * 0.1f, Quaternion.identity, geometryLayer))
                            continue;

                        ushort morton = WorldGraphMath.EncodeChunkToMorton(cIdx);
                        var data = new EditorChunkHeights { Heights = new float[WorldGraphConstants.NodesPerChunk] };
                        for (int i = 0; i < data.Heights.Length; i++) data.Heights[i] = float.NaN;
                        activeChunks[morton] = data;
                        validChunkMortons.Add(morton);
                    }
                }
            }

            int totalNodes = validChunkMortons.Count * WorldGraphConstants.NodesPerChunk;
            if (totalNodes == 0)
                return activeChunks;

            var nodeChunkMortons = new ushort[totalNodes];
            var nodeMortons = new byte[totalNodes];

            var commands = new NativeArray<RaycastCommand>(totalNodes, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(totalNodes, Allocator.TempJob);

            int idx = 0;
            foreach (ushort chunkMorton in validChunkMortons)
            {
                int3 chunkIdx = WorldGraphMath.DecodeMortonToChunk(chunkMorton);
                for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
                {
                    int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                    float3 nodeWorldPos = WorldGraphMath.GraphToWorldBase(regionKey, chunkIdx, nodeIdx);
                    float rayOriginY = nodeWorldPos.y + WorldGraphConstants.ChunkHeight;
                    Vector3 rayOrigin = new Vector3(nodeWorldPos.x, rayOriginY, nodeWorldPos.z);

                    commands[idx] = new RaycastCommand(rayOrigin, Vector3.down, new QueryParameters(geometryLayer), WorldGraphConstants.ChunkHeight);
                    nodeChunkMortons[idx] = chunkMorton;
                    nodeMortons[idx] = (byte)i;
                    idx++;
                }
            }

            RaycastCommand.ScheduleBatch(commands, results, 32, default).Complete();

            var commandsOv = new NativeArray<OverlapCapsuleCommand>(totalNodes, Allocator.TempJob);
            var resultsOv = new NativeArray<ColliderHit>(totalNodes, Allocator.TempJob);
            float capsuleHeight = 2.0f;
            float capsuleRadius = 0.10f;
            float groundClearance = 0.05f;
            LayerMask overlapMask = geometryLayer | obstacleLayer;

            for (int i = 0; i < totalNodes; i++)
            {
                if (results[i].collider != null)
                {
                    Vector3 groundPt = results[i].point;
                    Vector3 pBot = groundPt + Vector3.up * (capsuleRadius + groundClearance);
                    Vector3 pTop = groundPt + Vector3.up * (capsuleHeight - capsuleRadius + groundClearance);
                    commandsOv[i] = new OverlapCapsuleCommand(pBot, pTop, capsuleRadius, new QueryParameters(overlapMask));
                }
                else
                {
                    commandsOv[i] = new OverlapCapsuleCommand();
                }
            }

            OverlapCapsuleCommand.ScheduleBatch(commandsOv, resultsOv, 32, 1, default).Complete();

            for (int i = 0; i < totalNodes; i++)
            {
                ushort chunkMorton = nodeChunkMortons[i];
                byte nodeMorton = nodeMortons[i];
                if (!activeChunks.TryGetValue(chunkMorton, out EditorChunkHeights data))
                    continue;

                if (results[i].collider != null)
                {
                    int hitId = results[i].collider.GetInstanceID();
                    if (resultsOv[i].instanceID == 0 || resultsOv[i].instanceID == hitId)
                    {
                        data.Heights[nodeMorton] = results[i].point.y;
                    }
                }
            }

            commands.Dispose();
            results.Dispose();
            commandsOv.Dispose();
            resultsOv.Dispose();

            var toRemove = new List<ushort>();
            foreach (var kvp in activeChunks)
            {
                bool hasData = false;
                var heights = kvp.Value.Heights;
                for (int i = 0; i < heights.Length; i++)
                {
                    if (!float.IsNaN(heights[i])) { hasData = true; break; }
                }
                if (!hasData) toRemove.Add(kvp.Key);
            }
            foreach (var k in toRemove) activeChunks.Remove(k);

            return activeChunks;
        }

        private static void BuildAndWriteRegion(
            RegionBakeData regionData,
            Dictionary<int3, RegionBakeData> scannedRegions,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            Dictionary<ulong, EditorChunkHeights> bakedChunkCache,
            int worldId)
        {
            string assetPath = GetRegionAssetPath(regionData.RegionKey, worldId);

            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref Region regionBlob = ref builder.ConstructRoot<Region>();
                regionBlob.MortonCode = 0;

                var sortedKeys = regionData.ActiveChunks.Keys.ToList();
                sortedKeys.Sort();

                int validCount = sortedKeys.Count;
                var chunksArray = builder.Allocate(ref regionBlob.Chunks, validCount);

                int maxMorton = 32768;
                var lookupArray = builder.Allocate(ref regionBlob.ChunkLookup, maxMorton);
                for (int i = 0; i < maxMorton; i++) lookupArray[i] = -1;

                for (int i = 0; i < validCount; i++)
                {
                    ushort morton = sortedKeys[i];
                    EditorChunkHeights rawData = regionData.ActiveChunks[morton];

                    lookupArray[morton] = (short)i;
                    chunksArray[i].MortonCode = morton;
                    var nodesArray = builder.Allocate(ref chunksArray[i].Nodes, WorldGraphConstants.NodesPerChunk);

                    ulong[] calculatedFlags = new ulong[WorldGraphConstants.NodesPerChunk];
                    CalculateConnectivityForChunk(regionData.RegionKey, WorldGraphMath.DecodeMortonToChunk(morton), scannedRegions, blobCache, bakedChunkCache, worldId, rawData.Heights, ref calculatedFlags);

                    for (int n = 0; n < WorldGraphConstants.NodesPerChunk; n++)
                    {
                        nodesArray[n].Y = rawData.Heights[n];
                        nodesArray[n].ExitMask = (MovementFlags)calculatedFlags[n];
                    }
                }

                BlobAssetReference<Region>.Write(builder, assetPath, 0);
            }

            AssetDatabase.ImportAsset(assetPath);
            ulong regionAddress = WorldGraphMath.PackChunkAddress(regionData.RegionKey, int3.zero, worldId);
            Debug.Log($"Baked Region {regionData.RegionKey} to {assetPath} at Address {regionAddress}");
            NavigationAssetProvider.ForceReloadOfBlobAsset(regionAddress);
        }
        
        // Removed GetFlatChunkIndex/GetChunkIndexFromFlat as they are no longer used.

        /// <summary>
        /// Scan a single chunk and return heights only (for incremental baking diff).
        /// Public for use by incremental baking layers.
        /// </summary>
        public static void ScanChunkForDiff(int3 r, int3 c, LayerMask ground, LayerMask obs, out float[] heights)
        {
            ScanSingleChunk(r, c, ground, obs, out var result);
            heights = result.Heights;
        }

        /// <summary>
        /// Calculate connectivity flags for a chunk given heights (for incremental baking).
        /// Public for use by incremental baking layers.
        /// </summary>
        public static void CalculateConnectivityForChunkDiff(int3 r, int3 c, float[] heights, ref MovementFlags[] outFlags)
        {
            // Simplified connectivity calculation for a single chunk
            long baseGlobalZ = (long)r.z * WorldGraphConstants.RegionSizeChunksZ * WorldGraphConstants.ChunkSizeNodesZ 
                           + (long)c.z * WorldGraphConstants.ChunkSizeNodesZ;

            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                float h = heights[i];
                if (float.IsNaN(h))
                {
                    outFlags[i] = MovementFlags.Unreachable;
                    continue;
                }
                
                int2 node = WorldGraphMath.DecodeMortonToNode((byte)i);
                
                // Hex Neighbors Logic
                bool isOddRow = ((baseGlobalZ + node.y) & 1) != 0;
                int2[] offsets = isOddRow 
                    ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                    : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };
                
                // Map offsets to Direction Flags
                ulong[] dirs = isOddRow
                    ? new ulong[] { (ulong)MovementFlags.NW, (ulong)MovementFlags.NE, (ulong)MovementFlags.SW, (ulong)MovementFlags.SE, (ulong)MovementFlags.W, (ulong)MovementFlags.E }
                    : new ulong[] { (ulong)MovementFlags.NE, (ulong)MovementFlags.NW, (ulong)MovementFlags.SE, (ulong)MovementFlags.SW, (ulong)MovementFlags.W, (ulong)MovementFlags.E };

                ulong flags = 0;

                for (int k = 0; k < 6; k++)
                {
                    int2 nNode = node + offsets[k];
                    int3 nChunk = c;
                    
                    // Wrapping Logic (Crossing Chunk Boundaries in X/Z)
                    if (nNode.x < 0) { nNode.x += WorldGraphConstants.ChunkSizeNodesX; nChunk.x--; }
                    else if (nNode.x >= WorldGraphConstants.ChunkSizeNodesX) { nNode.x -= WorldGraphConstants.ChunkSizeNodesX; nChunk.x++; }

                    if (nNode.y < 0) { nNode.y += WorldGraphConstants.ChunkSizeNodesZ; nChunk.z--; }
                    else if (nNode.y >= WorldGraphConstants.ChunkSizeNodesZ) { nNode.y -= WorldGraphConstants.ChunkSizeNodesZ; nChunk.z++; }

                    // Region Bounds Check
                    if (nChunk.x < 0 || nChunk.x >= WorldGraphConstants.RegionSizeChunksX ||
                        nChunk.z < 0 || nChunk.z >= WorldGraphConstants.RegionSizeChunksZ)
                        continue;

                    // For incremental baking, assume neighbors in chunk are valid if within region
                    // (actual validation would require access to loaded blobs)
                    flags |= dirs[k];
                }
                outFlags[i] = (MovementFlags)flags;
            }
        }

        private static void ScanSingleChunk(int3 r, int3 c, LayerMask ground, LayerMask obs, out EditorChunkHeights result)
        {
            // Simplified synchronous scan reuse
            result = new EditorChunkHeights();
            result.Heights = new float[WorldGraphConstants.NodesPerChunk];
            
            // Optimization: Check bounds before raycasting (OverlapBox)
            float3 origin = WorldGraphMath.GraphToWorldBase(r, c, int2.zero);
            Vector3 center = origin + new float3(WorldGraphConstants.ChunkSizeX * 0.5f, WorldGraphConstants.ChunkHeight * 0.5f, WorldGraphConstants.ChunkSizeZ * 0.5f);
            Vector3 halfExtents = new Vector3(WorldGraphConstants.ChunkSizeX * 0.5f, WorldGraphConstants.ChunkHeight * 0.5f, WorldGraphConstants.ChunkSizeZ * 0.5f);
            
            // If no geometry is present in this chunk's volume, skip expensive raycasts
            // We expand the check slightly (0.1f) to ensure we catch boundary geometry
            if (!Physics.CheckBox(center, halfExtents + Vector3.one * 0.1f, Quaternion.identity, ground))
            {
                for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++) result.Heights[i] = float.NaN;
                return;
            }

            // Re-implement the ScanChunk logic from Tool roughly using Jobs
            // For production, this should share the exact same code via a shared static Utils class.
            // I'll duplicate the core Scan logic here for autonomy ensuring it aligns with the overlay.

            int nodeCount = WorldGraphConstants.NodesPerChunk;
            var commands = new NativeArray<RaycastCommand>(nodeCount, Allocator.TempJob);
            var hitResults = new NativeArray<RaycastHit>(nodeCount, Allocator.TempJob);
            
             for (int i = 0; i < nodeCount; i++)
            {
                // Use Node Morton Decoding to get local X/Z for correct ray positions
                // We iterate i from 0..255 (which is the Morton Code)
                int2 localNode = WorldGraphMath.DecodeMortonToNode((byte)i);
                float3 pos = WorldGraphMath.GraphToWorldBase(r, c, localNode);
                commands[i] = new RaycastCommand(pos + new float3(0, WorldGraphConstants.ChunkHeight, 0), Vector3.down, new QueryParameters(ground), WorldGraphConstants.ChunkHeight);
            }
            RaycastCommand.ScheduleBatch(commands, hitResults, 32, default).Complete();
            
            // Overlaps
             var ovCommands = new NativeArray<OverlapCapsuleCommand>(nodeCount, Allocator.TempJob);
             var ovResults = new NativeArray<ColliderHit>(nodeCount, Allocator.TempJob);

             float capsuleHeight = 2.0f; float capsuleRadius = 0.10f; float groundClearence = 0.05f;
             LayerMask overlapMask = ground | obs;
             
             for(int i=0; i<nodeCount; i++)
             {
                 if (hitResults[i].collider != null)
                 {
                     Vector3 groundPt = hitResults[i].point;
                     Vector3 pBot = groundPt + Vector3.up * (capsuleRadius + groundClearence);
                     Vector3 pTop = groundPt + Vector3.up * (capsuleHeight - capsuleRadius + groundClearence);
                     ovCommands[i] = new OverlapCapsuleCommand(pBot, pTop, capsuleRadius, new QueryParameters(overlapMask));
                 }
                 else
                 {
                     ovCommands[i] = new OverlapCapsuleCommand();
                 }
             }
             OverlapCapsuleCommand.ScheduleBatch(ovCommands, ovResults, 32, 1, default).Complete();

             for (int i = 0; i < nodeCount; i++)
             {
                 if (hitResults[i].collider != null)
                 {
                     int hitId = hitResults[i].collider.GetInstanceID();
                     if (ovResults[i].instanceID == 0 || ovResults[i].instanceID == hitId)
                         result.Heights[i] = hitResults[i].point.y;
                     else
                         result.Heights[i] = float.NaN;
                 }
                 else
                 {
                     result.Heights[i] = float.NaN;
                 }
             }

             commands.Dispose(); hitResults.Dispose(); ovCommands.Dispose(); ovResults.Dispose();
        }

        private static void CalculateConnectivityForChunk(
            int3 r,
            int3 c,
            Dictionary<int3, RegionBakeData> scannedRegions,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            Dictionary<ulong, EditorChunkHeights> bakedChunkCache,
            int worldId,
            float[] heights,
            ref ulong[] outFlags)
        {
            long baseGlobalZ = (long)r.z * WorldGraphConstants.RegionSizeChunksZ * WorldGraphConstants.ChunkSizeNodesZ 
                           + (long)c.z * WorldGraphConstants.ChunkSizeNodesZ;

            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                float h = heights[i];
                if (float.IsNaN(h))
                {
                    outFlags[i] = (ulong)MovementFlags.Unreachable;
                    continue;
                }
                
                int2 node = WorldGraphMath.DecodeMortonToNode((byte)i);
                
                bool isOddRow = ((baseGlobalZ + node.y) & 1) != 0;
                int2[] offsets = isOddRow 
                    ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                    : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };
                
                ulong[] dirs = isOddRow
                    ? new ulong[] { (ulong)MovementFlags.NW, (ulong)MovementFlags.NE, (ulong)MovementFlags.SW, (ulong)MovementFlags.SE, (ulong)MovementFlags.W, (ulong)MovementFlags.E }
                    : new ulong[] { (ulong)MovementFlags.NE, (ulong)MovementFlags.NW, (ulong)MovementFlags.SE, (ulong)MovementFlags.SW, (ulong)MovementFlags.W, (ulong)MovementFlags.E };

                ulong flags = 0;

                for (int k = 0; k < 6; k++)
                {
                    int2 nNode = node + offsets[k];
                    int3 nChunk = c;
                    int3 nRegion = r;
                    
                    if (nNode.x < 0) { nNode.x += WorldGraphConstants.ChunkSizeNodesX; nChunk.x--; }
                    else if (nNode.x >= WorldGraphConstants.ChunkSizeNodesX) { nNode.x -= WorldGraphConstants.ChunkSizeNodesX; nChunk.x++; }

                    if (nNode.y < 0) { nNode.y += WorldGraphConstants.ChunkSizeNodesZ; nChunk.z--; }
                    else if (nNode.y >= WorldGraphConstants.ChunkSizeNodesZ) { nNode.y -= WorldGraphConstants.ChunkSizeNodesZ; nChunk.z++; }

                    if (nChunk.x < 0) { nChunk.x += WorldGraphConstants.RegionSizeChunksX; nRegion.x--; }
                    else if (nChunk.x >= WorldGraphConstants.RegionSizeChunksX) { nChunk.x -= WorldGraphConstants.RegionSizeChunksX; nRegion.x++; }

                    if (nChunk.z < 0) { nChunk.z += WorldGraphConstants.RegionSizeChunksZ; nRegion.z--; }
                    else if (nChunk.z >= WorldGraphConstants.RegionSizeChunksZ) { nChunk.z -= WorldGraphConstants.RegionSizeChunksZ; nRegion.z++; }

                    for (int dy = 1; dy >= -1; dy--)
                    {
                        int3 checkC = nChunk;
                        int3 checkR = nRegion;
                        checkC.y += dy;
                        if (checkC.y >= WorldGraphConstants.RegionSizeChunksY) { checkC.y -= WorldGraphConstants.RegionSizeChunksY; checkR.y++; }
                        else if (checkC.y < 0) { checkC.y += WorldGraphConstants.RegionSizeChunksY; checkR.y--; }

                        if (TryGetChunkData(checkR, checkC, scannedRegions, blobCache, bakedChunkCache, worldId, out EditorChunkHeights nData))
                        {
                            int nFlat = WorldGraphMath.EncodeNodeToMorton(nNode);
                            float nh = nData.Heights[nFlat];
                            if (!float.IsNaN(nh) && Mathf.Abs(nh - h) <= WorldGraphConstants.MaxSlopeHeight)
                            {
                                flags |= dirs[k];
                                break;
                            }
                        }
                    }
                }
                outFlags[i] = flags;
            }

            // Pass 2: Core or Neighbor-of-Core logic (matches tool behavior)
            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                float h = heights[i];
                if (float.IsNaN(h))
                    continue;

                ulong f = outFlags[i];
                if (CountSetBits(f) == 6)
                    continue; // Is Core

                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                bool isOddRow = ((baseGlobalZ + nodeIdx.y) & 1) != 0;
                int2[] offsets = isOddRow
                    ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                    : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };

                ulong[] dirMasks = isOddRow
                    ? new ulong[] { (ulong)MovementFlags.NW, (ulong)MovementFlags.NE, (ulong)MovementFlags.SW, (ulong)MovementFlags.SE, (ulong)MovementFlags.W, (ulong)MovementFlags.E }
                    : new ulong[] { (ulong)MovementFlags.NE, (ulong)MovementFlags.NW, (ulong)MovementFlags.SE, (ulong)MovementFlags.SW, (ulong)MovementFlags.W, (ulong)MovementFlags.E };

                bool hasCoreNeighbor = false;
                for (int k = 0; k < 6; k++)
                {
                    if ((f & dirMasks[k]) == 0)
                        continue;

                    int2 targetNode = nodeIdx + offsets[k];
                    if (GetNeighborNodeCoords(r, c, targetNode, out int3 nRegion, out int3 nChunk, out int2 nNode))
                    {
                        if (IsNodeCore(nRegion, nChunk, nNode, scannedRegions, blobCache, bakedChunkCache, worldId))
                        {
                            hasCoreNeighbor = true;
                            break;
                        }
                    }
                }

                if (!hasCoreNeighbor)
                    outFlags[i] |= (ulong)MovementFlags.Unreachable;
            }
        }

        private static int CountSetBits(ulong value)
        {
            value &= 0xFFF; // Only existence bits
            int count = 0;
            while (value != 0) { count++; value &= value - 1; }
            return count;
        }

        private static bool IsNodeCore(
            int3 region,
            int3 chunk,
            int2 node,
            Dictionary<int3, RegionBakeData> scannedRegions,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            Dictionary<ulong, EditorChunkHeights> bakedChunkCache,
            int worldId)
        {
            if (TryGetChunkData(region, chunk, scannedRegions, blobCache, bakedChunkCache, worldId, out EditorChunkHeights data))
            {
                int flat = WorldGraphMath.EncodeNodeToMorton(node);
                float h = data.Heights[flat];
                if (!float.IsNaN(h))
                {
                    return CountSetBits(GetHexConnectivityFlags(region, chunk, node, h, scannedRegions, blobCache, bakedChunkCache, worldId)) == 6;
                }
            }
            return false;
        }

        private static ulong GetHexConnectivityFlags(
            int3 region,
            int3 chunk,
            int2 nodeIdx,
            float currentY,
            Dictionary<int3, RegionBakeData> scannedRegions,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            Dictionary<ulong, EditorChunkHeights> bakedChunkCache,
            int worldId)
        {
            ulong flags = 0;
            long globalZ = (long)region.z * WorldGraphConstants.RegionSizeChunksZ * WorldGraphConstants.ChunkSizeNodesZ
                           + (long)chunk.z * WorldGraphConstants.ChunkSizeNodesZ + nodeIdx.y;
            bool isOddRow = (globalZ & 1) != 0;

            int2[] offsets = isOddRow
                ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };

            ulong[] dirs = isOddRow
                ? new ulong[] { (ulong)MovementFlags.NW, (ulong)MovementFlags.NE, (ulong)MovementFlags.SW, (ulong)MovementFlags.SE, (ulong)MovementFlags.W, (ulong)MovementFlags.E }
                : new ulong[] { (ulong)MovementFlags.NE, (ulong)MovementFlags.NW, (ulong)MovementFlags.SE, (ulong)MovementFlags.SW, (ulong)MovementFlags.W, (ulong)MovementFlags.E };

            for (int k = 0; k < 6; k++)
            {
                int2 targetNode = nodeIdx + offsets[k];
                if (GetNeighborNodeCoords(region, chunk, targetNode, out int3 nRegion, out int3 nChunk, out int2 nNode))
                {
                    for (int dy = 1; dy >= -1; dy--)
                    {
                        int3 checkC = nChunk;
                        int3 checkR = nRegion;
                        checkC.y += dy;
                        if (checkC.y >= WorldGraphConstants.RegionSizeChunksY) { checkC.y -= WorldGraphConstants.RegionSizeChunksY; checkR.y++; }
                        else if (checkC.y < 0) { checkC.y += WorldGraphConstants.RegionSizeChunksY; checkR.y--; }

                        if (TryGetChunkData(checkR, checkC, scannedRegions, blobCache, bakedChunkCache, worldId, out EditorChunkHeights nData))
                        {
                            int nFlat = WorldGraphMath.EncodeNodeToMorton(nNode);
                            float nh = nData.Heights[nFlat];
                            if (!float.IsNaN(nh) && Mathf.Abs(nh - currentY) <= WorldGraphConstants.MaxSlopeHeight)
                            {
                                flags |= dirs[k];
                                break;
                            }
                        }
                    }
                }
            }

            return flags;
        }

        private static bool GetNeighborNodeCoords(int3 region, int3 chunk, int2 targetNode, out int3 outRegion, out int3 outChunk, out int2 outNode)
        {
            outRegion = region;
            outChunk = chunk;
            outNode = targetNode;

            if (outNode.x < 0) { outNode.x += WorldGraphConstants.ChunkSizeNodesX; outChunk.x--; }
            else if (outNode.x >= WorldGraphConstants.ChunkSizeNodesX) { outNode.x -= WorldGraphConstants.ChunkSizeNodesX; outChunk.x++; }

            if (outNode.y < 0) { outNode.y += WorldGraphConstants.ChunkSizeNodesZ; outChunk.z--; }
            else if (outNode.y >= WorldGraphConstants.ChunkSizeNodesZ) { outNode.y -= WorldGraphConstants.ChunkSizeNodesZ; outChunk.z++; }

            if (outChunk.x < 0) { outChunk.x += WorldGraphConstants.RegionSizeChunksX; outRegion.x--; }
            else if (outChunk.x >= WorldGraphConstants.RegionSizeChunksX) { outChunk.x -= WorldGraphConstants.RegionSizeChunksX; outRegion.x++; }

            if (outChunk.z < 0) { outChunk.z += WorldGraphConstants.RegionSizeChunksZ; outRegion.z--; }
            else if (outChunk.z >= WorldGraphConstants.RegionSizeChunksZ) { outChunk.z -= WorldGraphConstants.RegionSizeChunksZ; outRegion.z++; }

            return true;
        }

        private static bool TryGetChunkData(
            int3 region,
            int3 chunk,
            Dictionary<int3, RegionBakeData> scannedRegions,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            Dictionary<ulong, EditorChunkHeights> bakedChunkCache,
            int worldId,
            out EditorChunkHeights data)
        {
            ulong chunkAddress = WorldGraphMath.PackChunkAddress(region, chunk);
            if (bakedChunkCache.TryGetValue(chunkAddress, out data))
                return true;

            if (scannedRegions.TryGetValue(region, out var regionData))
            {
                ushort morton = WorldGraphMath.EncodeChunkToMorton(chunk);
                if (regionData.ActiveChunks.TryGetValue(morton, out data))
                {
                    bakedChunkCache[chunkAddress] = data;
                    return true;
                }
            }

            if (TryGetChunkDataFromBlob(region, chunk, blobCache, worldId, out data))
            {
                bakedChunkCache[chunkAddress] = data;
                return true;
            }

            return false;
        }

        private static bool TryGetChunkDataFromBlob(
            int3 region,
            int3 chunk,
            Dictionary<ulong, BlobAssetHandle<Region>> blobCache,
            int worldId,
            out EditorChunkHeights data)
        {
            data = default;
            ulong regionAddress = WorldGraphMath.PackChunkAddress(region, int3.zero, worldId);
            if (!blobCache.TryGetValue(regionAddress, out var handle))
            {
                handle = NavigationAssetProvider.CheckOut(regionAddress);
                if (!handle.IsValid)
                    return false;
                blobCache[regionAddress] = handle;
            }

            if (!handle.IsValid)
                return false;

            ref Region r = ref handle.Blob.Value;
            ushort morton = WorldGraphMath.EncodeChunkToMorton(chunk);
            if (morton < r.ChunkLookup.Length)
            {
                short idx = r.ChunkLookup[morton];
                if (idx != -1 && idx < r.Chunks.Length)
                {
                    var heights = new float[WorldGraphConstants.NodesPerChunk];
                    ref Chunk c = ref r.Chunks[idx];
                    for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
                    {
                        heights[i] = c.Nodes[i].Y;
                    }
                    data = new EditorChunkHeights { Heights = heights };
                    return true;
                }
            }

            return false;
        }
    }
}