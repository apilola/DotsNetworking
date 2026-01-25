using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DotsNetworking.WorldGraph.Editor
{
    /// <summary>
    /// Maps a scene asset to a world ID for baking.
    /// </summary>
    [System.Serializable]
    public struct SceneWorldMapping
    {
        [SerializeField]
        public SceneAsset SceneAsset;
        
        [SerializeField]
        public UInt16 WorldId;
        
        public SceneWorldMapping(SceneAsset scene, UInt16 worldId)
        {
            SceneAsset = scene;
            WorldId = worldId;
        }
    }

    [FilePath("ProjectSettings/WorldGraphEditorSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class WorldGraphEditorSettings : ScriptableSingleton<WorldGraphEditorSettings>
    {
        [Header("Generation Rules")]
        [Tooltip("Layer mask defining what objects are considered static geometry for the World Graph.")]
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
        
        [Header("World Mapping")]
        [Tooltip("Maps scene assets to world IDs for baking and loading.")]
        [SerializeField]
        private SceneWorldMapping[] m_SceneWorldMappings = System.Array.Empty<SceneWorldMapping>();
        
        public SceneWorldMapping[] SceneWorldMappings
        {
            get => m_SceneWorldMappings;
            set
            {
                if (m_SceneWorldMappings != value)
                {
                    m_SceneWorldMappings = value;
                    Save(true);
                }
            }
        }
        
        /// <summary>
        /// Get the world ID for the currently active scene.
        /// Throws exception if scene is not mapped.
        /// </summary>
        public int GetCurrentSceneWorldId()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            return GetWorldIdForScene(activeScene.name);
        }
        
        /// <summary>
        /// Get the world ID for a named scene.
        /// Throws exception if scene is not found in mappings.
        /// </summary>
        public int GetWorldIdForScene(string sceneName)
        {
            if(string.IsNullOrEmpty(sceneName))
            {
                return 0;
            }

            foreach (var mapping in m_SceneWorldMappings)
            {
                if (mapping.SceneAsset != null && mapping.SceneAsset.name == sceneName)
                {
                    return mapping.WorldId;
                }
            }

            throw new InvalidOperationException($"Scene '{sceneName}' is not mapped to a world ID. Please configure it in World Graph Editor Settings.");
        }
        
        // Helper to ensure we save when modified via SerializedObject
        public void SaveSettings()
        {
            Save(true);
        }
    }
}
