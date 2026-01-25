using DotsNetworking.WorldGraph.Utils;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DotsNetworking.WorldGraph.Editor
{
    public class WorldGraphSettingsWindow : EditorWindow
    {
        private SerializedObject _serializedSettings;
        private SerializedProperty _geometryLayerProp;
        private SerializedProperty _obstacleLayerProp;
        private SerializedProperty _sceneWorldMappingsProp;

        [SerializeField]
        private Vector3Int _regionToBake;

        [MenuItem("Window/World Graph/Settings")]
        public static void ShowWindow()
        {
            GetWindow<WorldGraphSettingsWindow>("World Graph Settings");
        }

        private void OnEnable()
        {
            var settings = WorldGraphEditorSettings.instance;
            _serializedSettings = new SerializedObject(settings);
            _geometryLayerProp = _serializedSettings.FindProperty("m_GeometryLayer");
            _obstacleLayerProp = _serializedSettings.FindProperty("m_ObstacleLayer");
            _sceneWorldMappingsProp = _serializedSettings.FindProperty("m_SceneWorldMappings");
        }

        private void OnGUI()
        {
            _serializedSettings.Update();

            GUILayout.Label("World Graph Baking Settings", EditorStyles.boldLabel);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generation Rules", EditorStyles.boldLabel);
            
            EditorGUILayout.PropertyField(_geometryLayerProp, new GUIContent("Geometry Layer", "Layer mask for static geometry"));
            EditorGUILayout.PropertyField(_obstacleLayerProp, new GUIContent("Obstacle Layer", "Layer mask for obstacles"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("World Mapping", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Map scenes to world IDs. Each scene must be uniquely mapped for proper baking.", MessageType.Info);

            EditorGUILayout.PropertyField(_sceneWorldMappingsProp, new GUIContent("Scene World Mappings"), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Baking Actions", EditorStyles.boldLabel);

            _regionToBake = EditorGUILayout.Vector3IntField("Region to Bake", _regionToBake);
            int3 regionInt3 = new int3(_regionToBake.x, _regionToBake.y, _regionToBake.z);
            if (GUILayout.Button("Bake Specified Region", GUILayout.Height(30)))
            {
                int worldId = WorldGraphEditorSettings.instance.GetCurrentSceneWorldId();
                WorldGraphBakingService.BakeRegion(regionInt3, WorldGraphEditorSettings.instance.GeometryLayer, WorldGraphEditorSettings.instance.ObstacleLayer, worldId);
            }

            
            if (GUILayout.Button("Bake Region under Mouse", GUILayout.Height(30)))
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    Ray mouseRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                    if (Physics.Raycast(mouseRay, out RaycastHit hit, 1000f, WorldGraphEditorSettings.instance.GeometryLayer))
                    {
                        WorldGraphMath.WorldToGraph(hit.point, out int3 region, out _, out _, out _);
                        int worldId = WorldGraphEditorSettings.instance.GetCurrentSceneWorldId();
                        WorldGraphBakingService.BakeRegion(region, WorldGraphEditorSettings.instance.GeometryLayer, WorldGraphEditorSettings.instance.ObstacleLayer, worldId);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Bake Region", "No geometry found under mouse position.", "OK");
                    }
                }
            }
            
            if (GUILayout.Button("Bake Entire Scene", GUILayout.Height(30)))
            {
                WorldGraphBakingService.BakeScene(WorldGraphEditorSettings.instance.GeometryLayer, WorldGraphEditorSettings.instance.ObstacleLayer);
            }
            
            EditorGUILayout.Space();
            if (GUILayout.Button("Save Settings", GUILayout.Height(30)))
            {
                _serializedSettings.ApplyModifiedProperties();
                WorldGraphEditorSettings.instance.SaveSettings();
                Debug.Log("World Graph settings saved!");
            }

            _serializedSettings.ApplyModifiedProperties();
        }
    }
}
