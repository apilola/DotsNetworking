using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Mathematics;
using DotsNetworking.WorldGraph.Utils;
using DotsNetworking.WorldGraph;
using UnityEditorInternal;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using System.Linq;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;
using System.Reflection;

namespace DotsNetworking.WorldGraph.Editor
{
    [Overlay(typeof(SceneView), "World Graph", true)]
    public class WorldGraphDebugOverlay : Overlay
    {
        // --- Settings (Static for persistence across SceneViews) ---
        public static bool EnableMouseHover = true;
        public static bool ShowRegionBounds = false;
        public static bool VisualizeBaked = false;

        // --- Cache Logic ---
        
        // Region packed with Chunk Address -> Data
        private static Dictionary<ulong, EditorChunkData> _chunksCache = new Dictionary<ulong, EditorChunkData>();
        
        // Blob Cache - keyed by packed region address (ulong)
        private static Dictionary<ulong, BlobAssetHandle<Region>> _loadedHandles = new Dictionary<ulong, BlobAssetHandle<Region>>();

        // Hover State
        private static ulong _lastHoveredAddress = ulong.MaxValue;
        private static int _hoverFrameCount = 0;
        private static bool _isHoverValid = false;
        private static int3 _hoverRegionKey;
        private static int3 _hoverChunkIdx;
        private static Vector3 _hoverPoint;
        private static Vector3 _hoverNormal;

        WorldGraphEditorSettings settings;
        UnityEditor.Editor worldGraphSettingsEditor;
        public override void OnCreated()
        {
            base.OnCreated();
            settings = WorldGraphEditorSettings.instance;
            UnityEditor.Editor.CreateEditor(settings);
        }

        public override void OnWillBeDestroyed()
        {
            base.OnWillBeDestroyed();
            UnityEngine.Object.DestroyImmediate(worldGraphSettingsEditor);
        }

        // --- Overlay UI ---
        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement() { style = { minWidth = 220, paddingBottom = 5, paddingTop = 5, paddingLeft = 5, paddingRight = 5 } };

            // --- Settings Binding ---
            root.Add(new IMGUIContainer(() =>
            {
                if (worldGraphSettingsEditor == null)
                    worldGraphSettingsEditor = UnityEditor.Editor.CreateEditor(settings);

                worldGraphSettingsEditor.serializedObject.Update();

                SerializedProperty prop = worldGraphSettingsEditor.serializedObject.GetIterator();
                prop.NextVisible(true); // Skip script reference
                while (prop.NextVisible(false))
                {
                    EditorGUILayout.PropertyField(prop, true);
                }
                if(worldGraphSettingsEditor.serializedObject.ApplyModifiedProperties())
                {
                    settings.SaveSettings();
                }
            }));

            // Spacer
            root.Add(new VisualElement() { style = { height = 8 } });

            // --- Debug Foldout ---
            var debugFoldout = new Foldout() { text = "Debug Options", value = true };
            root.Add(debugFoldout);

            // Toggle: Enable Mouse Hover
            var toggleHover = new Toggle("Enable Mouse Hover") { value = EnableMouseHover };
            toggleHover.RegisterValueChangedCallback(evt => {
                EnableMouseHover = evt.newValue;
                SceneView.RepaintAll();
            });
            debugFoldout.Add(toggleHover);

            // Toggle: Show Region Bounds
            var toggleBounds = new Toggle("Show Region Bounds") { value = ShowRegionBounds };
            toggleBounds.RegisterValueChangedCallback(evt => {
                ShowRegionBounds = evt.newValue;
                SceneView.RepaintAll();
            });
            debugFoldout.Add(toggleBounds);

            var toggleBaked = new Toggle("Visualize Baked Geometry") { value = VisualizeBaked };
            toggleBaked.RegisterValueChangedCallback(evt =>
            {
                VisualizeBaked = evt.newValue;
                SceneView.RepaintAll();
            });
            debugFoldout.Add(toggleBaked);

            // Button: Clear Cache
            var btnClear = new Button(() => { ClearCache(); SceneView.RepaintAll(); }) { text = "Clear Cache", style = { marginTop = 5 } };
            debugFoldout.Add(btnClear);

            return root;
        }

        public static void ClearCache()
        {
            _chunksCache.Clear();
            _lastHoveredAddress = ulong.MaxValue;
            
            // Clear Blob Cache
            foreach (var kvp in _loadedHandles) NavigationAssetProvider.Release(kvp.Key);
            _loadedHandles.Clear();
        }

