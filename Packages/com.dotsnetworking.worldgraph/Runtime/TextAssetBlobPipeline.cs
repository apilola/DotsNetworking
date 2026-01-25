using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace DotsNetworking.WorldGraph
{
    /// <summary>
    /// Borrowed handle that always reads the *current* blob value from the entry's shared slot.
    /// IMPORTANT: The handle does not own the slot. The pipeline owns/disposes it.
    /// </summary>
    public struct BlobAssetHandle<T> where T : unmanaged
    {
        private readonly NativeReference<BlobAssetReference<T>> _blobRef;

        internal BlobAssetHandle(NativeReference<BlobAssetReference<T>> blobRef)
        {
            _blobRef = blobRef;
        }

        /// <summary>
        /// True if the slot exists and currently contains a created blob.
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (!_blobRef.IsCreated)
                {
                    return false;
                }

                return _blobRef.Value.IsCreated;
            }
        }

        /// <summary>
        /// Returns the current blob from the shared slot, or default if invalid.
        /// NOTE: Avoid caching this value long-term if the underlying bytes may be invalidated by reimport/unload.
        /// Prefer keeping the handle and re-reading Blob when needed.
        /// </summary>
        public BlobAssetReference<T> Blob => IsValid ? _blobRef.Value : default;
    }

    /// <summary>
    /// Pipeline that loads TextAssets and exposes a "live" handle.
    /// When reimport/unload happens, the pipeline can invalidate all handles by setting the shared slot to default.
    /// </summary>
    public sealed class TextAssetBlobPipeline<T> : IDisposable where T : unmanaged
    {
        public sealed class Entry : IDisposable
        {
            public int RefCount;
            public readonly string Key;

            public TextAsset Asset;

            // Shared slot for the current blob. All handles borrow this.
            public NativeReference<BlobAssetReference<T>> BlobRef;

            // Async state
            public ResourceRequest Request;
            public Action<BlobAssetHandle<T>> Callbacks;

            public Entry(string key)
            {
                Key = key;
                BlobRef = new NativeReference<BlobAssetReference<T>>(Allocator.Persistent);
                BlobRef.Value = default;
            }

            public void Dispose()
            {
                if (BlobRef.IsCreated)
                {
                    BlobRef.Dispose();
                }
            }
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly int _expectedVersion;

        private bool _disposed;

        // Reflection MethodInfo (Delegate creation proved unstable across Unity versions/platforms)
        private static readonly MethodInfo _tryReadInplaceMethod;

        static TextAssetBlobPipeline()
        {
            _tryReadInplaceMethod = typeof(BlobAssetReference<T>)
                .GetMethod("TryReadInplace", BindingFlags.NonPublic | BindingFlags.Static);

            if (_tryReadInplaceMethod == null)
            {
                Debug.LogError(
                    $"Could not find BlobAssetReference<{typeof(T).Name}>.TryReadInplace via reflection.");
            }
        }

        public TextAssetBlobPipeline(int expectedVersion)
        {
            _expectedVersion = expectedVersion;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Invalidate all live handles first (slot -> default), then unload assets, then dispose slots.
            foreach (var kvp in _entries)
            {
                var entry = kvp.Value;
                if (entry.BlobRef.IsCreated)
                    entry.BlobRef.Value = default;

                if (entry.Asset != null)
                    Resources.UnloadAsset(entry.Asset);

                entry.Callbacks = null;
                entry.Request = null;

                entry.Dispose();
            }

            _entries.Clear();
        }

        /// <summary>
        /// Clears all entries. Invalidates handles (slot -> default), unloads assets, disposes slots.
        /// </summary>
        public void Clear()
        {
            EnsureNotDisposed();

            foreach (var kvp in _entries)
            {
                var entry = kvp.Value;

                // Invalidate outstanding handles immediately
                if (entry.BlobRef.IsCreated)
                    entry.BlobRef.Value = default;

                // Notify pending callbacks as invalid (optional, but avoids hanging callers)
                if (entry.Callbacks != null)
                {
                    var cbs = entry.Callbacks;
                    entry.Callbacks = null;
                    cbs.Invoke(default);
                }

                if (entry.Asset != null)
                    Resources.UnloadAsset(entry.Asset);

                entry.Request = null;
                entry.Dispose();
            }

            _entries.Clear();
        }

        public bool IsLoaded(string key)
        {
            EnsureNotDisposed();

            return _entries.TryGetValue(key, out var entry) &&
                   entry.BlobRef.IsCreated &&
                   entry.BlobRef.Value.IsCreated;
        }

        /// <summary>
        /// Main async checkout. Multiple callers coalesce into one ResourceRequest.
        /// Handles always read the current blob from the entry's shared slot.
        /// </summary>
        public void CheckOut(string key, Action<BlobAssetHandle<T>> onComplete)
        {
            EnsureNotDisposed();
            if (onComplete == null) return;

            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                _entries[key] = entry;
            }

            // If already loaded, complete immediately
            if (entry.BlobRef.IsCreated && entry.BlobRef.Value.IsCreated)
            {
                entry.RefCount++;
                onComplete(new BlobAssetHandle<T>(entry.BlobRef));
                return;
            }

            // Coalesce callbacks
            entry.RefCount++;
            entry.Callbacks += onComplete;

            // If a request is already in flight, just wait
            if (entry.Request != null && !entry.Request.isDone)
                return;

            // Start request
            entry.Request = Resources.LoadAsync<TextAsset>(key);
            entry.Request.completed += _ => OnRequestCompleted(entry);
        }

        /// <summary>
        /// Sync checkout (forces load immediately). Still returns a live handle bound to the shared slot.
        /// </summary>
        public BlobAssetHandle<T> CheckOut(string key)
        {
            EnsureNotDisposed();

            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                _entries[key] = entry;
            }

            // Loaded already
            if (entry.BlobRef.Value.IsCreated)
            {
                entry.RefCount++;
                return new BlobAssetHandle<T>(entry.BlobRef);
            }

            // If async is in flight, sync-load will still return the live handle.
            // We will just perform a synchronous load path and publish into the slot.
            var asset = Resources.Load<TextAsset>(key);
            if (asset == null)
                return default;

            if (!TryLoadBlob(asset, out var blob))
            {
                Resources.UnloadAsset(asset);
                return default;
            }

            // Publish
            entry.Asset = asset;
            entry.BlobRef.Value = blob;
            entry.RefCount++;

            // Complete any pending callbacks (if async was used previously)
            if (entry.Callbacks != null)
            {
                var cbs = entry.Callbacks;
                entry.Callbacks = null;

                // NOTE: each callback corresponds to a prior RefCount++ already done in async path.
                cbs.Invoke(new BlobAssetHandle<T>(entry.BlobRef));
            }

            entry.Request = null;
            return new BlobAssetHandle<T>(entry.BlobRef);
        }

        /// <summary>
        /// Decrement refcount; when it hits 0, invalidate handles, unload asset, dispose slot and remove entry.
        /// </summary>
        public void Release(string key)
        {
            EnsureNotDisposed();

            if (!_entries.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            // Invalidate handles immediately
            if (entry.BlobRef.IsCreated)
                entry.BlobRef.Value = default;

            // If pending callbacks, complete as invalid
            if (entry.Callbacks != null)
            {
                var cbs = entry.Callbacks;
                entry.Callbacks = null;
                cbs.Invoke(default);
            }

            if (entry.Asset != null)
                Resources.UnloadAsset(entry.Asset);

            entry.Request = null;

            entry.Dispose();
            _entries.Remove(key);
        }

        /// <summary>
        /// Force unload and invalidate all handles for this key immediately.
        /// If an async request is in flight, any pending callbacks are completed with default.
        /// </summary>
        public void Unload(string key)
        {
            EnsureNotDisposed();

            if (!_entries.TryGetValue(key, out var entry))
                return;

            // Invalidate all live handles immediately
            if (entry.BlobRef.IsCreated)
                entry.BlobRef.Value = default;

            // Fail any pending callbacks
            if (entry.Callbacks != null)
            {
                var cbs = entry.Callbacks;
                entry.Callbacks = null;
                cbs.Invoke(default);
            }

            if (entry.Asset != null)
                Resources.UnloadAsset(entry.Asset);

            entry.Request = null;

            entry.Dispose();
            _entries.Remove(key);
        }

        /// <summary>
        /// Call this from your importer/editor pipeline when the underlying bytes for a key have changed.
        /// This invalidates existing handles immediately; subsequent CheckOut will load the updated asset.
        /// </summary>
        public void NotifyChanged(string key)
        {
            EnsureNotDisposed();

            if (!_entries.TryGetValue(key, out var entry))
                return;

            // Invalidate existing handles
            if (entry.BlobRef.IsCreated)
                entry.BlobRef.Value = default;

            // Unload old asset (since TryReadInplace views into its bytes)
            if (entry.Asset != null)
            {
                Resources.UnloadAsset(entry.Asset);
                entry.Asset = null;
            }

            // If a request is in-flight, we leave it; callers can CheckOut again.
            // Optionally, you could cancel/fail callbacks here; Unity ResourceRequest can't truly be canceled.
        }

        private void OnRequestCompleted(Entry entry)
        {
            // Entry might have been removed/unloaded before completion
            if (entry == null) return;
            if (_disposed) return;

            var asset = entry.Request?.asset as TextAsset;
            entry.Request = null;

            // If entry was invalidated (slot cleared) due to NotifyChanged/Unload,
            // we still attempt to load whatever completed, but you can decide policy.
            // Here we accept the completion as the current asset and publish it.
            if (asset != null && TryLoadBlob(asset, out var blob))
            {
                entry.Asset = asset;
                entry.BlobRef.Value = blob;

                if (entry.Callbacks != null)
                {
                    var cbs = entry.Callbacks;
                    entry.Callbacks = null;
                    cbs.Invoke(new BlobAssetHandle<T>(entry.BlobRef));
                }
            }
            else
            {
                // Only log error if asset existed but failed to load (blob corruption, version mismatch, etc.)
                // Don't log if asset simply doesn't exist (asset == null is expected for missing regions)
                if (asset != null)
                {
                    Debug.LogError(
                        $"Failed to load BlobAssetReference<{typeof(T).Name}> from TextAsset at key: {entry.Key}");
                    Resources.UnloadAsset(asset);
                }

                // Complete callbacks as invalid and roll back refcounts for those waiters
                if (entry.Callbacks != null)
                {
                    int n = entry.Callbacks.GetInvocationList().Length;

                    var cbs = entry.Callbacks;
                    entry.Callbacks = null;
                    cbs.Invoke(default);

                    entry.RefCount -= n;
                }

                // If no one is holding it anymore, clean up; otherwise keep entry as tombstone
                if (entry.RefCount <= 0)
                {
                    if (entry.BlobRef.IsCreated)
                        entry.BlobRef.Value = default;

                    if (entry.Asset != null)
                    {
                        Resources.UnloadAsset(entry.Asset);
                        entry.Asset = null;
                    }

                    entry.Dispose();
                    _entries.Remove(entry.Key);
                }
                else
                {
                    // Keep entry; handles remain invalid until next successful CheckOut
                    if (entry.BlobRef.IsCreated)
                        entry.BlobRef.Value = default;
                }
            }
        }

        public void AttemptForceKeyReload(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;

            var asset = Resources.Load<TextAsset>(key);
            if (asset == null)
                return;

            if (!TryLoadBlob(asset, out var blob))
                return;
            entry.Asset = asset;
            entry.BlobRef.Value = blob;
        }

        private unsafe bool TryLoadBlob(TextAsset asset, out BlobAssetReference<T> blobRef)
        {
            blobRef = default;

            if (asset == null) return false;
            if (_tryReadInplaceMethod == null) return false;

            var nativeData = asset.GetData<byte>();
            if (!nativeData.IsCreated || nativeData.Length == 0) return false;

            var ptr = (IntPtr)nativeData.GetUnsafeReadOnlyPtr();

            object[] parameters =
            {
                ptr,
                (long)nativeData.Length,
                _expectedVersion,
                default(BlobAssetReference<T>),
                0
            };

            try
            {
                bool success = (bool)_tryReadInplaceMethod.Invoke(null, parameters);
                if (success)
                    blobRef = (BlobAssetReference<T>)parameters[3];

                return success;
            }
            catch (Exception e)
            {
                Debug.LogError($"TryReadInplace invoke failed: {e.Message}");
                return false;
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TextAssetBlobPipeline<T>));
        }
    }
}
