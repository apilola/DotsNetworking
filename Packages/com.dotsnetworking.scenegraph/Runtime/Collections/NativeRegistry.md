# NativeRegistry

`NativeRegistry<TKey>` is a native container that stores per-key values for multiple unmanaged types.
It is designed for fast, job-friendly access in DOTS systems where data is indexed by a stable key
(`TKey`) and each key needs a value for every registered type.

## Core ideas

- **Per-key, per-type storage**: each registered type has its own internal `UnsafePagedList<T>` for
  values and a parallel `UnsafePagedList<int>` for refcounts/locking.
- **Stable indices**: a `TKey` is mapped to an integer index via an `UnsafeParallelHashMap`.
  All type storages use that same index for the key.
- **Handles**: readers use `RegistryReader<T>` and writers use `RegistryWriter<T>`.
  Writers acquire a write intent bit; actual write permission is granted only when readers drain.
- **Locking model**: read acquires increment a counter; write uses intent + lock bits stored in the
  refcount slot. Readers fail fast when a writer is pending or active.

## Typical usage

1. Create the registry (often with `NativeRegistryBuilder.Create<TKey>(..., params Type[])`).
2. Register keys (e.g., section addresses).
3. Acquire a reader or writer handle for a key + type, use `Value`, then dispose the handle.

```csharp
var registry = NativeRegistryBuilder.Create<SectionAddress>(
    Allocator.Persistent,
    pageSize: 64,
    keyCapacity: manifest.SectionCount,
    typeof(BlobAssetReference<Section>),
    typeof(Entity));

registry.RegisterKey(address);
using (var writer = registry.AcquireWrite<Entity>(address))
{
    if (writer.CanWrite)
        writer.Value = entity;
}
```

## Notes

- `RegisterKey` now returns the index for the key (existing or newly created).
- Safety checks are enabled under `ENABLE_UNITY_COLLECTIONS_CHECKS`.
- `Dispose` releases all native allocations; always call it when done.