        // --- Lifecycle & Drawing ---
        
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (!EnableMouseHover && !ShowRegionBounds) return;

            Event e = Event.current;
            
            // Draw Bounds regardless of hover if enabled
            if (ShowRegionBounds)
            {
                int3 regionToDraw = _hoverRegionKey;
                if (!_isHoverValid)
                {
                    WorldGraphMath.WorldToGraph(view.camera.transform.position, out regionToDraw, out _, out _, out _);
                }
                DrawRegionGrid(regionToDraw);
            }

            if (EnableMouseHover)
            {
                // Logic mostly follows old UpdateHoverTarget but static
                if (e.type == EventType.MouseMove)
                {
                    UpdateHoverTarget(e);
                    view.Repaint();
                }

                if (e.type == EventType.Repaint)
                {
                    DrawHoverTarget();
                }
            }
        }

        private static void UpdateHoverTarget(Event e)
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (Physics.Raycast(mouseRay, out RaycastHit hit, 1000f, WorldGraphEditorSettings.instance.GeometryLayer))
            {
                _isHoverValid = true;
                _hoverPoint = hit.point;
                _hoverNormal = hit.normal;
                WorldGraphMath.WorldToGraph(hit.point, out _hoverRegionKey, out _hoverChunkIdx, out _, out _);
            }
            else
            {
                _isHoverValid = false;
            }
        }

        private static void DrawRegionGrid(int3 regionKey)
        {
             float3 regionOrigin = new float3(
                regionKey.x * WorldGraphConstants.RegionSizeX,
                regionKey.y * WorldGraphConstants.RegionSizeY,
                regionKey.z * WorldGraphConstants.RegionSizeZ
            );
            
            Handles.color = new Color(1, .5f, 1, 1f);
            Handles.DrawWireCube(
                regionOrigin + new float3(WorldGraphConstants.RegionSizeX/2, WorldGraphConstants.RegionSizeY/2, WorldGraphConstants.RegionSizeZ/2),
                new Vector3(WorldGraphConstants.RegionSizeX, WorldGraphConstants.RegionSizeY, WorldGraphConstants.RegionSizeZ)
            );
            Handles.Label(regionOrigin + new float3(0, WorldGraphConstants.RegionSizeY, 0), $"Region {regionKey}");
        }

        private static void DrawHoverTarget()
        {
            if (!_isHoverValid) return;

            // Draw Mouse Hit
            Handles.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Handles.DrawSolidDisc(_hoverPoint, _hoverNormal, 0.1f);
            
            int3 drawRegion = _hoverRegionKey;
            int3 drawChunk = _hoverChunkIdx;

            // Snap
            WorldGraphMath.WorldToGraph(_hoverPoint, out _, out _, out int2 nodeIdx, out _);
            float3 latticePos = WorldGraphMath.GraphToWorldBase(drawRegion, drawChunk, nodeIdx);
            latticePos.y = _hoverPoint.y; 

            Handles.color = Color.cyan;
            Handles.DrawWireDisc(latticePos, Vector3.up, 0.2f); 

            // Offset Debugging
            long globalZ = (long)drawRegion.z * WorldGraphConstants.RegionSizeChunksZ * WorldGraphConstants.ChunkSizeNodesZ 
                           + (long)drawChunk.z * WorldGraphConstants.ChunkSizeNodesZ + nodeIdx.y;
            bool isOddRow = (globalZ & 1) != 0;
            int2[] offsets = isOddRow 
                ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };

            Handles.color = new Color(1, 1, 0, 0.5f);
            foreach (var off in offsets)
            {
                float3 nPos = WorldGraphMath.GraphToWorldBase(drawRegion, drawChunk, nodeIdx + off);
                nPos.y = latticePos.y; 
                Handles.DrawLine(latticePos, nPos);
            }

            DrawChunkBounds(drawRegion, drawChunk);
            UpdateAndDrawChunk(drawRegion, drawChunk, WorldGraphEditorSettings.instance.GeometryLayer, WorldGraphEditorSettings.instance.ObstacleLayer);
            DrawNeighbors(drawRegion, drawChunk);
        }

        private static void DrawNeighbors(int3 currentReg, int3 currentChunk)
        {
             for (int x = -1; x <= 1; x++)
            {
                for (int y = 1; y >= -1; y--) // Top-Down render priority
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue; 

                        int3 nCh = currentChunk + new int3(x, y, z);
                        int3 nReg = currentReg; 
                        
                        // Wrapping logic
                        if (nCh.x < 0) { nReg.x--; nCh.x = WorldGraphConstants.RegionSizeChunksX - 1; }
                        else if (nCh.x >= WorldGraphConstants.RegionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = WorldGraphConstants.RegionSizeChunksY - 1; }
                        else if (nCh.y >= WorldGraphConstants.RegionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = WorldGraphConstants.RegionSizeChunksZ - 1; }
                        else if (nCh.z >= WorldGraphConstants.RegionSizeChunksZ) { nReg.z++; nCh.z = 0; }

                        if (TryGetChunkOrBlob(nReg, nCh, out EditorChunkData chunkData))
                        {
                            // Ensure Baked
                            if (!chunkData.IsConnectivityCalculated) 
                                BakeChunkConnectivity(nReg, nCh, chunkData);

                            Handles.color = new Color(0, 1, 0, 0.15f); // Faint
                            
                            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
                            {
                                float h = chunkData.Heights[i];
                                if (float.IsNaN(h)) continue;
                                if (chunkData.Flags[i].HasFlag(MovementFlags.Unreachable)) continue;

                                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                                int cx = nodeIdx.x;
                                int cz = nodeIdx.y;

                                float3 pos = WorldGraphMath.GraphToWorldBase(nReg, nCh, new int2(cx, cz));
                                pos.y = h;
                                Handles.DrawSolidDisc(pos, Vector3.up, WorldGraphConstants.NodeSize * 0.05f);
                            }
                        }
                    }
                }
            }
        }

        private static void DrawChunkBounds(int3 regionKey, int3 chunkIdx)
        {
            float3 chunkBase = new float3(
                regionKey.x * WorldGraphConstants.RegionSizeX + chunkIdx.x * WorldGraphConstants.ChunkSizeX,
                regionKey.y * WorldGraphConstants.RegionSizeY + chunkIdx.y * WorldGraphConstants.ChunkHeight,
                regionKey.z * WorldGraphConstants.RegionSizeZ + chunkIdx.z * WorldGraphConstants.ChunkSizeZ
            );
            Vector3 center = chunkBase + new float3(WorldGraphConstants.ChunkSizeX / 2, WorldGraphConstants.ChunkHeight / 2, WorldGraphConstants.ChunkSizeZ / 2);
            Vector3 size = new Vector3(WorldGraphConstants.ChunkSizeX, WorldGraphConstants.ChunkHeight, WorldGraphConstants.ChunkSizeZ);

            Handles.color = Color.yellow;
            Handles.DrawWireCube(center, size);
            Handles.Label(center + Vector3.up * 2, $"Chk: {chunkIdx}");           

        }

        private static void UpdateAndDrawChunk(int3 regionKey, int3 chunkIdx, LayerMask groundMask, LayerMask obstacleMask)
        {
            ulong address = WorldGraphMath.PackChunkAddress(regionKey, chunkIdx);

            // Preload neighbor baked chunks if not already cached to reduce false positives.
            PreloadNeighborChunksFromBlob(regionKey, chunkIdx);
            
            if (_lastHoveredAddress != address)
            {
                _lastHoveredAddress = address;
                _hoverFrameCount = 0;
                
                RemoveChunkFromCache(address);
            }
            else
            {
                if (Event.current.type == EventType.Repaint)
                {
                    _hoverFrameCount++;
                    RemoveChunkFromCache(address);
                }
            }

            if (!TryGetChunk(address, out EditorChunkData chunkData))
            {
                chunkData = new EditorChunkData();
                ScanChunk(regionKey, chunkIdx, groundMask, obstacleMask, chunkData);
                AddChunkToCache(address, chunkData);
            }

             // Blob Check
            bool usingBlob = TryGetBlobChunk(regionKey, chunkIdx, out EditorChunkData blobData);
            bool isGeomDirty = false;

            if (!chunkData.IsConnectivityCalculated)
            {
                BakeChunkConnectivity(regionKey, chunkIdx, chunkData);
            }

            if (usingBlob)
            {
                isGeomDirty = IsChunkDifferent(chunkData, blobData);
                if(VisualizeBaked)
                {
                    // Draw Baked instead of Live
                    chunkData = blobData;
                }    
            }


            // Color Selection: 
            // - Yellow = Live Only (No Bake)
            // - Green = Synced (Live == Bake)
            // - Red = Dirty (Live != Bake)
            
            if (usingBlob)
                Handles.color = isGeomDirty ? new Color(1, .2f, .2f, 0.5f) : new Color(0, 1, 0, 0.5f);
            else
                Handles.color = new Color(1, 1, 0, 0.5f);


            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                float h = chunkData.Heights[i];
                //if (float.IsNaN(h)) continue;
                if (chunkData.Flags[i].HasFlag(MovementFlags.Unreachable)) continue;

                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                int x = nodeIdx.x;
                int z = nodeIdx.y;
                
                float3 pos = WorldGraphMath.GraphToWorldBase(regionKey, chunkIdx, new int2(x, z));
                pos.y = h;
                Handles.DrawSolidDisc(pos, Vector3.up, WorldGraphConstants.NodeSize * 0.05f);
            }
        }
        
        private static unsafe bool TryGetBlobChunk(int3 region, int3 chunk, out EditorChunkData data)
        {
            data = null;
            ulong regionAddress = WorldGraphMath.PackChunkAddress(region, int3.zero, WorldGraphEditorSettings.instance.GetCurrentSceneWorldId());
            
            if (!_loadedHandles.ContainsKey(regionAddress))
            {
                var handle = NavigationAssetProvider.CheckOut(regionAddress);
                if (handle.IsValid)
                {
                    _loadedHandles[regionAddress] = handle;
                }
            }

            if (_loadedHandles.TryGetValue(regionAddress, out var rHandle))
            {
                if (!rHandle.IsValid)
                {
                    _loadedHandles.Remove(regionAddress);
                    return false;
                }

                ref Region r = ref rHandle.Blob.Value;
                ushort morton = WorldGraphMath.EncodeChunkToMorton(chunk);
            
                // Check Bounds
                if (morton < r.ChunkLookup.Length) 
                {
                     short idx = r.ChunkLookup[morton];
                     if (idx != -1 && idx < r.Chunks.Length)
                     {
                         EditorChunkData d = new EditorChunkData();
                         ref Chunk c = ref r.Chunks[idx];
                     
                         for(int i=0; i<WorldGraphConstants.NodesPerChunk; i++)
                         {
                             d.Heights[i] = c.Nodes[i].Y;
                             d.Flags[i] = c.Nodes[i].ExitMask;
                         }
                         d.IsConnectivityCalculated = true; // Cached blobs are pre-calc
                         data = d;
                         return true;
                     }
                }
            }
            return false;
        }

        private static bool IsChunkDifferent(EditorChunkData a, EditorChunkData b)
        {
            int mismatchCount = 0;
            for(int i=0; i<WorldGraphConstants.NodesPerChunk; i++)
            {
                float h1 = a.Heights[i];
                float h2 = b.Heights[i];
                bool n1 = float.IsNaN(h1);
                bool n2 = float.IsNaN(h2);
                if (n1 != n2)
                {
                    if(n1)
                    {
                        var pos = WorldGraphMath.GraphToWorldBase(_hoverRegionKey, _hoverChunkIdx, WorldGraphMath.DecodeMortonToNode((byte)i));
                        Handles.color = Color.magenta;
                        Handles.DrawSolidDisc(new float3(pos.x, pos.y, pos.z), Vector3.up, 0.05f); // Prevent unused warning
                    }
                    mismatchCount++;
                }
                else if (!n1 && Mathf.Abs(h1 - h2) > 0.05f)
                {
                    mismatchCount++;
                }else if (!n1 && a.Flags[i] != b.Flags[i])
                {                    //Debug.Log("Region " + _hoverRegionKey + " Chunk " + _hoverChunkIdx + " Node " + WorldGraphMath.DecodeMortonToNode((byte)i) + " Flag Mismatch: Live=" + a.Flags[i] + " Blob=" + b.Flags[i]);
                    var pos = WorldGraphMath.GraphToWorldBase(_hoverRegionKey, _hoverChunkIdx, WorldGraphMath.DecodeMortonToNode((byte)i));
                    Handles.color = Color.blue;
                    Handles.DrawSolidDisc(new float3(pos.x, h2, pos.z), Vector3.up, 0.05f); // Prevent unused warning
                    mismatchCount++;
                }
            }
            return mismatchCount > 0;
        }

        // --- Logic ---

        private static void BakeChunkConnectivity(int3 region, int3 chunk, EditorChunkData data)
        {
            // Pass 1: Raw existence
            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                if (float.IsNaN(data.Heights[i])) 
                {
                    data.Flags[i] = MovementFlags.Unreachable;
                    continue;
                }
                
                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                int x = nodeIdx.x;
                int z = nodeIdx.y;

                ulong flags = GetHexConnectivityFlags(region, chunk, new int2(x, z), data.Heights[i]);
                data.Flags[i] = (MovementFlags)flags; 
            }

            // Pass 2: Core or Neighbor-of-Core logic
            long baseGlobalZ = (long)region.z * WorldGraphConstants.RegionSizeChunksZ * WorldGraphConstants.ChunkSizeNodesZ 
                           + (long)chunk.z * WorldGraphConstants.ChunkSizeNodesZ;
            
            for (int i = 0; i < WorldGraphConstants.NodesPerChunk; i++)
            {
                if (float.IsNaN(data.Heights[i])) continue;
                
                ulong f = (ulong)data.Flags[i];
                if (CountSetBits(f) == 6) continue; // Is Core

                // Check neighbors for Core
                bool hasCoreNeighbor = false;
                
                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                int x = nodeIdx.x;
                int z = nodeIdx.y;
                
                bool isOddRow = ((baseGlobalZ + z) & 1) != 0;
                int2[] offsets = isOddRow 
                    ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                    : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };

                // Maps direction bits to offsets
                 ulong[] dirMasks = isOddRow
                    ? new ulong[] { (ulong)MovementFlags.NW, (ulong)MovementFlags.NE, (ulong)MovementFlags.SW, (ulong)MovementFlags.SE, (ulong)MovementFlags.W, (ulong)MovementFlags.E }
                    : new ulong[] { (ulong)MovementFlags.NE, (ulong)MovementFlags.NW, (ulong)MovementFlags.SE, (ulong)MovementFlags.SW, (ulong)MovementFlags.W, (ulong)MovementFlags.E };

                for (int k = 0; k < 6; k++)
                {
                    if ((f & dirMasks[k]) != 0) // Valid neighbor connection
                    {
                        if (GetNeighborWorldPos(region, chunk, new int2(x, z) + offsets[k], data.Heights[i], out _, out int3 nR, out int3 nC, out int2 nN))
                        {
                            if (IsNodeCore(nR, nC, nN, data.Heights[i]))
                            {
                                hasCoreNeighbor = true;
                                break;
                            }
                        }
                    }
                }

                if (!hasCoreNeighbor) data.Flags[i] |= MovementFlags.Unreachable;
            }
            data.IsConnectivityCalculated = true;
        }

        private static int CountSetBits(ulong value)
        {
            value &= 0xFFF; // Only existence bits
            int count = 0;
            while (value != 0) { count++; value &= value - 1; }
            return count;
        }

        private static bool IsNodeCore(int3 region, int3 chunk, int2 node, float y)
        {
            ulong addr = WorldGraphMath.PackChunkAddress(region, chunk);
            if (TryGetChunkOrBlob(region, chunk, out EditorChunkData data))
            {
                 int flat = WorldGraphMath.EncodeNodeToMorton(node); // Morton Index
                 if (data.Flags[flat] != MovementFlags.None)
                 {
                     return CountSetBits((ulong)data.Flags[flat]) == 6;
                 }
            }
            // Fallback
             return CountSetBits(GetHexConnectivityFlags(region, chunk, node, y)) == 6;
        }

        private static ulong GetHexConnectivityFlags(int3 region, int3 chunk, int2 nodeIdx, float currentY)
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
                if (GetNeighborWorldPos(region, chunk, nodeIdx + offsets[k], currentY, out _, out _, out _, out _))
                {
                    flags |= dirs[k];
                }
            }
            return flags;
        }

        private static bool GetNeighborWorldPos(int3 r, int3 c, int2 targetNode, float baseY, out float3 worldPos, out int3 resR, out int3 resC, out int2 resN)
        {
             worldPos = float3.zero; resR = int3.zero; resC = int3.zero; resN = int2.zero;
             int3 targetC = c; int3 targetR = r; int2 tNode = targetNode;

            if (tNode.x < 0) {tNode.x += WorldGraphConstants.ChunkSizeNodesX; targetC.x--; }
            else if (tNode.x >= WorldGraphConstants.ChunkSizeNodesX) { tNode.x -= WorldGraphConstants.ChunkSizeNodesX; targetC.x++; }

            if (tNode.y < 0) {tNode.y += WorldGraphConstants.ChunkSizeNodesZ; targetC.z--; }
            else if (tNode.y >= WorldGraphConstants.ChunkSizeNodesZ) { tNode.y -= WorldGraphConstants.ChunkSizeNodesZ; targetC.z++; }

            if (targetC.x < 0) { targetC.x += WorldGraphConstants.RegionSizeChunksX; targetR.x--; }
            else if (targetC.x >= WorldGraphConstants.RegionSizeChunksX) { targetC.x -= WorldGraphConstants.RegionSizeChunksX; targetR.x++; }
            
            if (targetC.z < 0) { targetC.z += WorldGraphConstants.RegionSizeChunksZ; targetR.z--; }
            else if (targetC.z >= WorldGraphConstants.RegionSizeChunksZ) { targetC.z -= WorldGraphConstants.RegionSizeChunksZ; targetR.z++; }

            for (int dy = 1; dy >= -1; dy--)
            {
                int3 checkC = targetC; int3 checkR = targetR; checkC.y += dy;
                if (checkC.y >= WorldGraphConstants.RegionSizeChunksY) { checkC.y -= WorldGraphConstants.RegionSizeChunksY; checkR.y++; }
                else if (checkC.y < 0) { checkC.y += WorldGraphConstants.RegionSizeChunksY; checkR.y--; }

                ulong cAddr = WorldGraphMath.PackChunkAddress(checkR, checkC);
                if (TryGetChunkOrBlob(checkR, checkC, out EditorChunkData data))
                {
                    int flatIdx = WorldGraphMath.EncodeNodeToMorton(tNode);
                    float h = data.Heights[flatIdx];
                    if (!float.IsNaN(h) && Mathf.Abs(h - baseY) <= WorldGraphConstants.MaxSlopeHeight)
                    {
                        resR = checkR; resC = checkC; resN = tNode;
                        worldPos = WorldGraphMath.GraphToWorldBase(checkR, checkC, tNode);
                        worldPos.y = h;
                        return true;
                    }
                }
            }
            return false;
        }

        private static void ScanChunk(int3 regionKey, int3 chunkIdx, LayerMask groundMask, LayerMask obstacleMask, EditorChunkData data)
        {
            int nodeCount = WorldGraphConstants.NodesPerChunk;
            
            var commands = new NativeArray<RaycastCommand>(nodeCount, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(nodeCount, Allocator.TempJob);

            // Scan in Morton Order so the resulting array is Morton-ordered
            for (int i = 0; i < nodeCount; i++)
            {
                int2 nodeIdx = WorldGraphMath.DecodeMortonToNode((byte)i);
                float3 nodeWorldPos = WorldGraphMath.GraphToWorldBase(regionKey, chunkIdx, nodeIdx);
                float rayOriginY = nodeWorldPos.y + WorldGraphConstants.ChunkHeight;
                Vector3 rayOrigin = new Vector3(nodeWorldPos.x, rayOriginY, nodeWorldPos.z);
                commands[i] = new RaycastCommand(rayOrigin, Vector3.down, new QueryParameters(groundMask), WorldGraphConstants.ChunkHeight);
            }

            JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 32, default(JobHandle));
            handle.Complete();

            var commandsOv = new NativeArray<OverlapCapsuleCommand>(nodeCount, Allocator.TempJob);
            var resultsOv = new NativeArray<ColliderHit>(nodeCount, Allocator.TempJob); 
            float capsuleHeight = 2.0f; float capsuleRadius = 0.10f; float groundClearance = 0.05f;
            LayerMask overlapMask = groundMask | obstacleMask;

            for (int i = 0; i < nodeCount; i++)
            {
                if (results[i].collider != null)
                {
                    Vector3 groundPt = results[i].point;
                    Vector3 pBot = groundPt + Vector3.up * (capsuleRadius + groundClearance);
                    Vector3 pTop = groundPt + Vector3.up * (capsuleHeight - capsuleRadius + groundClearance);
                    //Handles.DrawWireDisc(pBot, Vector3.Normalize(SceneView.lastActiveSceneView.camera.transform.position - pBot), capsuleRadius); // Debug Draw
                    //Handles.DrawWireDisc(pTop, Vector3.Normalize(SceneView.lastActiveSceneView.camera.transform.position - pTop), capsuleRadius);
                    //Handles.DrawLine(pBot + Vector3.right * capsuleRadius, pTop + Vector3.right * capsuleRadius);
                    //Handles.DrawLine(pBot - Vector3.right * capsuleRadius, pTop - Vector3.right * capsuleRadius);
                    commandsOv[i] = new OverlapCapsuleCommand(pBot, pTop, capsuleRadius, new QueryParameters(overlapMask));
                }
                else
                {
                    commandsOv[i] = new OverlapCapsuleCommand(Vector3.zero, Vector3.zero, 0f, new QueryParameters());
                }
            }

            JobHandle handleOv = OverlapCapsuleCommand.ScheduleBatch(commandsOv, resultsOv, 32, 1, default(JobHandle));
            handleOv.Complete();

            for (int i = 0; i < nodeCount; i++)
            {
                if (results[i].collider != null)
                {
                    int hitId = results[i].collider.GetInstanceID();
                    if (resultsOv[i].instanceID == 0 || resultsOv[i].instanceID == hitId)
                    {
                        data.Heights[i] = results[i].point.y;
                    }
                    else
                    {
                        data.Heights[i] = float.NaN;
                    }
                }
                else
                {
                    data.Heights[i] = float.NaN;
                }
            }
            commands.Dispose(); results.Dispose(); commandsOv.Dispose(); resultsOv.Dispose();
            InvalidateNeighbors(regionKey, chunkIdx);
        }

        private static void InvalidateNeighbors(int3 region, int3 chunk)
        { 
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                         int3 nCh = chunk + new int3(x, y, z); int3 nReg = region;
                        if (nCh.x < 0) { nReg.x--; nCh.x = WorldGraphConstants.RegionSizeChunksX - 1; }
                        else if (nCh.x >= WorldGraphConstants.RegionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = WorldGraphConstants.RegionSizeChunksY - 1; }
                        else if (nCh.y >= WorldGraphConstants.RegionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = WorldGraphConstants.RegionSizeChunksZ - 1; }
                        else if (nCh.z >= WorldGraphConstants.RegionSizeChunksZ) { nReg.z++; nCh.z = 0; }
                        
                        ulong cAddr = WorldGraphMath.PackChunkAddress(nReg, nCh);
                        if (TryGetChunk(cAddr, out EditorChunkData chunkData)) chunkData.IsConnectivityCalculated = false;
                    }
                }
            }
        }
        
        // --- Helper for Partitioned Cache ---
        private static bool TryGetChunk(ulong address, out EditorChunkData data)
        {
            return _chunksCache.TryGetValue(address, out data);
        }

        private static bool TryGetChunkOrBlob(int3 region, int3 chunk, out EditorChunkData data)
        {
            ulong address = WorldGraphMath.PackChunkAddress(region, chunk);
            if (_chunksCache.TryGetValue(address, out data))
                return true;

            if (TryGetBlobChunk(region, chunk, out data))
            {
                _chunksCache[address] = data;
                return true;
            }

            return false;
        }

        private static void PreloadNeighborChunksFromBlob(int3 regionKey, int3 chunkIdx)
        {
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        int3 nCh = chunkIdx + new int3(x, y, z);
                        int3 nReg = regionKey;

                        if (nCh.x < 0) { nReg.x--; nCh.x = WorldGraphConstants.RegionSizeChunksX - 1; }
                        else if (nCh.x >= WorldGraphConstants.RegionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = WorldGraphConstants.RegionSizeChunksY - 1; }
                        else if (nCh.y >= WorldGraphConstants.RegionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = WorldGraphConstants.RegionSizeChunksZ - 1; }
                        else if (nCh.z >= WorldGraphConstants.RegionSizeChunksZ) { nReg.z++; nCh.z = 0; }

                        ulong addr = WorldGraphMath.PackChunkAddress(nReg, nCh);
                        if (_chunksCache.ContainsKey(addr))
                            continue;

                        if (TryGetBlobChunk(nReg, nCh, out EditorChunkData data))
                        {
                            _chunksCache[addr] = data;
                        }
                    }
                }
            }
        }

        private static void AddChunkToCache(ulong address, EditorChunkData data)
        {
            _chunksCache[address] = data;
        }

        private static void RemoveChunkFromCache(ulong address)
        {
            _chunksCache.Remove(address);
        }
    }
}
