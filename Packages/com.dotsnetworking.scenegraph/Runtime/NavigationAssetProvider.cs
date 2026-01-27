using System;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph
{
    public static class NavigationAssetProvider
    {
        private static TextAssetBlobPipeline<Section> _pipeline = new TextAssetBlobPipeline<Section>(0);

        private static string GetPath(EntitiesHash128 sceneGuid, uint sectionIndex)
        {
            return $"Data/SubScene_{sceneGuid}/Section_{sectionIndex}";
        }

        public static string GetResourceKey(EntitiesHash128 sceneGuid, uint sectionIndex)
        {
            return GetPath(sceneGuid, sectionIndex);
        }

        public static BlobAssetHandle<Section> CheckOut(SectionAddress sectionAddress)
        {
            return _pipeline.CheckOut(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static BlobAssetHandle<Section> CheckOut(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            return _pipeline.CheckOut(GetPath(sceneGuid, (uint)sectionIndex));
        }

        public static void CheckOutAsync(SectionAddress sectionAddress, Action<BlobAssetHandle<Section>> onComplete)
        {
            _pipeline.CheckOut(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId), onComplete);
        }

        public static void CheckOutAsync(EntitiesHash128 sceneGuid, int sectionIndex, Action<BlobAssetHandle<Section>> onComplete)
        {
            _pipeline.CheckOut(GetPath(sceneGuid, (uint)sectionIndex), onComplete);
        }

        public static void Release(SectionAddress sectionAddress)
        {
            _pipeline.Release(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static void Release(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            _pipeline.Release(GetPath(sceneGuid, (uint)sectionIndex));
        }
        
        public static void Unload(SectionAddress sectionAddress)
        {
            _pipeline.Unload(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static void Unload(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            _pipeline.Unload(GetPath(sceneGuid, (uint)sectionIndex));
        }

        public static bool IsLoaded(SectionAddress sectionAddress)
        {
            return _pipeline.IsLoaded(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static bool IsLoaded(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            return _pipeline.IsLoaded(GetPath(sceneGuid, (uint)sectionIndex));
        }

        public static void ForceReloadOfBlobAsset(string assetPath)
        {
            _pipeline.AttemptForceKeyReload(assetPath);
        }

        public static void ForceReloadOfBlobAsset(SectionAddress sectionAddress)
        {
            _pipeline.AttemptForceKeyReload(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static void ForceReloadOfBlobAsset(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            _pipeline.AttemptForceKeyReload(GetPath(sceneGuid, (uint)sectionIndex));
        }
        
#if UNITY_EDITOR
        //[UnityEditor.InitializeOnEnterPlayMode]
        //static void OnEnterPlayMode() => _pipeline.Clear();

        [InitializeOnLoadMethod]
        private static void InitCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload += _pipeline.Clear;
            EditorApplication.quitting += _pipeline.Clear;
        }
#endif
    }
}



