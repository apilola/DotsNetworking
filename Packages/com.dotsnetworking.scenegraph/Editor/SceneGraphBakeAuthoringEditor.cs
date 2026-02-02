using System.Collections.Generic;
using BovineLabs.Core.Editor.Settings;
using BovineLabs.Core.Editor.Extensions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using DotsNetworking.SceneGraph.Authoring;
using DotsNetworking.SceneGraph.Components;
using Unity.Entities;
using EntitiesHash128 = Unity.Entities.Hash128;
using DotsNetworking.SceneGraph.Utils;

namespace DotsNetworking.SceneGraph.Editor
{
    [CustomEditor(typeof(SceneGraphBakeAuthoring))]
    public class SceneGraphBakeAuthoringEditor : UnityEditor.Editor
    {
        private bool m_ManifestFoldout = true;
        private Vector2 m_ManifestScrollPosition;
        private SceneGraphManifest m_CachedManifest;
        private string m_CachedScenePath;
        private List<SectionDefinition> m_CachedSceneSections;
        private EntitiesHash128 m_CachedSceneGuid;
        private Unity.Mathematics.int3? m_HoveredSectionKey;

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!m_HoveredSectionKey.HasValue)
                return;

            var sectionKey = m_HoveredSectionKey.Value;
            Bounds bounds = GetSectionBounds(sectionKey);

            // Draw wireframe box
            Handles.color = new Color(0.2f, 0.8f, 1f, 1f);
            Handles.DrawWireCube(bounds.center, bounds.size);

