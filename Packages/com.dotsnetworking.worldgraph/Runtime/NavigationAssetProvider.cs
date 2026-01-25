using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using DotsNetworking.WorldGraph.Utils;

namespace DotsNetworking.WorldGraph
{
    public static class NavigationAssetProvider
    {
        private static TextAssetBlobPipeline<Region> _pipeline = new TextAssetBlobPipeline<Region>(0);

        /// <summary>
        /// Get the asset path for a region address.
        /// Address format: [WorldId:16][RegionId:24][ChunkMorton:15][Reserved:9]
        /// </summary>
        private static string GetPath(ulong regionAddress)
        {
            int worldId = (int)((regionAddress >> WorldGraphConstants.NodeIdShift_WorldId) & ((1UL << WorldGraphConstants.NodeIdBits_WorldId) - 1));
            uint regionId = (uint)((regionAddress >> WorldGraphConstants.NodeIdShift_RegionId) & ((1UL << WorldGraphConstants.NodeIdBits_RegionId) - 1));
            
            int3 regionKey = WorldGraphMath.UnpackRegionId(regionId);
            return $"Data/World_{worldId}/Region_{regionKey.x}_{regionKey.y}_{regionKey.z}";
        }

        public static BlobAssetHandle<Region> CheckOut(ulong regionAddress)
        {
            return _pipeline.CheckOut(GetPath(regionAddress));
        }

        public static void CheckOutAsync(ulong regionAddress, Action<BlobAssetHandle<Region>> onComplete)
        {
            _pipeline.CheckOut(GetPath(regionAddress), onComplete);
        }

        public static void Release(ulong regionAddress)
        {
            _pipeline.Release(GetPath(regionAddress));
        }
        
        public static void Unload(ulong regionAddress)
        {
            _pipeline.Unload(GetPath(regionAddress));
        }

        public static bool IsLoaded(ulong regionAddress)
        {
            return _pipeline.IsLoaded(GetPath(regionAddress));
        }

        public static void ForceReloadOfBlobAsset(string assetPath)
        {
            _pipeline.AttemptForceKeyReload(assetPath);
        }

        public static void ForceReloadOfBlobAsset(ulong regionAddress)
        {
            _pipeline.AttemptForceKeyReload(GetPath(regionAddress));
        }
        
        // Legacy overloads for backward compatibility
        public static BlobAssetHandle<Region> CheckOut(int3 regionKey)
        {
            return CheckOut(WorldGraphMath.PackChunkAddress(regionKey, int3.zero, 0));
        }

        public static void Release(int3 regionKey)
        {
            Release(WorldGraphMath.PackChunkAddress(regionKey, int3.zero, 0));
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
