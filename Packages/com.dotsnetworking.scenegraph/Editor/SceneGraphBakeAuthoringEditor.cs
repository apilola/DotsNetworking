using UnityEditor;
using UnityEngine;
using DotsNetworking.SceneGraph.Authoring;

namespace DotsNetworking.SceneGraph.Editor
{
    [CustomEditor(typeof(SceneGraphBakeAuthoring))]
    public class SceneGraphBakeAuthoringEditor : UnityEditor.Editor
    {
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
                }
            }

            if (!hasSceneAsset)
            {
                EditorGUILayout.HelpBox("Place this component in a saved scene to bake.", MessageType.Warning);
            }
        }
    }
}



