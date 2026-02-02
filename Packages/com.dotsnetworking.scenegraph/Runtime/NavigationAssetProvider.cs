using System;
using System.Collections.Generic;
using DotsNetworking.SceneGraph.Components;
using Unity.Entities;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph
{
    public static class NavigationAssetProvider
    {
        private sealed class Entry
        {
            public int RefCount;
            public readonly string Key;
            public BlobAssetHandler Asset;
            public ResourceRequest Request;
            public Action<BlobAssetHandler> Callbacks;

            public Entry(string key)
            {
                Key = key;
            }
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        private static string GetPath(EntitiesHash128 sceneGuid, uint sectionIndex)
        {
            return $"SceneGraph/{sceneGuid}/Section_{sectionIndex}";
        }

        public static string GetResourceKey(EntitiesHash128 sceneGuid, uint sectionIndex)
        {
            return GetPath(sceneGuid, sectionIndex);
        }

        public static BlobAssetHandler CheckOut(SectionAddress sectionAddress)
        {
            return CheckOut(sectionAddress.SceneGuid, (int)sectionAddress.SectionId);
        }

        public static BlobAssetHandler CheckOut(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            string key = GetPath(sceneGuid, (uint)sectionIndex);
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                Entries[key] = entry;
            }

            if (entry.Asset != null)
            {
                entry.RefCount++;
                return entry.Asset;
            }

            var asset = Resources.Load<BlobAssetHandler>(key);
            if (asset == null)
                return null;

            entry.Asset = asset;
            entry.RefCount++;

            if (entry.Callbacks != null)
            {
                var cbs = entry.Callbacks;
                entry.Callbacks = null;
                cbs.Invoke(entry.Asset);
            }

            return entry.Asset;
        }

        public static void CheckOutAsync(SectionAddress sectionAddress, Action<BlobAssetHandler> onComplete)
        {
            CheckOutAsync(sectionAddress.SceneGuid, (int)sectionAddress.SectionId, onComplete);
        }

        public static void CheckOutAsync(EntitiesHash128 sceneGuid, int sectionIndex, Action<BlobAssetHandler> onComplete)
        {
            if (onComplete == null)
                return;

            string key = GetPath(sceneGuid, (uint)sectionIndex);
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                Entries[key] = entry;
            }

            if (entry.Asset != null)
            {
                entry.RefCount++;
                onComplete(entry.Asset);
                return;
            }

            entry.RefCount++;
            entry.Callbacks += onComplete;

            if (entry.Request != null && !entry.Request.isDone)
                return;

            entry.Request = Resources.LoadAsync<BlobAssetHandler>(key);
            entry.Request.completed += _ => OnRequestCompleted(entry);
        }

        public static void Release(SectionAddress sectionAddress)
        {
            Release(sectionAddress.SceneGuid, (int)sectionAddress.SectionId);
        }

        public static void Release(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            string key = GetPath(sceneGuid, (uint)sectionIndex);
            if (!Entries.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            if (entry.Asset != null)
                Resources.UnloadAsset(entry.Asset);

            entry.Asset = null;
            entry.Callbacks = null;
            entry.Request = null;

            Entries.Remove(key);
        }

        public static void Unload(SectionAddress sectionAddress)
        {
            Unload(sectionAddress.SceneGuid, (int)sectionAddress.SectionId);
        }

        public static void Unload(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            string key = GetPath(sceneGuid, (uint)sectionIndex);
            if (!Entries.TryGetValue(key, out var entry))
                return;

            if (entry.Asset != null)
                Resources.UnloadAsset(entry.Asset);

            if (entry.Callbacks != null)
            {
                var cbs = entry.Callbacks;
                entry.Callbacks = null;
                cbs.Invoke(null);
            }

            entry.Asset = null;
            entry.Request = null;

            Entries.Remove(key);
        }

        public static bool IsLoaded(SectionAddress sectionAddress)
        {
            return IsLoaded(sectionAddress.SceneGuid, (int)sectionAddress.SectionId);
        }

        public static bool IsLoaded(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            string key = GetPath(sceneGuid, (uint)sectionIndex);
            return Entries.TryGetValue(key, out var entry) && entry.Asset != null;
        }

        public static void ForceReloadOfBlobAsset(string assetPath)
        {
            AttemptForceKeyReload(assetPath);
        }

        public static void ForceReloadOfBlobAsset(SectionAddress sectionAddress)
        {
            AttemptForceKeyReload(GetPath(sectionAddress.SceneGuid, sectionAddress.SectionId));
        }

        public static void ForceReloadOfBlobAsset(EntitiesHash128 sceneGuid, int sectionIndex)
        {
            AttemptForceKeyReload(GetPath(sceneGuid, (uint)sectionIndex));
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
        }
#endif

        public static void Clear()
        {
            foreach (var kvp in Entries)
            {
                var entry = kvp.Value;
                if (entry.Asset != null)
                    Resources.UnloadAsset(entry.Asset);

                if (entry.Callbacks != null)
                {
                    var cbs = entry.Callbacks;
                    entry.Callbacks = null;
                    cbs.Invoke(null);
                }
            }

            Entries.Clear();
        }

        private static void OnRequestCompleted(Entry entry)
        {
            if (entry == null)
                return;

            var asset = entry.Request?.asset as BlobAssetHandler;
            entry.Request = null;

            if (asset != null)
            {
                entry.Asset = asset;

                if (entry.Callbacks != null)
                {
                    var cbs = entry.Callbacks;
                    entry.Callbacks = null;
                    cbs.Invoke(entry.Asset);
                }

                return;
            }

            if (entry.Callbacks != null)
            {
                int n = entry.Callbacks.GetInvocationList().Length;
                var cbs = entry.Callbacks;
                entry.Callbacks = null;
                cbs.Invoke(null);
                entry.RefCount -= n;
            }

            if (entry.RefCount <= 0)
            {
                Entries.Remove(entry.Key);
            }
            else
            {
                entry.Asset = null;
            }
        }

        private static void AttemptForceKeyReload(string key)
        {
            var asset = Resources.Load<BlobAssetHandler>(key);
            if (asset == null)
                return;

            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                Entries[key] = entry;
            }
            else if (entry.Asset != null && entry.Asset != asset)
            {
                Resources.UnloadAsset(entry.Asset);
            }

            entry.Asset = asset;
        }
    }
}
