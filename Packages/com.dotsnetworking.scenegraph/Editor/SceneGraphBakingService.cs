using System.Collections.Generic;
using System.IO;
using System.Linq;
using BovineLabs.Core.Editor.Settings;
using BovineLabs.Core.Editor.Extensions;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Content;
using DotsNetworking.SceneGraph.Utils;
using Unity.Entities.Serialization;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using EntitiesHash128 = Unity.Entities.Hash128;
using Unity.Collections.LowLevel.Unsafe;
using DotsNetworking.SceneGraph.Components;
using DotsNetworking.SceneGraph.Authoring;
namespace DotsNetworking.SceneGraph.Editor
{
    public static class SceneGraphBakingService
    {
        public static void RegenerateSectionAuthoring(SceneGraphBakeAuthoring authoring)
        {
            if (authoring == null)
            {
                Debug.LogWarning("SceneGraphBakeAuthoring is null.");
                return;
            }

            var scene = authoring.gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("SceneGraphBakeAuthoring must be in a saved scene.");
                return;
            }

            var manifest = EditorSettingsUtility.GetSettings<SceneGraphManifest>();
            if (manifest == null)
            {
                Debug.LogWarning("SceneGraph manifest not found.");
                return;
            }

            var sceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(scene.path);
            if (sceneGuid.Equals(default))
            {
                Debug.LogWarning($"Failed to resolve scene GUID for scene: {scene.path}");
                return;
            }

            var sections = manifest.GetSectionsForSubscene(sceneGuid) ?? new List<SectionDefinition>();
            var desired = new Dictionary<SectionAddress, SectionDefinition>();
            foreach (var section in sections)
            {
                if (!desired.ContainsKey(section.Address))
                {
                    desired.Add(section.Address, section);
                }
            }

            var existing = authoring.GetComponentsInChildren<SectionAuthoring>(true);
            var existingByAddress = new Dictionary<SectionAddress, SectionAuthoring>();
            var duplicates = new List<SectionAuthoring>();

            foreach (var section in existing)
            {
                if (existingByAddress.ContainsKey(section.Address))
                {
                    duplicates.Add(section);
                }
                else
                {
                    existingByAddress.Add(section.Address, section);
                }
            }

            foreach (var dup in duplicates)
            {
                Undo.DestroyObjectImmediate(dup.gameObject);
            }

            foreach (var kvp in existingByAddress)
            {
                if (!desired.ContainsKey(kvp.Key))
                {
                    Undo.DestroyObjectImmediate(kvp.Value.gameObject);
                }
            }

            foreach (var kvp in desired)
            {
                var address = kvp.Key;
                var entry = kvp.Value;
                var blobAsset = ResolveBlobAsset(entry);
                string desiredName = GetSectionObjectName(address);
                var sectionKey = SceneGraphMath.UnpackSectionId(address.SectionId);
                var desiredPosition = GetSectionBounds(sectionKey).center;
                if (!existingByAddress.TryGetValue(address, out var sectionAuthoring))
                {
                    var go = new GameObject(desiredName);
                    Undo.RegisterCreatedObjectUndo(go, "Create SectionAuthoring");
                    go.transform.SetParent(authoring.transform, false);
                    go.transform.position = desiredPosition;
                    sectionAuthoring = Undo.AddComponent<SectionAuthoring>(go);
                    sectionAuthoring.Initialize(address, blobAsset);
                    EditorUtility.SetDirty(sectionAuthoring);
                }
                else
                {
                    bool changed = false;
                    if (!sectionAuthoring.Address.Equals(address))
                    {
                        changed = true;
                    }

                    var currentBlob = sectionAuthoring.BlobAsset.isSet ? sectionAuthoring.BlobAsset.asset : null;
                    if (currentBlob != blobAsset)
                    {
                        changed = true;
                    }

                    if (changed)
                    {
                        Undo.RecordObject(sectionAuthoring, "Update SectionAuthoring");
                        sectionAuthoring.Initialize(address, blobAsset);
                        EditorUtility.SetDirty(sectionAuthoring);
                    }

                    if (sectionAuthoring.gameObject.name != desiredName)
                    {
                        Undo.RecordObject(sectionAuthoring.gameObject, "Rename SectionAuthoring");
                        sectionAuthoring.gameObject.name = desiredName;
                    }

                    if (sectionAuthoring.transform.parent != authoring.transform)
                    {
                        Undo.SetTransformParent(sectionAuthoring.transform, authoring.transform, "Reparent SectionAuthoring");
                    }

                    if ((sectionAuthoring.transform.position - desiredPosition).sqrMagnitude > 0.0001f)
                    {
                        Undo.RecordObject(sectionAuthoring.transform, "Move SectionAuthoring");
                        sectionAuthoring.transform.position = desiredPosition;
                    }

                }
            }

