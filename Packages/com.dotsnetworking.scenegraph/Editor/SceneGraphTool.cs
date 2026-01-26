using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Mathematics;
using DotsNetworking.SceneGraph.Utils;
using DotsNetworking.SceneGraph;
using UnityEditorInternal;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using System.Linq;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;
using System.Reflection;
using UnityEngine.Profiling;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph.Editor
{
    [Overlay(typeof(SceneView), "Scene Graph", true)]
    public class SceneGraphDebugOverlay : Overlay
    {
        // --- Settings (Static for persistence across SceneViews) ---
        public static bool EnableMouseHover = true;
        public static bool ShowSectionBounds = false;
        public static bool VisualizeBaked = false;

        // --- Cache Logic ---
        
        // Section packed with Chunk Address -> Data
        private static Dictionary<ChunkAddress, EditorChunkData> _chunksCache = new Dictionary<ChunkAddress, EditorChunkData>();
        
        // Blob Cache - keyed by scene+section
        private static Dictionary<SectionAddress, BlobAssetHandle<Section>> _loadedHandles = new Dictionary<SectionAddress, BlobAssetHandle<Section>>();

        // Hover State
        private static ChunkAddress _lastHoveredAddress;
        private static bool _hasLastHovered = false;
        private static int _hoverFrameCount = 0;
        private static bool _isHoverValid = false;
        private static int3 _hoverSectionKey;
        private static int3 _hoverChunkIdx;
        private static Vector3 _hoverPoint;
        private static Vector3 _hoverNormal;
        private static EntitiesHash128 _hoverSceneGuid;
        private static bool _hasHoverSceneGuid = false;

        SceneGraphEditorSettings settings;
        UnityEditor.Editor SceneGraphSettingsEditor;
        public override void OnCreated()
        {
            base.OnCreated();
            settings = SceneGraphEditorSettings.instance;
            UnityEditor.Editor.CreateEditor(settings);
        }

        public override void OnWillBeDestroyed()
        {
            base.OnWillBeDestroyed();
            UnityEngine.Object.DestroyImmediate(SceneGraphSettingsEditor);
        }

        // --- Overlay UI ---
        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement() { style = { minWidth = 220, paddingBottom = 5, paddingTop = 5, paddingLeft = 5, paddingRight = 5 } };

            // --- Settings Binding ---
            root.Add(new IMGUIContainer(() =>
            {
                if (SceneGraphSettingsEditor == null)
                    SceneGraphSettingsEditor = UnityEditor.Editor.CreateEditor(settings);

                SceneGraphSettingsEditor.serializedObject.Update();

                SerializedProperty prop = SceneGraphSettingsEditor.serializedObject.GetIterator();
                prop.NextVisible(true); // Skip script reference
                while (prop.NextVisible(false))
                {
                    EditorGUILayout.PropertyField(prop, true);
                }
                if(SceneGraphSettingsEditor.serializedObject.ApplyModifiedProperties())
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

            // Toggle: Show Section Bounds
            var toggleBounds = new Toggle("Show Section Bounds") { value = ShowSectionBounds };
            toggleBounds.RegisterValueChangedCallback(evt => {
                ShowSectionBounds = evt.newValue;
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
            _hasLastHovered = false;
            _hasHoverSceneGuid = false;
            
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
            Profiler.BeginSample("SceneGraphTool.OnSceneGUI");
            try
            {
                if (!EnableMouseHover && !ShowSectionBounds) return;

                Event e = Event.current;
                
                // Draw Bounds regardless of hover if enabled
                if (ShowSectionBounds)
                {
                    int3 sectionToDraw = _hoverSectionKey;
                    if (!_isHoverValid)
                    {
                        SceneGraphMath.WorldToGraph(view.camera.transform.position, out sectionToDraw, out _, out _, out _);
                    }
                    DrawSectionGrid(sectionToDraw);
                }

                if (EnableMouseHover)
                {
                    // Logic mostly follows old UpdateHoverTarget but static
                    if (e.type == EventType.MouseMove)
                    {
                        Profiler.BeginSample("SceneGraphTool.UpdateHoverTarget");
                        UpdateHoverTarget(e);
                        Profiler.EndSample();
                        view.Repaint();
                    }

                    if (e.type == EventType.Repaint)
                    {
                        Profiler.BeginSample("SceneGraphTool.DrawHoverTarget");
                        DrawHoverTarget();
                        Profiler.EndSample();
                    }
                }
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        private static void UpdateHoverTarget(Event e)
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (Physics.Raycast(mouseRay, out RaycastHit hit, 1000f, SceneGraphEditorSettings.instance.GeometryLayer))
            {
                var hitSceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(hit.collider.gameObject.scene.path);
                if (hitSceneGuid.Equals(default))
                {
                    _isHoverValid = false;
                    _hasHoverSceneGuid = false;
                    return;
                }

                _isHoverValid = true;
                _hoverPoint = hit.point;
                _hoverNormal = hit.normal;
                _hoverSceneGuid = hitSceneGuid;
                _hasHoverSceneGuid = true;
                SceneGraphMath.WorldToGraph(hit.point, out _hoverSectionKey, out _hoverChunkIdx, out _, out _);
            }
            else
            {
                _isHoverValid = false;
                _hasHoverSceneGuid = false;
            }
        }

        private static void DrawSectionGrid(int3 sectionKey)
        {
             float3 sectionOrigin = new float3(
                sectionKey.x * SceneGraphConstants.SectionSizeX,
                sectionKey.y * SceneGraphConstants.SectionSizeY,
                sectionKey.z * SceneGraphConstants.SectionSizeZ
            );
            
            Handles.color = new Color(1, .5f, 1, 1f);
            Handles.DrawWireCube(
                sectionOrigin + new float3(SceneGraphConstants.SectionSizeX/2, SceneGraphConstants.SectionSizeY/2, SceneGraphConstants.SectionSizeZ/2),
                new Vector3(SceneGraphConstants.SectionSizeX, SceneGraphConstants.SectionSizeY, SceneGraphConstants.SectionSizeZ)
            );
            Handles.Label(sectionOrigin + new float3(0, SceneGraphConstants.SectionSizeY, 0), $"Section {sectionKey}");
        }

        private static void DrawHoverTarget()
        {
            if (!_isHoverValid) return;
            Profiler.BeginSample("SceneGraphTool.DrawHoverTarget.Body");

            if (!_hasHoverSceneGuid)
            {
                Profiler.EndSample();
                return;
            }
            
            EntitiesHash128 sceneGuid = _hoverSceneGuid;

            // Draw Mouse Hit
            Handles.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Handles.DrawSolidDisc(_hoverPoint, _hoverNormal, 0.1f);
            
            int3 drawSection = _hoverSectionKey;
            int3 drawChunk = _hoverChunkIdx;

            // Snap
            SceneGraphMath.WorldToGraph(_hoverPoint, out _, out _, out int2 nodeIdx, out _);
            float3 latticePos = SceneGraphMath.GraphToWorldBase(drawSection, drawChunk, nodeIdx);
            latticePos.y = _hoverPoint.y; 

            Handles.color = Color.cyan;
            Handles.DrawWireDisc(latticePos, Vector3.up, 0.2f); 

            // Offset Debugging
            long globalZ = (long)drawSection.z * SceneGraphConstants.SectionSizeChunksZ * SceneGraphConstants.ChunkSizeNodesZ 
                           + (long)drawChunk.z * SceneGraphConstants.ChunkSizeNodesZ + nodeIdx.y;
            bool isOddRow = (globalZ & 1) != 0;
            int2[] offsets = isOddRow 
                ? new int2[] { new int2(0, 1), new int2(1, 1), new int2(0, -1), new int2(1, -1), new int2(-1, 0), new int2(1, 0) }
                : new int2[] { new int2(0, 1), new int2(-1, 1), new int2(0, -1), new int2(-1, -1), new int2(-1, 0), new int2(1, 0) };

            Handles.color = new Color(1, 1, 0, 0.5f);
            foreach (var off in offsets)
            {
                float3 nPos = SceneGraphMath.GraphToWorldBase(drawSection, drawChunk, nodeIdx + off);
                nPos.y = latticePos.y; 
                Handles.DrawLine(latticePos, nPos);
            }

            DrawChunkBounds(drawSection, drawChunk);
            UpdateAndDrawChunk(sceneGuid, drawSection, drawChunk, SceneGraphEditorSettings.instance.GeometryLayer, SceneGraphEditorSettings.instance.ObstacleLayer);
            DrawNeighbors(sceneGuid, drawSection, drawChunk);
            Profiler.EndSample();
        }

        private static void DrawNeighbors(EntitiesHash128 sceneGuid, int3 currentReg, int3 currentChunk)
        {
            Profiler.BeginSample("SceneGraphTool.DrawNeighbors");
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
                        if (nCh.x < 0) { nReg.x--; nCh.x = SceneGraphConstants.SectionSizeChunksX - 1; }
                        else if (nCh.x >= SceneGraphConstants.SectionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = SceneGraphConstants.SectionSizeChunksY - 1; }
                        else if (nCh.y >= SceneGraphConstants.SectionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = SceneGraphConstants.SectionSizeChunksZ - 1; }
                        else if (nCh.z >= SceneGraphConstants.SectionSizeChunksZ) { nReg.z++; nCh.z = 0; }

                        if (TryGetChunkOrBlob(sceneGuid, nReg, nCh, out EditorChunkData chunkData))
                        {
                            // Ensure Baked
                            if (!chunkData.IsConnectivityCalculated) 
                                BakeChunkConnectivity(sceneGuid, nReg, nCh, chunkData);

                            Handles.color = new Color(0, 1, 0, 0.3f); // Faint
                            Profiler.BeginSample("SceneGraphTool.DrawNeighbors.DrawNodes");
                            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
                            {
                                float h = chunkData.Heights[i];
                                if (float.IsNaN(h)) continue;
                                if (chunkData.Flags[i].HasFlag(MovementFlags.Unreachable)) continue;

                                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
                                int cx = nodeIdx.x;
                                int cz = nodeIdx.y;

                                float3 pos = SceneGraphMath.GraphToWorldBase(nReg, nCh, new int2(cx, cz));
                                pos.y = h;
                                Handles.SphereHandleCap(0, pos, Quaternion.identity, SceneGraphConstants.NodeSize * 0.1f, EventType.Repaint);
                                //Handles.DrawSolidDisc(pos, Vector3.up, SceneGraphConstants.NodeSize * 0.05f);
                            }
                            Profiler.EndSample();
                        }
                    }
                }
            }
            Profiler.EndSample();
        }

        private static void DrawChunkBounds(int3 sectionKey, int3 chunkIdx)
        {
            float3 chunkBase = new float3(
                sectionKey.x * SceneGraphConstants.SectionSizeX + chunkIdx.x * SceneGraphConstants.ChunkSizeX,
                sectionKey.y * SceneGraphConstants.SectionSizeY + chunkIdx.y * SceneGraphConstants.ChunkHeight,
                sectionKey.z * SceneGraphConstants.SectionSizeZ + chunkIdx.z * SceneGraphConstants.ChunkSizeZ
            );
            Vector3 center = chunkBase + new float3(SceneGraphConstants.ChunkSizeX / 2, SceneGraphConstants.ChunkHeight / 2, SceneGraphConstants.ChunkSizeZ / 2);
            Vector3 size = new Vector3(SceneGraphConstants.ChunkSizeX, SceneGraphConstants.ChunkHeight, SceneGraphConstants.ChunkSizeZ);

            Handles.color = Color.yellow;
            Handles.DrawWireCube(center, size);
            Handles.Label(center + Vector3.up * 2, $"Chk: {chunkIdx}");           

        }

        private static void UpdateAndDrawChunk(EntitiesHash128 sceneGuid, int3 sectionKey, int3 chunkIdx, LayerMask groundMask, LayerMask obstacleMask)
        {
            Profiler.BeginSample("SceneGraphTool.UpdateAndDrawChunk");
            ChunkAddress address = SceneGraphMath.GetChunkAddress(sceneGuid, sectionKey, chunkIdx);

            // Preload neighbor baked chunks if not already cached to reduce false positives.
            Profiler.BeginSample("SceneGraphTool.PreloadNeighborChunksFromBlob");
            PreloadNeighborChunksFromBlob(sceneGuid, sectionKey, chunkIdx);
            Profiler.EndSample();
            
            if (!_hasLastHovered || _lastHoveredAddress != address)
            {
                _lastHoveredAddress = address;
                _hasLastHovered = true;
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
                Profiler.BeginSample("SceneGraphTool.ScanChunk");
                chunkData = new EditorChunkData();
                ScanChunk(sectionKey, chunkIdx, groundMask, obstacleMask, chunkData);
                AddChunkToCache(address, chunkData);
                Profiler.EndSample();
            }

             // Blob Check
            Profiler.BeginSample("SceneGraphTool.TryGetBlobChunk");
            bool usingBlob = TryGetBlobChunk(sceneGuid, sectionKey, chunkIdx, out EditorChunkData blobData);
            Profiler.EndSample();
            bool isGeomDirty = false;

            if (!chunkData.IsConnectivityCalculated)
            {
                Profiler.BeginSample("SceneGraphTool.BakeChunkConnectivity");
                BakeChunkConnectivity(sceneGuid, sectionKey, chunkIdx, chunkData);
                Profiler.EndSample();
            }

            if (usingBlob)
            {
                Profiler.BeginSample("SceneGraphTool.IsChunkDifferent");
                isGeomDirty = IsChunkDifferent(chunkData, blobData);
                Profiler.EndSample();
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


            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
            {
                float h = chunkData.Heights[i];
                //if (float.IsNaN(h)) continue;
                if (chunkData.Flags[i].HasFlag(MovementFlags.Unreachable)) continue;

                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
                int x = nodeIdx.x;
                int z = nodeIdx.y;
                
                float3 pos = SceneGraphMath.GraphToWorldBase(sectionKey, chunkIdx, new int2(x, z));
                pos.y = h;
                Handles.SphereHandleCap(0, pos, Quaternion.identity, SceneGraphConstants.NodeSize * 0.1f, EventType.Repaint);
                //Handles.DrawSolidDisc(pos, Vector3.up, SceneGraphConstants.NodeSize * 0.05f);
            }
            Profiler.EndSample();
        }
        
        private static unsafe bool TryGetBlobChunk(EntitiesHash128 sceneGuid, int3 section, int3 chunk, out EditorChunkData data)
        {
            Profiler.BeginSample("SceneGraphTool.TryGetBlobChunk.Internal");
            try
            {
                data = null;
                uint sectionIndex = SceneGraphMath.PackSectionId(section);
                var sectionAddress = new SectionAddress(sceneGuid, sectionIndex);
                
                if (!_loadedHandles.ContainsKey(sectionAddress))
                {
                    var handle = NavigationAssetProvider.CheckOut(sectionAddress);
                    if (handle.IsValid)
                    {
                        _loadedHandles[sectionAddress] = handle;
                    }
                }

                if (_loadedHandles.TryGetValue(sectionAddress, out var rHandle))
                {
                    if (!rHandle.IsValid)
                    {
                        _loadedHandles.Remove(sectionAddress);
                        return false;
                    }

                    ref Section r = ref rHandle.Blob.Value;
                    ushort morton = SceneGraphMath.EncodeChunkToMorton(chunk);
                
                    // Check Bounds
                    if (morton < r.ChunkLookup.Length) 
                    {
                         short idx = r.ChunkLookup[morton];
                         if (idx != -1 && idx < r.Chunks.Length)
                         {
                             EditorChunkData d = new EditorChunkData();
                             ref Chunk c = ref r.Chunks[idx];
                         
                             for(int i=0; i<SceneGraphConstants.NodesPerChunk; i++)
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
            finally
            {
                Profiler.EndSample();
            }
        }

        private static bool IsChunkDifferent(EditorChunkData a, EditorChunkData b)
        {
            int mismatchCount = 0;
            for(int i=0; i<SceneGraphConstants.NodesPerChunk; i++)
            {
                float h1 = a.Heights[i];
                float h2 = b.Heights[i];
                bool n1 = float.IsNaN(h1);
                bool n2 = float.IsNaN(h2);
                if (n1 != n2)
                {
                    return true;
                    if(n1)
                    {
                        var pos = SceneGraphMath.GraphToWorldBase(_hoverSectionKey, _hoverChunkIdx, SceneGraphMath.DecodeMortonToNode((byte)i));
                        Handles.color = Color.magenta;
                        Handles.DrawSolidDisc(new float3(pos.x, pos.y, pos.z), Vector3.up, 0.05f); // Prevent unused warning
                    }
                    mismatchCount++;
                }
                else if (!n1 && Mathf.Abs(h1 - h2) > 0.05f)
                {
                    return true;
                    mismatchCount++;
                }else if (!n1 && a.Flags[i] != b.Flags[i])
                {                    //Debug.Log("Section " + _hoverSectionKey + " Chunk " + _hoverChunkIdx + " Node " + SceneGraphMath.DecodeMortonToNode((byte)i) + " Flag Mismatch: Live=" + a.Flags[i] + " Blob=" + b.Flags[i]);
                    var pos = SceneGraphMath.GraphToWorldBase(_hoverSectionKey, _hoverChunkIdx, SceneGraphMath.DecodeMortonToNode((byte)i));
                    Handles.color = Color.blue;
                    Handles.DrawSolidDisc(new float3(pos.x, h2, pos.z), Vector3.up, 0.05f); // Prevent unused warning
                    mismatchCount++;
                    return true;
                }
            }
            return mismatchCount > 0;
        }

        // --- Logic ---

        private static void BakeChunkConnectivity(EntitiesHash128 sceneGuid, int3 section, int3 chunk, EditorChunkData data)
        {
            Profiler.BeginSample("SceneGraphTool.BakeChunkConnectivity.Internal");
            // Pass 1: Raw existence
            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
            {
                if (float.IsNaN(data.Heights[i])) 
                {
                    data.Flags[i] = MovementFlags.Unreachable;
                    continue;
                }
                
                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
                int x = nodeIdx.x;
                int z = nodeIdx.y;

                ulong flags = GetHexConnectivityFlags(sceneGuid, section, chunk, new int2(x, z), data.Heights[i]);
                data.Flags[i] = (MovementFlags)flags; 
            }

            // Pass 2: Core or Neighbor-of-Core logic
            long baseGlobalZ = (long)section.z * SceneGraphConstants.SectionSizeChunksZ * SceneGraphConstants.ChunkSizeNodesZ 
                           + (long)chunk.z * SceneGraphConstants.ChunkSizeNodesZ;
            
            for (int i = 0; i < SceneGraphConstants.NodesPerChunk; i++)
            {
                if (float.IsNaN(data.Heights[i])) continue;
                
                ulong f = (ulong)data.Flags[i];
                if (CountSetBits(f) == 6) continue; // Is Core

                // Check neighbors for Core
                bool hasCoreNeighbor = false;
                
                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
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
                        if (GetNeighborWorldPos(sceneGuid, section, chunk, new int2(x, z) + offsets[k], data.Heights[i], out _, out int3 nR, out int3 nC, out int2 nN))
                        {
                            if (IsNodeCore(sceneGuid, nR, nC, nN, data.Heights[i]))
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
            Profiler.EndSample();
        }

        private static int CountSetBits(ulong value)
        {
            value &= 0xFFF; // Only existence bits
            int count = 0;
            while (value != 0) { count++; value &= value - 1; }
            return count;
        }

        private static bool IsNodeCore(EntitiesHash128 sceneGuid, int3 section, int3 chunk, int2 node, float y)
        {
            if (TryGetChunkOrBlob(sceneGuid, section, chunk, out EditorChunkData data))
            {
                 int flat = SceneGraphMath.EncodeNodeToMorton(node); // Morton Index
                 if (data.Flags[flat] != MovementFlags.None)
                 {
                     return CountSetBits((ulong)data.Flags[flat]) == 6;
                 }
            }
            // Fallback
             return CountSetBits(GetHexConnectivityFlags(sceneGuid, section, chunk, node, y)) == 6;
        }

        private static ulong GetHexConnectivityFlags(EntitiesHash128 sceneGuid, int3 section, int3 chunk, int2 nodeIdx, float currentY)
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
                if (GetNeighborWorldPos(sceneGuid, section, chunk, nodeIdx + offsets[k], currentY, out _, out _, out _, out _))
                {
                    flags |= dirs[k];
                }
            }
            return flags;
        }

        private static bool GetNeighborWorldPos(EntitiesHash128 sceneGuid, int3 r, int3 c, int2 targetNode, float baseY, out float3 worldPos, out int3 resR, out int3 resC, out int2 resN)
        {
             worldPos = float3.zero; resR = int3.zero; resC = int3.zero; resN = int2.zero;
             int3 targetC = c; int3 targetR = r; int2 tNode = targetNode;

            if (tNode.x < 0) {tNode.x += SceneGraphConstants.ChunkSizeNodesX; targetC.x--; }
            else if (tNode.x >= SceneGraphConstants.ChunkSizeNodesX) { tNode.x -= SceneGraphConstants.ChunkSizeNodesX; targetC.x++; }

            if (tNode.y < 0) {tNode.y += SceneGraphConstants.ChunkSizeNodesZ; targetC.z--; }
            else if (tNode.y >= SceneGraphConstants.ChunkSizeNodesZ) { tNode.y -= SceneGraphConstants.ChunkSizeNodesZ; targetC.z++; }

            if (targetC.x < 0) { targetC.x += SceneGraphConstants.SectionSizeChunksX; targetR.x--; }
            else if (targetC.x >= SceneGraphConstants.SectionSizeChunksX) { targetC.x -= SceneGraphConstants.SectionSizeChunksX; targetR.x++; }
            
            if (targetC.z < 0) { targetC.z += SceneGraphConstants.SectionSizeChunksZ; targetR.z--; }
            else if (targetC.z >= SceneGraphConstants.SectionSizeChunksZ) { targetC.z -= SceneGraphConstants.SectionSizeChunksZ; targetR.z++; }

            for (int dy = 1; dy >= -1; dy--)
            {
                int3 checkC = targetC; int3 checkR = targetR; checkC.y += dy;
                if (checkC.y >= SceneGraphConstants.SectionSizeChunksY) { checkC.y -= SceneGraphConstants.SectionSizeChunksY; checkR.y++; }
                else if (checkC.y < 0) { checkC.y += SceneGraphConstants.SectionSizeChunksY; checkR.y--; }

                ChunkAddress cAddr = SceneGraphMath.GetChunkAddress(sceneGuid, checkR, checkC);
                if (TryGetChunkOrBlob(sceneGuid, checkR, checkC, out EditorChunkData data))
                {
                    int flatIdx = SceneGraphMath.EncodeNodeToMorton(tNode);
                    float h = data.Heights[flatIdx];
                    if (!float.IsNaN(h) && Mathf.Abs(h - baseY) <= SceneGraphConstants.MaxSlopeHeight)
                    {
                        resR = checkR; resC = checkC; resN = tNode;
                        worldPos = SceneGraphMath.GraphToWorldBase(checkR, checkC, tNode);
                        worldPos.y = h;
                        return true;
                    }
                }
            }
            return false;
        }

        private static void ScanChunk(int3 sectionKey, int3 chunkIdx, LayerMask groundMask, LayerMask obstacleMask, EditorChunkData data)
        {
            Profiler.BeginSample("SceneGraphTool.ScanChunk.Internal");
            int nodeCount = SceneGraphConstants.NodesPerChunk;
            
            var commands = new NativeArray<RaycastCommand>(nodeCount, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(nodeCount, Allocator.TempJob);

            // Scan in Morton Order so the resulting array is Morton-ordered
            for (int i = 0; i < nodeCount; i++)
            {
                int2 nodeIdx = SceneGraphMath.DecodeMortonToNode((byte)i);
                float3 nodeWorldPos = SceneGraphMath.GraphToWorldBase(sectionKey, chunkIdx, nodeIdx);
                float rayOriginY = nodeWorldPos.y + SceneGraphConstants.ChunkHeight;
                Vector3 rayOrigin = new Vector3(nodeWorldPos.x, rayOriginY, nodeWorldPos.z);
                commands[i] = new RaycastCommand(rayOrigin, Vector3.down, new QueryParameters(groundMask), SceneGraphConstants.ChunkHeight);
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
            data.IsConnectivityCalculated = false;
            //InvalidateNeighbors(sectionKey, chunkIdx);
            Profiler.EndSample();
        }

        private static void InvalidateNeighbors(EntitiesHash128 sceneGuid, int3 section, int3 chunk)
        { 
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                         int3 nCh = chunk + new int3(x, y, z); int3 nReg = section;
                        if (nCh.x < 0) { nReg.x--; nCh.x = SceneGraphConstants.SectionSizeChunksX - 1; }
                        else if (nCh.x >= SceneGraphConstants.SectionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = SceneGraphConstants.SectionSizeChunksY - 1; }
                        else if (nCh.y >= SceneGraphConstants.SectionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = SceneGraphConstants.SectionSizeChunksZ - 1; }
                        else if (nCh.z >= SceneGraphConstants.SectionSizeChunksZ) { nReg.z++; nCh.z = 0; }
                        
                        ChunkAddress cAddr = SceneGraphMath.GetChunkAddress(sceneGuid, nReg, nCh);
                        if (TryGetChunk(cAddr, out EditorChunkData chunkData)) chunkData.IsConnectivityCalculated = false;
                    }
                }
            }
        }
        
        // --- Helper for Partitioned Cache ---
        private static bool TryGetChunk(ChunkAddress address, out EditorChunkData data)
        {
            return _chunksCache.TryGetValue(address, out data);
        }

        private static bool TryGetChunkOrBlob(EntitiesHash128 sceneGuid, int3 section, int3 chunk, out EditorChunkData data)
        {
            ChunkAddress address = SceneGraphMath.GetChunkAddress(sceneGuid, section, chunk);
            if (_chunksCache.TryGetValue(address, out data))
                return true;

            if (TryGetBlobChunk(sceneGuid, section, chunk, out data))
            {
                _chunksCache[address] = data;
                return true;
            }

            return false;
        }

        private static void PreloadNeighborChunksFromBlob(EntitiesHash128 sceneGuid, int3 sectionKey, int3 chunkIdx)
        {
            Profiler.BeginSample("SceneGraphTool.PreloadNeighborChunksFromBlob.Internal");
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        int3 nCh = chunkIdx + new int3(x, y, z);
                        int3 nReg = sectionKey;

                        if (nCh.x < 0) { nReg.x--; nCh.x = SceneGraphConstants.SectionSizeChunksX - 1; }
                        else if (nCh.x >= SceneGraphConstants.SectionSizeChunksX) { nReg.x++; nCh.x = 0; }
                        if (nCh.y < 0) { nReg.y--; nCh.y = SceneGraphConstants.SectionSizeChunksY - 1; }
                        else if (nCh.y >= SceneGraphConstants.SectionSizeChunksY) { nReg.y++; nCh.y = 0; }
                        if (nCh.z < 0) { nReg.z--; nCh.z = SceneGraphConstants.SectionSizeChunksZ - 1; }
                        else if (nCh.z >= SceneGraphConstants.SectionSizeChunksZ) { nReg.z++; nCh.z = 0; }

                        ChunkAddress addr = SceneGraphMath.GetChunkAddress(sceneGuid, nReg, nCh);
                        if (_chunksCache.ContainsKey(addr))
                            continue;

                        if (TryGetBlobChunk(sceneGuid, nReg, nCh, out EditorChunkData data))
                        {
                            _chunksCache[addr] = data;
                        }
                    }
                }
            }
            Profiler.EndSample();
        }

        private static void AddChunkToCache(ChunkAddress address, EditorChunkData data)
        {
            _chunksCache[address] = data;
        }

        private static void RemoveChunkFromCache(ChunkAddress address)
        {
            _chunksCache.Remove(address);
        }
    }
}



