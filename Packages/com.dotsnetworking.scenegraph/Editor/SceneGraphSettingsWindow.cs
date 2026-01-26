using DotsNetworking.SceneGraph.Utils;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;

namespace DotsNetworking.SceneGraph.Editor
{
    public class SceneGraphSettingsWindow : EditorWindow
    {
        private SerializedObject _serializedSettings;
        private SerializedProperty _geometryLayerProp;
        private SerializedProperty _obstacleLayerProp;

        [SerializeField]
        private Vector3Int _sectionToBake;

        [MenuItem("Window/Scene Graph/Settings")]
        public static void ShowWindow()
        {
            GetWindow<SceneGraphSettingsWindow>("Scene Graph Settings");
        }

        private void OnEnable()
        {
            var settings = SceneGraphEditorSettings.instance;
            _serializedSettings = new SerializedObject(settings);
            _geometryLayerProp = _serializedSettings.FindProperty("m_GeometryLayer");
            _obstacleLayerProp = _serializedSettings.FindProperty("m_ObstacleLayer");
        }

        private void OnGUI()
        {
            _serializedSettings.Update();

            GUILayout.Label("Scene Graph Baking Settings", EditorStyles.boldLabel);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generation Rules", EditorStyles.boldLabel);
            
            EditorGUILayout.PropertyField(_geometryLayerProp, new GUIContent("Geometry Layer", "Layer mask for static geometry"));
            EditorGUILayout.PropertyField(_obstacleLayerProp, new GUIContent("Obstacle Layer", "Layer mask for obstacles"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Baking Actions", EditorStyles.boldLabel);

            _sectionToBake = EditorGUILayout.Vector3IntField("Section to Bake", _sectionToBake);
            int3 sectionInt3 = new int3(_sectionToBake.x, _sectionToBake.y, _sectionToBake.z);
            if (GUILayout.Button("Bake Specified Section", GUILayout.Height(30)))
            {
                var sceneGuid = SceneGraphEditorSettings.instance.GetCurrentSceneGuid();
                SceneGraphBakingService.BakeSection(sectionInt3, SceneGraphEditorSettings.instance.GeometryLayer, SceneGraphEditorSettings.instance.ObstacleLayer, sceneGuid);
            }

            
            if (GUILayout.Button("Bake Section under Mouse", GUILayout.Height(30)))
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    Ray mouseRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                    if (Physics.Raycast(mouseRay, out RaycastHit hit, 1000f, SceneGraphEditorSettings.instance.GeometryLayer))
                    {
                        SceneGraphMath.WorldToGraph(hit.point, out int3 section, out _, out _, out _);
                        var sceneGuid = SceneGraphEditorSettings.instance.GetCurrentSceneGuid();
                        SceneGraphBakingService.BakeSection(section, SceneGraphEditorSettings.instance.GeometryLayer, SceneGraphEditorSettings.instance.ObstacleLayer, sceneGuid);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Bake Section", "No geometry found under mouse position.", "OK");
                    }
                }
            }
            
            if (GUILayout.Button("Bake Entire Scene", GUILayout.Height(30)))
            {
                var scene = SceneManager.GetActiveScene();
                SceneGraphBakingService.BakeScene(scene, SceneGraphEditorSettings.instance.GeometryLayer, SceneGraphEditorSettings.instance.ObstacleLayer);
            }
            
            EditorGUILayout.Space();
            if (GUILayout.Button("Save Settings", GUILayout.Height(30)))
            {
                _serializedSettings.ApplyModifiedProperties();
                SceneGraphEditorSettings.instance.SaveSettings();
                Debug.Log("Scene Graph settings saved!");
            }

            _serializedSettings.ApplyModifiedProperties();
        }
    }
}