            // Draw translucent faces
            Handles.color = new Color(0.2f, 0.8f, 1f, 0.1f);
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            DrawSolidBox(bounds);
        }

        private static void DrawSolidBox(Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            // Bottom face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(min.x, min.y, min.z), new(max.x, min.y, min.z), new(max.x, min.y, max.z), new(min.x, min.y, max.z) },
                Handles.color, Color.clear);
            // Top face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(min.x, max.y, min.z), new(max.x, max.y, min.z), new(max.x, max.y, max.z), new(min.x, max.y, max.z) },
                Handles.color, Color.clear);
            // Front face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(min.x, min.y, min.z), new(max.x, min.y, min.z), new(max.x, max.y, min.z), new(min.x, max.y, min.z) },
                Handles.color, Color.clear);
            // Back face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(min.x, min.y, max.z), new(max.x, min.y, max.z), new(max.x, max.y, max.z), new(min.x, max.y, max.z) },
                Handles.color, Color.clear);
            // Left face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(min.x, min.y, min.z), new(min.x, min.y, max.z), new(min.x, max.y, max.z), new(min.x, max.y, min.z) },
                Handles.color, Color.clear);
            // Right face
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[] { new(max.x, min.y, min.z), new(max.x, min.y, max.z), new(max.x, max.y, max.z), new(max.x, max.y, min.z) },
                Handles.color, Color.clear);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var authoring = (SceneGraphBakeAuthoring)target;
            var scene = authoring.gameObject.scene;
            bool hasSceneAsset = scene.IsValid() && !string.IsNullOrEmpty(scene.path);

            using (new EditorGUI.DisabledScope(!hasSceneAsset))
            {
                if (GUILayout.Button("Bake SubScene Navigation"))
                {
                    SceneGraphBakingService.BakeScene(
                        scene,
                        SceneGraphEditorSettings.instance.GeometryLayer,
                        SceneGraphEditorSettings.instance.ObstacleLayer);
                    m_CachedManifest = null; // Force refresh after bake
                    m_CachedSceneSections = null;
                }

                if (GUILayout.Button("Rebuild SceneGraph Manifest"))
                {
                    SceneGraphBakingService.RebuildManifest(scene);
                    m_CachedManifest = null; // Force refresh after rebuild
                    m_CachedSceneSections = null;
                }

                if (GUILayout.Button("Regenerate SectionAuthoring Objects"))
                {
                    RegenerateSectionAuthoring(authoring, scene.path);
                    m_CachedManifest = null;
                    m_CachedSceneSections = null;
                }
            }

            if (!hasSceneAsset)
            {
                EditorGUILayout.HelpBox("Place this component in a saved scene to bake.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.Space(10);
                DrawManifestSection(scene.path);
            }
        }

        private void DrawManifestSection(string scenePath)
        {
            // Refresh cached manifest if scene changed or not yet loaded
            if (m_CachedManifest == null || m_CachedScenePath != scenePath)
            {
                m_CachedScenePath = scenePath;
                m_CachedManifest = LoadManifest();
                m_CachedSceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(scenePath);
                m_CachedSceneSections = m_CachedManifest?.GetSectionsForSubscene(m_CachedSceneGuid);
            }

            m_ManifestFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(m_ManifestFoldout, "Scene Graph Manifest");
            
            if (m_ManifestFoldout)
            {
                if (m_CachedSceneSections == null || m_CachedSceneSections.Count == 0)
                {
                    EditorGUILayout.HelpBox("No manifest data available. Bake the scene to generate sections.", MessageType.Info);
                }
                else
                {
                    DrawManifestList();
                }
            }
            
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawManifestList()
        {
            var sections = m_CachedSceneSections;
            
            // Header
            EditorGUILayout.LabelField($"Sections ({sections.Count})", EditorStyles.boldLabel);
            
            // Draw a boxed area for the list
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Fixed height scroll view (non-resizable)
                float itemHeight = EditorGUIUtility.singleLineHeight + 4;
                float maxVisibleItems = 8;
                float listHeight = Mathf.Min(sections.Count, maxVisibleItems) * itemHeight + 4;
                
                m_ManifestScrollPosition = EditorGUILayout.BeginScrollView(
                    m_ManifestScrollPosition, 
                    GUILayout.Height(listHeight));
                
                m_HoveredSectionKey = null; // Reset hover state each frame
                
                for (int i = 0; i < sections.Count; i++)
                {
                    DrawSectionEntry(sections[i], i);
                }
                
                EditorGUILayout.EndScrollView();
            }
            
            // Request scene view repaint when hover state might change
            if (Event.current.type == EventType.Repaint)
            {
                SceneView.RepaintAll();
            }
        }

        private void DrawSectionEntry(SectionDefinition entry, int index)
        {
            var sectionKey = SceneGraphMath.UnpackSectionId(entry.Address.SectionId);
            
            Rect rowRect = EditorGUILayout.BeginHorizontal();
            
            // Check for hover
            if (rowRect.Contains(Event.current.mousePosition))
            {
                m_HoveredSectionKey = sectionKey;
            }
            
            // Frame button
            if (GUILayout.Button(EditorGUIUtility.IconContent("d_SceneViewCamera", "Frame section in Scene View"), GUILayout.Width(24), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                FrameSectionInSceneView(sectionKey);
            }
            
            // Index label
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField($"[{index}]", GUILayout.Width(30));
                
                // Section ID (unpacked for readability)
                EditorGUILayout.LabelField($"Section ({sectionKey.x}, {sectionKey.y}, {sectionKey.z})", GUILayout.MinWidth(120));
                
                // Resource key
                EditorGUILayout.LabelField(entry.ResourceKey, EditorStyles.miniLabel);
            }
            
            EditorGUILayout.EndHorizontal();
        }

        private void FrameSectionInSceneView(Unity.Mathematics.int3 sectionKey)
        {
            Bounds sectionBounds = GetSectionBounds(sectionKey);
            
            // Frame the bounds in the last active scene view
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.Frame(sectionBounds, false);
            }
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

        private SceneGraphManifest LoadManifest()
        {
            return EditorSettingsUtility.GetSettings<SceneGraphManifest>();
        }

        private void RegenerateSectionAuthoring(SceneGraphBakeAuthoring authoring, string scenePath)
        {
            var manifest = LoadManifest();
            if (manifest == null)
            {
                Debug.LogWarning("SceneGraph manifest not found.");
                return;
            }

            var sceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(scenePath);
            if (sceneGuid.Equals(default))
            {
                Debug.LogWarning($"Failed to resolve scene GUID for scene: {scenePath}");
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
                int desiredSectionIndex = (int)address.SectionId;

                if (!existingByAddress.TryGetValue(address, out var sectionAuthoring))
                {
                    var go = new GameObject(desiredName);
                    Undo.RegisterCreatedObjectUndo(go, "Create SectionAuthoring");
                    go.transform.SetParent(authoring.transform, false);
                    go.transform.position = desiredPosition;
                    sectionAuthoring = Undo.AddComponent<SectionAuthoring>(go);
                    sectionAuthoring.Initialize(address, blobAsset);
                    EnsureSceneSectionComponent(go, desiredSectionIndex);
                    EditorUtility.SetDirty(sectionAuthoring);
                }
                else
                {
                    bool changed = false;
                    if (sectionAuthoring.Address.Equals(address) == false)
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

                    EnsureSceneSectionComponent(sectionAuthoring.gameObject, desiredSectionIndex);
                }
            }

            EditorSceneManager.MarkSceneDirty(authoring.gameObject.scene);
        }

        private static string GetSectionObjectName(SectionAddress address)
        {
            return $"Section_{address.SectionId}";
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

        private static void EnsureSceneSectionComponent(GameObject target, int sectionIndex)
        {
            var sceneSection = target.GetComponent<SceneSectionComponent>();
            if (sceneSection == null)
            {
                sceneSection = Undo.AddComponent<SceneSectionComponent>(target);
            }

            if (sceneSection.SectionIndex != sectionIndex)
            {
                Undo.RecordObject(sceneSection, "Update SceneSectionComponent");
                sceneSection.SectionIndex = sectionIndex;
                EditorUtility.SetDirty(sceneSection);
            }
        }
    }
}
