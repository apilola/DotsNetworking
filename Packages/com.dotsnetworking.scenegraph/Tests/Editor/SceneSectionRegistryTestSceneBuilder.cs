using System.IO;
using BovineLabs.Core.Editor.Settings;
using DotsNetworking.SceneGraph;
using DotsNetworking.SceneGraph.Authoring;
using DotsNetworking.SceneGraph.Editor;
using DotsNetworking.SceneGraph.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Unity.Scenes;
using Unity.Scenes.Editor;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph.Tests.Editor
{
    public sealed class SceneSectionRegistryTestSceneBuilder : IPrebuildSetup, IPostBuildCleanup
    {
        public const string TempRoot = "Packages/com.dotsnetworking.scenegraph/Tests/TempAssets";
        public const string ParentScenePath = TempRoot + "/SceneGraphRegistryTest.unity";
        public const string SubSceneName = "SceneGraphRegistryTest_SubScene";

        public static EntitiesHash128 SceneGuid { get; private set; }
        public static string SubScenePath { get; private set; }

        public void Setup()
        {
            Build();
        }

        public void Cleanup()
        {
            CleanupAssets();
        }

        public static void Build()
        {
            EnsureTempRoot();
            DeleteIfExists(ParentScenePath);

            var parentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(parentScene, ParentScenePath);

            var subScene = CreateSubScene(parentScene);
            var subSceneEditing = subScene.EditingScene;

            CreateTestGeometry(subSceneEditing);
            CreateBakeAuthoring(subSceneEditing);

            EditorSceneManager.SaveScene(subSceneEditing);
            EditorSceneManager.SaveScene(parentScene);

            Physics.SyncTransforms();

            var geometryLayer = SceneGraphEditorSettings.instance.GeometryLayer;
            var obstacleLayer = SceneGraphEditorSettings.instance.ObstacleLayer;

            SceneGraphBakingService.BakeScene(subSceneEditing, geometryLayer, obstacleLayer);
            SceneGraphBakingService.RebuildManifest(subSceneEditing);

            SceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(subSceneEditing.path);
            SubScenePath = subSceneEditing.path;

            SubSceneInspectorUtility.CloseSceneWithoutSaving(subScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void CleanupAssets()
        {
            if (SceneGuid.Equals(default) && !string.IsNullOrEmpty(SubScenePath))
            {
                SceneGuid = SceneGraphEditorSettings.instance.GetSceneGuidForScene(SubScenePath);
            }

            if (!SceneGuid.Equals(default))
            {
                var manifest = EditorSettingsUtility.GetSettings<SceneGraphManifest>();
                if (manifest.RemoveSubscene(SceneGuid))
                {
                    EditorUtility.SetDirty(manifest);
                    AssetDatabase.SaveAssets();
                }

                string navFolder = $"Assets/Resources/SceneGraph/{SceneGuid}";
                if (AssetDatabase.IsValidFolder(navFolder))
                {
                    AssetDatabase.DeleteAsset(navFolder);
                }
            }

            DeleteIfExists(SubScenePath);
            DeleteIfExists(ParentScenePath);

            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static SubScene CreateSubScene(Scene parentScene)
        {
            SceneManager.SetActiveScene(parentScene);

            var subScenePath = $"{TempRoot}/{SubSceneName}.unity";
            DeleteIfExists(subScenePath);

            var subSceneEditing = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SubSceneInspectorUtility.SetSceneAsSubScene(subSceneEditing);
            EditorSceneManager.SaveScene(subSceneEditing, subScenePath);

            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(subScenePath);
            var go = new GameObject(SubSceneName);
            var subScene = go.AddComponent<SubScene>();
            subScene.SceneAsset = sceneAsset;
            subScene.AutoLoadScene = false;

            SceneManager.MoveGameObjectToScene(go, parentScene);
            SubSceneUtility.EditScene(subScene);
            return subScene;
        }

        private static void CreateTestGeometry(Scene subSceneEditing)
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "SceneGraph_TestPlane";
            plane.layer = GetFirstLayerFromMask(SceneGraphEditorSettings.instance.GeometryLayer);

            var pos = new Vector3(SceneGraphConstants.ChunkSizeX * 0.5f, 0f, SceneGraphConstants.ChunkSizeZ * 0.5f);
            plane.transform.position = pos;

            var scaleX = SceneGraphConstants.ChunkSizeX / 10f;
            var scaleZ = SceneGraphConstants.ChunkSizeZ / 10f;
            plane.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

            SceneManager.MoveGameObjectToScene(plane, subSceneEditing);
        }

        private static void CreateBakeAuthoring(Scene subSceneEditing)
        {
            var go = new GameObject("SceneGraphBakeAuthoring");
            go.AddComponent<SceneGraphBakeAuthoring>();
            SceneManager.MoveGameObjectToScene(go, subSceneEditing);
        }

        private static int GetFirstLayerFromMask(LayerMask mask)
        {
            int value = mask.value;
            for (int i = 0; i < 32; i++)
            {
                if ((value & (1 << i)) != 0)
                {
                    return i;
                }
            }

            return 0;
        }

        private static void EnsureTempRoot()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
                return;

            string parent = "Packages/com.dotsnetworking.scenegraph/Tests";
            if (!AssetDatabase.IsValidFolder(parent))
            {
                AssetDatabase.CreateFolder("Packages/com.dotsnetworking.scenegraph", "Tests");
            }

            AssetDatabase.CreateFolder(parent, "TempAssets");
        }

        private static void DeleteIfExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (AssetDatabase.LoadAssetAtPath<Object>(path) != null || AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