            EditorSceneManager.MarkSceneDirty(authoring.gameObject.scene);
        }

        private static string GetSectionObjectName(SectionAddress address)
        {
            return $"Section_{address.SectionId}";
        }

        private static Bounds GetSectionBounds(Unity.Mathematics.int3 sectionKey)
        {
            Vector3 min = new Vector3(
                sectionKey.x * SceneGraphConstants.SectionSizeX,
                sectionKey.y * SceneGraphConstants.SectionSizeY,
                sectionKey.z * SceneGraphConstants.SectionSizeZ);

            Vector3 size = new Vector3(
                SceneGraphConstants.SectionSizeX,
                SceneGraphConstants.SectionSizeY,
                SceneGraphConstants.SectionSizeZ);

            return new Bounds(min + size * 0.5f, size);
        }

        private static BlobAssetHandler ResolveBlobAsset(SectionDefinition entry)
        {
            var asset = entry.SectionBlob.GetEditorObject<BlobAssetHandler>();
            if (asset != null)
            {
                return asset;
            }

            if (!string.IsNullOrEmpty(entry.ResourceKey))
            {
                return Resources.Load<BlobAssetHandler>(entry.ResourceKey);
            }

            return null;
        }

        public static void RebuildManifest(Scene scene)
        {  
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("RebuildManifest requires a valid scene with an asset path.");
                return;
            }

            EntitiesHash128 sceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(scene.path);
            if (sceneGuid.Equals(default))
            {
                Debug.LogWarning($"Failed to resolve scene GUID for SubScene asset: {scene.path}");
                return;
            }

