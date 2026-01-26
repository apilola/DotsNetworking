using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph.Editor
{
    [FilePath("ProjectSettings/SceneGraphEditorSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class SceneGraphEditorSettings : ScriptableSingleton<SceneGraphEditorSettings>
    {
        [Header("Generation Rules")]
        [Tooltip("Layer mask defining what objects are considered static geometry for the Scene Graph.")]
        [SerializeField]
        private LayerMask m_GeometryLayer = ~0; // Default to Everything

        public LayerMask GeometryLayer
        {
            get => m_GeometryLayer;
            set
            {
                if (m_GeometryLayer != value)
                {
                    m_GeometryLayer = value;
                    Save(true);
                }
            }
        }

        [Tooltip("Layer mask defining what objects are considered obstacles upon the geometry.")]
        [SerializeField]
        private LayerMask m_ObstacleLayer = ~0; // Default to Everything

        public LayerMask ObstacleLayer
        {
            get => m_ObstacleLayer;
            set
            {
                if (m_ObstacleLayer != value)
                {
                    m_ObstacleLayer = value;
                    Save(true);
                }
            }
        }

        public EntitiesHash128 GetCurrentSceneGuid()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            return GetSceneGuidForScene(activeScene.path);
        }

        public EntitiesHash128 GetSceneGuidForScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return default;
            }

            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                return default;
            }

            return new EntitiesHash128(guid);
        }
        
        // Helper to ensure we save when modified via SerializedObject
        public void SaveSettings()
        {
            Save(true);
        }
    }
}