            var manifest = GetOrCreateManifest();
            RebuildManifestForSubscene(manifest, sceneGuid, scene.path);
        }

        public static void BakeScene(Scene targetScene, LayerMask geometryLayer, LayerMask obstacleLayer)
        {
            if (!targetScene.IsValid() || string.IsNullOrEmpty(targetScene.path))
            {
                Debug.LogWarning("BakeScene requires a valid scene with an asset path.");
                return;
            }

            string scenePath = targetScene.path;
            EntitiesHash128 sceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(scenePath);
            if (sceneGuid.Equals(default))
            {
                Debug.LogWarning($"Failed to resolve scene GUID for SubScene asset: {scenePath}");
                return;
            }

            var manifest = GetOrCreateManifest();
            
            // Ensure subscene definition exists
            if (!manifest.TryGetSubscene(sceneGuid, out _))
            {
                manifest.SetSubscene(new SubsceneDefinition(sceneGuid, scenePath));
            }

            // 1. Calculate World Bounds based on Geometry Layer
            // This can be expensive if we search every collider, but it's an editor tool.
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            Bounds worldBounds = new Bounds();
            bool hasBounds = false;

            int geomeLayerVal = geometryLayer.value;
            foreach (var col in colliders)
            {
                if (col.gameObject.scene != targetScene)
                {
                    continue;
                }

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

            // 2. Determine affected Sections
            // We need to convert Min/Max world points to Section Keys.
            // Note: Section 0,0,0 is at 0,0,0 world (usually). 
            // Min Key
            SceneGraphMath.WorldToGraph(worldBounds.min, out int3 minSection, out _, out _, out _);
            // Max Key
            SceneGraphMath.WorldToGraph(worldBounds.max, out int3 maxSection, out _, out _, out _);

            int totalSections = (maxSection.x - minSection.x + 1) * (maxSection.y - minSection.y + 1) * (maxSection.z - minSection.z + 1);
            if (totalSections > 100)
            {
                bool confirm = EditorUtility.DisplayDialog("Bulk Bake Warning", 
                    $"This will bake {totalSections} sections. It might take a while. Continue?", "Yes", "Cancel");
                if (!confirm) return;
            }

            var scannedSections = new Dictionary<int3, SectionBakeData>();
            var blobCache = new Dictionary<SectionAddress, BlobAssetHandler>();
            var bakedChunkCache = new Dictionary<ChunkAddress, EditorChunkHeights>();

            // 3. Scan geometry for all sections first
            int processed = 0;
            try 
            {
                for (int x = minSection.x; x <= maxSection.x; x++)
                {
                    for (int y = minSection.y; y <= maxSection.y; y++)
                    {
                        for (int z = minSection.z; z <= maxSection.z; z++)
                        {
                            int3 sectionKey = new int3(x, y, z);
                            if (!SectionHasGeometry(sectionKey, geometryLayer))
                            {
                                TryDeleteSectionAsset(sectionKey, sceneGuid);
                                processed++;
                                continue;
                            }

                            EditorUtility.DisplayProgressBar("Baking World", $"Scanning Section {sectionKey}", (float)processed / totalSections);
                            var activeChunks = ScanSectionGeometry(sectionKey, geometryLayer, obstacleLayer);
                            if (activeChunks.Count > 0)
                            {
                                scannedSections[sectionKey] = new SectionBakeData(sectionKey, activeChunks);
                            }
                            else
                            {
                                TryDeleteSectionAsset(sectionKey, sceneGuid);
                            }
                            processed++;
                        }
                    }
                }

                // 4. Build connectivity after all geometry is scanned
                int baked = 0;
                int totalToBake = scannedSections.Count;
                foreach (var kvp in scannedSections)
                {
                    EditorUtility.DisplayProgressBar("Baking World", $"Building Section {kvp.Key}", totalToBake == 0 ? 1f : (float)baked / totalToBake);
                    BuildAndWriteSection(kvp.Value, scannedSections, blobCache, bakedChunkCache, sceneGuid);
                    baked++;
                }
            }
            finally
            {
                RebuildManifestForSubscene(manifest, sceneGuid, scenePath);
                foreach (var kvp in blobCache)
                {
                    NavigationAssetProvider.Release(kvp.Key);
                }
                EditorUtility.ClearProgressBar();
            }
        }

        public static void BakeSection(int3 sectionKey, LayerMask geometryLayer, LayerMask obstacleLayer, EntitiesHash128 sceneGuid)
        {
            Debug.Log($"Baking Section {sectionKey} for Scene {sceneGuid}");
            var manifest = GetOrCreateManifest();
            if (!SectionHasGeometry(sectionKey, geometryLayer))
            {
                TryDeleteSectionAsset(sectionKey, sceneGuid);
                return;
            }

            var blobCache = new Dictionary<SectionAddress, BlobAssetHandler>();
            var bakedChunkCache = new Dictionary<ChunkAddress, EditorChunkHeights>();

            try
            {
                EditorUtility.DisplayProgressBar("Baking Section", "Scanning Geometry...", 0f);
                var activeChunks = ScanSectionGeometry(sectionKey, geometryLayer, obstacleLayer);
                if (activeChunks.Count == 0)
                {
                    TryDeleteSectionAsset(sectionKey, sceneGuid);
                    return;
                }

                var scannedSections = new Dictionary<int3, SectionBakeData>
                {
                    [sectionKey] = new SectionBakeData(sectionKey, activeChunks)
                };

                EditorUtility.DisplayProgressBar("Baking Section", "Building Blob...", 0.5f);
                BuildAndWriteSection(scannedSections[sectionKey], scannedSections, blobCache, bakedChunkCache, sceneGuid);
            }
            finally
            {
                RebuildManifestForSubscene(manifest, sceneGuid, string.Empty);
                foreach (var kvp in blobCache)
                {
                    NavigationAssetProvider.Release(kvp.Key);
                }
                EditorUtility.ClearProgressBar();
            }
        }

        private sealed class SectionBakeData
        {
            public int3 SectionKey;
            public Dictionary<ushort, EditorChunkHeights> ActiveChunks;

            public SectionBakeData(int3 sectionKey, Dictionary<ushort, EditorChunkHeights> activeChunks)
            {
                SectionKey = sectionKey;
                ActiveChunks = activeChunks;
            }
        }

        private static string GetSectionAssetPath(int3 sectionKey, EntitiesHash128 sceneGuid)
        {
            uint sectionIndex = SceneGraphMath.PackSectionId(sectionKey);
            string folderPath = $"Assets/Resources/SceneGraph/{sceneGuid}";
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            return $"{folderPath}/Section_{sectionIndex}.asset";
        }

        private static string GetSubsceneFolderPath(EntitiesHash128 sceneGuid)
        {
            return $"Assets/Resources/SceneGraph/{sceneGuid}";
        }

        private static SceneGraphManifest GetOrCreateManifest()
        {
            return EditorSettingsUtility.GetSettings<SceneGraphManifest>();
        }

        private static void RebuildManifestForSubscene(SceneGraphManifest manifest, EntitiesHash128 sceneGuid, string scenePath)
        {
            if (manifest == null)
            {
                return;
            }

            string folderPath = GetSubsceneFolderPath(sceneGuid);
            
            // Ensure subscene definition exists
            if (!manifest.TryGetSubscene(sceneGuid, out _))
            {
                manifest.SetSubscene(new SubsceneDefinition(sceneGuid, scenePath));
            }
            
            if (!Directory.Exists(folderPath))
            {
                manifest.SetSectionsForSubscene(sceneGuid, null);
                SaveManifest(manifest);
                return;
            }

            var entries = new List<SectionDefinition>();
            string[] guids = AssetDatabase.FindAssets("t:BlobAssetHandler", new[] { folderPath });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = Path.GetFileNameWithoutExtension(assetPath);
                if (!TryParseSectionIndex(fileName, out uint sectionIndex))
                {
                    continue;
                }

                var byteAsset = AssetDatabase.LoadAssetAtPath<BlobAssetHandler>(assetPath);
                var entry = new SectionDefinition
                {
                    Address = new SectionAddress(sceneGuid, sectionIndex),
                    ResourceKey = NavigationAssetProvider.GetResourceKey(sceneGuid, sectionIndex),
                    SectionBlob = new WeakObjectReference<BlobAssetHandler>(byteAsset)
                };
                entries.Add(entry);
            }

            manifest.SetSectionsForSubscene(sceneGuid, entries);
            SaveManifest(manifest);
        }

        private static bool TryParseSectionIndex(string fileName, out uint sectionIndex)
        {
            sectionIndex = 0;
            const string prefix = "Section_";
            if (!fileName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string number = fileName.Substring(prefix.Length);
            return uint.TryParse(number, out sectionIndex);
        }

        private static void TryDeleteSectionAsset(int3 sectionKey, EntitiesHash128 sceneGuid)
        {
            string assetPath = GetSectionAssetPath(sectionKey, sceneGuid);
            if (AssetDatabase.LoadAssetAtPath<BlobAssetHandler>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private static bool SectionHasGeometry(int3 sectionKey, LayerMask geometryLayer)
        {
            float3 rOrigin = new float3(
                sectionKey.x * SceneGraphConstants.SectionSizeX,
                sectionKey.y * SceneGraphConstants.SectionSizeY,
                sectionKey.z * SceneGraphConstants.SectionSizeZ);

            Vector3 rCenter = rOrigin + new float3(
                SceneGraphConstants.SectionSizeX * 0.5f,
                SceneGraphConstants.SectionSizeY * 0.5f,
                SceneGraphConstants.SectionSizeZ * 0.5f);

            Vector3 rHalfExtents = new Vector3(
                SceneGraphConstants.SectionSizeX * 0.5f,
                SceneGraphConstants.SectionSizeY * 0.5f,
                SceneGraphConstants.SectionSizeZ * 0.5f);

            return Physics.CheckBox(rCenter, rHalfExtents + Vector3.one * 0.1f, Quaternion.identity, geometryLayer);
        }

        private static Dictionary<ushort, EditorChunkHeights> ScanSectionGeometry(int3 sectionKey, LayerMask geometryLayer, LayerMask obstacleLayer)
        {
            var activeChunks = new Dictionary<ushort, EditorChunkHeights>();
            var validChunkMortons = new List<ushort>();

            for (int cz = 0; cz < SceneGraphConstants.SectionSizeChunksZ; cz++)
            {
                for (int cy = 0; cy < SceneGraphConstants.SectionSizeChunksY; cy++)
                {
                    for (int cx = 0; cx < SceneGraphConstants.SectionSizeChunksX; cx++)
                    {
                        int3 cIdx = new int3(cx, cy, cz);
                        float3 origin = SceneGraphMath.GraphToWorldBase(sectionKey, cIdx, int2.zero);
                        Vector3 center = origin + new float3(SceneGraphConstants.ChunkSizeX * 0.5f, SceneGraphConstants.ChunkHeight * 0.5f, SceneGraphConstants.ChunkSizeZ * 0.5f);
                        Vector3 halfExtents = new Vector3(SceneGraphConstants.ChunkSizeX * 0.5f, SceneGraphConstants.ChunkHeight * 0.5f, SceneGraphConstants.ChunkSizeZ * 0.5f);

                        if (!Physics.CheckBox(center, halfExtents + Vector3.one * 0.1f, Quaternion.identity, geometryLayer))
                            continue;

                        ushort morton = SceneGraphMath.EncodeChunkToMorton(cIdx);
                        var data = new EditorChunkHeights { Heights = new float[SceneGraphConstants.NodesPerChunk] };
                        for (int i = 0; i < data.Heights.Length; i++) data.Heights[i] = float.NaN;
                        activeChunks[morton] = data;
                        validChunkMortons.Add(morton);
                    }
                }
            }

            int totalNodes = validChunkMortons.Count * SceneGraphConstants.NodesPerChunk;
            if (totalNodes == 0)
                return activeChunks;

            var nodeChunkMortons = new ushort[totalNodes];
            var nodeMortons = new byte[totalNodes];

            var commands = new NativeArray<RaycastCommand>(totalNodes, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(totalNodes, Allocator.TempJob);

            int idx = 0;
            foreach (ushort chunkMorton in validChunkMortons)
            {
                int3 chunkIdx = SceneGraphMath.DecodeMortonToChunk(chunkMorton);
                for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
                {
                    int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
                    float3 nodeWorldPos = SceneGraphMath.GraphToWorldBase(sectionKey, chunkIdx, nodeIdx);
                    float rayOriginY = nodeWorldPos.y + SceneGraphConstants.ChunkHeight;
                    Vector3 rayOrigin = new Vector3(nodeWorldPos.x, rayOriginY, nodeWorldPos.z);

                    commands[idx] = new RaycastCommand(rayOrigin, Vector3.down, new QueryParameters(geometryLayer), SceneGraphConstants.ChunkHeight);
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

        private static void BuildAndWriteSection(
            SectionBakeData sectionData,
            Dictionary<int3, SectionBakeData> scannedSections,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            Dictionary<ChunkAddress, EditorChunkHeights> bakedChunkCache,
            EntitiesHash128 sceneGuid)
        {
            string assetPath = GetSectionAssetPath(sectionData.SectionKey, sceneGuid);

            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref Section sectionBlob = ref builder.ConstructRoot<Section>();
                sectionBlob.MortonCode = 0;

                var sortedKeys = sectionData.ActiveChunks.Keys.ToList();
                sortedKeys.Sort();

                int validCount = sortedKeys.Count;
                var chunksArray = builder.Allocate(ref sectionBlob.Chunks, validCount);

                int maxMorton = 32768;
                var lookupArray = builder.Allocate(ref sectionBlob.ChunkLookup, maxMorton);
                for (int i = 0; i < maxMorton; i++) lookupArray[i] = -1;

                for (int i = 0; i < validCount; i++)
                {
                    ushort morton = sortedKeys[i];
                    EditorChunkHeights rawData = sectionData.ActiveChunks[morton];

                    lookupArray[morton] = (short)i;
                    chunksArray[i].MortonCode = morton;
                    var nodesArray = builder.Allocate(ref chunksArray[i].Nodes, SceneGraphConstants.NodesPerChunk);

                    ulong[] calculatedFlags = new ulong[SceneGraphConstants.NodesPerChunk];
                    CalculateConnectivityForChunk(sectionData.SectionKey, SceneGraphMath.DecodeMortonToChunk(morton), scannedSections, blobCache, bakedChunkCache, sceneGuid, rawData.Heights, ref calculatedFlags);

                    for (int n = 0; n < SceneGraphConstants.NodesPerChunk; n++)
                    {
                        nodesArray[n].Y = rawData.Heights[n];
                        nodesArray[n].ExitMask = (MovementFlags)calculatedFlags[n];
                    }
                }

                using (var writer = new MemoryBinaryWriter())
                {
                    BlobAssetReference<Section>.Write(writer, builder, 0);
                    SaveSectionAsset(assetPath, writer);
                }
            }

            uint sectionIndex = SceneGraphMath.PackSectionId(sectionData.SectionKey);
            var sectionAddress = new SectionAddress(sceneGuid, sectionIndex);
            Debug.Log($"Baked Section {sectionData.SectionKey} to {assetPath} at Address {sectionAddress}");
            NavigationAssetProvider.ForceReloadOfBlobAsset(sectionAddress);
        }

        private static unsafe void SaveSectionAsset(string assetPath, MemoryBinaryWriter writer)
        {
            var asset = AssetDatabase.LoadAssetAtPath<BlobAssetHandler>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<BlobAssetHandler>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            var bytes = new byte[writer.Length];
            if (writer.Length > 0)
            {
                fixed (byte* dst = bytes)
                {
                    UnsafeUtility.MemCpy(dst, writer.Data, writer.Length);
                }
            }

            asset.UpdateData(bytes);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        private static void SaveManifest(SceneGraphManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
        }

        private static void CalculateConnectivityForChunk(
            int3 r,
            int3 c,
            Dictionary<int3, SectionBakeData> scannedSections,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            Dictionary<ChunkAddress, EditorChunkHeights> bakedChunkCache,
            EntitiesHash128 sceneGuid,
            float[] heights,
            ref ulong[] outFlags)
        {
            long baseGlobalZ = (long)r.z * SceneGraphConstants.SectionSizeChunksZ * SceneGraphConstants.ChunkSizeNodesZ 
                           + (long)c.z * SceneGraphConstants.ChunkSizeNodesZ;

            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
            {
                float h = heights[i];
                if (float.IsNaN(h))
                {
                    outFlags[i] = (ulong)MovementFlags.Unreachable;
                    continue;
                }
                
                int2 node = SceneGraphMath.DecodeMortonToNode((byte)i);
                
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
                    int3 nSection = r;
                    
                    if (nNode.x < 0) { nNode.x += SceneGraphConstants.ChunkSizeNodesX; nChunk.x--; }
                    else if (nNode.x >= SceneGraphConstants.ChunkSizeNodesX) { nNode.x -= SceneGraphConstants.ChunkSizeNodesX; nChunk.x++; }

                    if (nNode.y < 0) { nNode.y += SceneGraphConstants.ChunkSizeNodesZ; nChunk.z--; }
                    else if (nNode.y >= SceneGraphConstants.ChunkSizeNodesZ) { nNode.y -= SceneGraphConstants.ChunkSizeNodesZ; nChunk.z++; }

                    if (nChunk.x < 0) { nChunk.x += SceneGraphConstants.SectionSizeChunksX; nSection.x--; }
                    else if (nChunk.x >= SceneGraphConstants.SectionSizeChunksX) { nChunk.x -= SceneGraphConstants.SectionSizeChunksX; nSection.x++; }

                    if (nChunk.z < 0) { nChunk.z += SceneGraphConstants.SectionSizeChunksZ; nSection.z--; }
                    else if (nChunk.z >= SceneGraphConstants.SectionSizeChunksZ) { nChunk.z -= SceneGraphConstants.SectionSizeChunksZ; nSection.z++; }

                    for (int dy = 1; dy >= -1; dy--)
                    {
                        int3 checkC = nChunk;
                        int3 checkR = nSection;
                        checkC.y += dy;
                        if (checkC.y >= SceneGraphConstants.SectionSizeChunksY) { checkC.y -= SceneGraphConstants.SectionSizeChunksY; checkR.y++; }
                        else if (checkC.y < 0) { checkC.y += SceneGraphConstants.SectionSizeChunksY; checkR.y--; }

                        if (TryGetChunkData(checkR, checkC, scannedSections, blobCache, bakedChunkCache, sceneGuid, out EditorChunkHeights nData))
                        {
                            int nFlat = SceneGraphMath.EncodeNodeToMorton(nNode);
                            float nh = nData.Heights[nFlat];
                            if (!float.IsNaN(nh) && Mathf.Abs(nh - h) <= SceneGraphConstants.MaxSlopeHeight)
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
            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
            {
                float h = heights[i];
                if (float.IsNaN(h))
                    continue;

                ulong f = outFlags[i];
                if (CountSetBits(f) == 6)
                    continue; // Is Core

                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
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
                    if (GetNeighborNodeCoords(r, c, targetNode, out int3 nSection, out int3 nChunk, out int2 nNode))
                    {
                        if (IsNodeCore(nSection, nChunk, nNode, scannedSections, blobCache, bakedChunkCache, sceneGuid))
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
            int3 section,
            int3 chunk,
            int2 node,
            Dictionary<int3, SectionBakeData> scannedSections,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            Dictionary<ChunkAddress, EditorChunkHeights> bakedChunkCache,
            EntitiesHash128 sceneGuid)
        {
            if (TryGetChunkData(section, chunk, scannedSections, blobCache, bakedChunkCache, sceneGuid, out EditorChunkHeights data))
            {
                int flat = SceneGraphMath.EncodeNodeToMorton(node);
                float h = data.Heights[flat];
                if (!float.IsNaN(h))
                {
                    return CountSetBits(GetHexConnectivityFlags(section, chunk, node, h, scannedSections, blobCache, bakedChunkCache, sceneGuid)) == 6;
                }
            }
            return false;
        }

        private static ulong GetHexConnectivityFlags(
            int3 section,
            int3 chunk,
            int2 nodeIdx,
            float currentY,
            Dictionary<int3, SectionBakeData> scannedSections,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            Dictionary<ChunkAddress, EditorChunkHeights> bakedChunkCache,
            EntitiesHash128 sceneGuid)
        {
            ulong flags = 0;
            long globalZ = (long)section.z * SceneGraphConstants.SectionSizeChunksZ * SceneGraphConstants.ChunkSizeNodesZ
                           + (long)chunk.z * SceneGraphConstants.ChunkSizeNodesZ + nodeIdx.y;
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
                if (GetNeighborNodeCoords(section, chunk, targetNode, out int3 nSection, out int3 nChunk, out int2 nNode))
                {
                    for (int dy = 1; dy >= -1; dy--)
                    {
                        int3 checkC = nChunk;
                        int3 checkR = nSection;
                        checkC.y += dy;
                        if (checkC.y >= SceneGraphConstants.SectionSizeChunksY) { checkC.y -= SceneGraphConstants.SectionSizeChunksY; checkR.y++; }
                        else if (checkC.y < 0) { checkC.y += SceneGraphConstants.SectionSizeChunksY; checkR.y--; }

                        if (TryGetChunkData(checkR, checkC, scannedSections, blobCache, bakedChunkCache, sceneGuid, out EditorChunkHeights nData))
                        {
                            int nFlat = SceneGraphMath.EncodeNodeToMorton(nNode);
                            float nh = nData.Heights[nFlat];
                            if (!float.IsNaN(nh) && Mathf.Abs(nh - currentY) <= SceneGraphConstants.MaxSlopeHeight)
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

        private static bool GetNeighborNodeCoords(int3 section, int3 chunk, int2 targetNode, out int3 outSection, out int3 outChunk, out int2 outNode)
        {
            outSection = section;
            outChunk = chunk;
            outNode = targetNode;

            if (outNode.x < 0) { outNode.x += SceneGraphConstants.ChunkSizeNodesX; outChunk.x--; }
            else if (outNode.x >= SceneGraphConstants.ChunkSizeNodesX) { outNode.x -= SceneGraphConstants.ChunkSizeNodesX; outChunk.x++; }

            if (outNode.y < 0) { outNode.y += SceneGraphConstants.ChunkSizeNodesZ; outChunk.z--; }
            else if (outNode.y >= SceneGraphConstants.ChunkSizeNodesZ) { outNode.y -= SceneGraphConstants.ChunkSizeNodesZ; outChunk.z++; }

            if (outChunk.x < 0) { outChunk.x += SceneGraphConstants.SectionSizeChunksX; outSection.x--; }
            else if (outChunk.x >= SceneGraphConstants.SectionSizeChunksX) { outChunk.x -= SceneGraphConstants.SectionSizeChunksX; outSection.x++; }

            if (outChunk.z < 0) { outChunk.z += SceneGraphConstants.SectionSizeChunksZ; outSection.z--; }
            else if (outChunk.z >= SceneGraphConstants.SectionSizeChunksZ) { outChunk.z -= SceneGraphConstants.SectionSizeChunksZ; outSection.z++; }

            return true;
        }

        private static bool TryGetChunkData(
            int3 section,
            int3 chunk,
            Dictionary<int3, SectionBakeData> scannedSections,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            Dictionary<ChunkAddress, EditorChunkHeights> bakedChunkCache,
            EntitiesHash128 sceneGuid,
            out EditorChunkHeights data)
        {
            ChunkAddress chunkAddress = SceneGraphMath.GetChunkAddress(sceneGuid, section, chunk);
            if (bakedChunkCache.TryGetValue(chunkAddress, out data))
                return true;

            if (scannedSections.TryGetValue(section, out var sectionData))
            {
                ushort morton = SceneGraphMath.EncodeChunkToMorton(chunk);
                if (sectionData.ActiveChunks.TryGetValue(morton, out data))
                {
                    bakedChunkCache[chunkAddress] = data;
                    return true;
                }
            }

            if (TryGetChunkDataFromBlob(section, chunk, blobCache, sceneGuid, out data))
            {
                bakedChunkCache[chunkAddress] = data;
                return true;
            }

            return false;
        }

        private static bool TryGetChunkDataFromBlob(
            int3 section,
            int3 chunk,
            Dictionary<SectionAddress, BlobAssetHandler> blobCache,
            EntitiesHash128 sceneGuid,
            out EditorChunkHeights data)
        {
            data = default;
            uint sectionIndex = SceneGraphMath.PackSectionId(section);
            var sectionAddress = new SectionAddress(sceneGuid, sectionIndex);
            if (!blobCache.TryGetValue(sectionAddress, out var handler))
            {
                handler = NavigationAssetProvider.CheckOut(sectionAddress);
                if (handler == null || !handler.IsCreated)
                    return false;
                blobCache[sectionAddress] = handler;
            }

            if (handler == null || !handler.IsCreated)
            {
                if (handler != null)
                {
                    blobCache.Remove(sectionAddress);
                    NavigationAssetProvider.Release(sectionAddress);
                }
                return false;
            }

            ref Section r = ref handler.Value;
            ushort morton = SceneGraphMath.EncodeChunkToMorton(chunk);
            if (morton < r.ChunkLookup.Length)
            {
                short idx = r.ChunkLookup[morton];
                if (idx != -1 && idx < r.Chunks.Length)
                {
                    var heights = new float[SceneGraphConstants.NodesPerChunk];
                    ref Chunk c = ref r.Chunks[idx];
                    for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
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



