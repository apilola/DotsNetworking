# Alternative ECS Designs for World Graph Double Buffering

This document explores alternative ECS-focused designs for the world graph snapshot system, analyzing pros and cons of each approach.

## Design 1: Singleton Snapshot Component (Current Plan)

**Structure**: `WorldGraphSnapshot` singleton with `NativeParallelHashMap<int3, RegionEntry>`

### Pros
- ✅ Simple and straightforward
- ✅ Lock-free reads (hash map is parallel-safe)
- ✅ O(1) lookup by region key
- ✅ Minimal memory overhead (just duplicate references)
- ✅ Clear ownership (WorldGraphSystem writes, others read)
- ✅ Burst-compatible (NativeParallelHashMap)
- ✅ No entity overhead

### Cons
- ❌ Manual snapshot management (rebuild on batch completion)
- ❌ Duplicate storage of RegionEntry data
- ❌ Not leveraging ECS change tracking
- ❌ Requires explicit batch completion detection

---

## Design 2: Entity-Per-Region Pattern

**Structure**: Each region is an entity with components:
- `RegionKey : IComponentData` (int3)
- `RegionEntry : IComponentData` (full entry data)
- `RegionLoaded : IComponentData` (tag, only when loaded)

### Pros
- ✅ Native ECS patterns (query, filter, iterate)
- ✅ Automatic change tracking via `ComponentLookup<T>`
- ✅ Can use `SystemAPI.Query<RegionKey, RegionEntry>().WithAll<RegionLoaded>()`
- ✅ Enable/disable components for load state
- ✅ Leverages ECS chunking for cache efficiency
- ✅ Can use `EntityCommandBuffer` for batch operations
- ✅ Natural fit for ECS systems

### Cons
- ❌ Entity overhead (~16 bytes per region + archetype overhead)
- ❌ BlobAssetReference in component (works but unusual)
- ❌ Harder to get O(1) lookup (need to build index or iterate)
- ❌ Structural changes (add/remove entities) are expensive
- ❌ Requires entity management (create/destroy on load/unload)
- ❌ Less efficient for "get region by key" queries

### Implementation Sketch
```csharp
public struct RegionKey : IComponentData { public int3 Value; }
public struct RegionData : IComponentData { public RegionEntry Entry; }
public struct RegionLoaded : IComponentData { } // Tag

// Query loaded regions
var query = SystemAPI.QueryBuilder()
    .WithAll<RegionKey, RegionData, RegionLoaded>()
    .Build();
```

---

## Design 3: DynamicBuffer Snapshot

**Structure**: `WorldGraphSnapshot` singleton with `DynamicBuffer<RegionSnapshot>`

### Pros
- ✅ Native ECS component (DynamicBuffer)
- ✅ Can use `SystemAPI.GetSingletonBuffer<RegionSnapshot>()`
- ✅ Burst-compatible
- ✅ Automatic memory management
- ✅ Can use jobs with `IJobEntityBatch`
- ✅ Change tracking via `ComponentLookup<T>`

### Cons
- ❌ O(n) lookup (must iterate to find region)
- ❌ Need to maintain sorted order or build index
- ❌ Less efficient for random access
- ❌ Still requires manual snapshot rebuild
- ❌ Buffer resizing can cause allocations

### Implementation Sketch
```csharp
public struct RegionSnapshot : IBufferElementData
{
    public int3 RegionKey;
    public RegionEntry Entry;
}

// Lookup requires iteration or separate index
```

---

## Design 4: Enableable Components Pattern

**Structure**: Use enableable components to mark loaded state:
- `RegionKey : IComponentData` (always present)
- `RegionData : IComponentData, IEnableableComponent` (enabled when loaded)

### Pros
- ✅ Native ECS enable/disable pattern
- ✅ Efficient filtering (`WithAll<RegionData>()` only gets enabled)
- ✅ No entity creation/destruction needed
- ✅ Change tracking via `ComponentLookup<T>.IsComponentEnabled()`
- ✅ Can batch enable/disable operations
- ✅ Cache-friendly (chunks remain stable)

### Cons
- ❌ Still need entities for each region (entity overhead)
- ❌ O(n) lookup unless building separate index
- ❌ Enable/disable operations are structural changes (expensive)
- ❌ BlobAssetReference in component (unusual but works)
- ❌ Requires entity management

### Implementation Sketch
```csharp
public struct RegionKey : IComponentData { public int3 Value; }
public struct RegionData : IComponentData, IEnableableComponent 
{ 
    public RegionEntry Entry; 
}

// Enable when loaded, disable when unloaded
lookup.SetComponentEnabled(entity, true/false);
```

---

## Design 5: SharedComponentData Grouping

**Structure**: Use SharedComponentData to group regions by world/state:
- `RegionShared : ISharedComponentData` (world ID, load state)
- `RegionKey : IComponentData` (per-region)
- `RegionData : IComponentData` (per-region)

### Pros
- ✅ Automatic chunking by shared component
- ✅ Efficient iteration of regions in same state
- ✅ Native ECS pattern
- ✅ Can filter by shared component value

### Cons
- ❌ SharedComponentData changes are expensive (moves entities)
- ❌ Not suitable for frequent state changes
- ❌ Still need entities
- ❌ O(n) lookup
- ❌ Less flexible for dynamic loading/unloading

### Implementation Sketch
```csharp
public struct RegionShared : ISharedComponentData
{
    public int WorldId;
    public RegionState State;
}
```

---

## Design 6: Component Change Events + Singleton

**Structure**: Hybrid approach:
- Keep `WorldGraph` singleton (working state)
- Use `ComponentLookup<WorldGraph>` with change tracking
- Publish to `WorldGraphSnapshot` when changes detected

### Pros
- ✅ Leverages ECS change tracking
- ✅ Automatic detection of changes
- ✅ No manual batch completion tracking
- ✅ Still maintains singleton snapshot for reads

### Cons
- ❌ Change tracking only works on components, not native containers
- ❌ Would need to restructure WorldGraph as multiple components
- ❌ More complex architecture
- ❌ May trigger updates too frequently

### Implementation Sketch
```csharp
// Split WorldGraph into multiple components
public struct WorldGraphRegions : IComponentData { ... }
public struct WorldGraphState : IComponentData { ... }

var lookup = SystemAPI.GetComponentLookup<WorldGraphRegions>(true);
if (lookup.DidChange(singletonEntity, lastVersion))
{
    PublishSnapshot();
}
```

---

## Design 7: Chunk-Based Archetypes

**Structure**: Different archetypes for different states:
- Archetype 1: `RegionKey + RegionData` (loaded regions)
- Archetype 2: `RegionKey` only (unloaded regions)

### Pros
- ✅ Automatic filtering via archetype
- ✅ Cache-efficient iteration
- ✅ No enable/disable needed
- ✅ Clear separation of loaded/unloaded

### Cons
- ❌ Entity moves between archetypes (expensive)
- ❌ Still need entities
- ❌ O(n) lookup
- ❌ Complex entity management
- ❌ Archetype changes are structural (very expensive)

---

## Design 8: Hybrid Entity Metadata + Singleton Data

**Structure**: 
- Entities for lightweight metadata (`RegionKey`, `RegionId`)
- Singleton hash map for full `RegionEntry` data
- Use entities to track which regions exist

### Pros
- ✅ Best of both worlds (ECS queries + O(1) lookup)
- ✅ Can query "which regions are loaded" via entities
- ✅ Can lookup full data via singleton hash map
- ✅ Entities are lightweight (just keys)
- ✅ Leverages ECS for iteration, hash map for lookup

### Cons
- ❌ More complex (two data structures)
- ❌ Need to keep entities and hash map in sync
- ❌ Still requires entity management
- ❌ More memory overhead

### Implementation Sketch
```csharp
// Entity with just key
public struct RegionKey : IComponentData { public int3 Value; }
public struct RegionLoaded : IComponentData { } // Tag

// Singleton with full data
public struct WorldGraphSnapshot : IComponentData
{
    public NativeParallelHashMap<int3, RegionEntry> Regions;
}

// Usage:
// 1. Query entities to iterate: SystemAPI.Query<RegionKey>().WithAll<RegionLoaded>()
// 2. Lookup full data: snapshot.Regions[regionKey]
```

---

## Comparison Matrix

| Design | O(1) Lookup | ECS Native | Entity Overhead | Change Tracking | Burst Compatible | Complexity |
|--------|-------------|------------|-----------------|-----------------|------------------|------------|
| **1. Singleton Snapshot** | ✅ | ❌ | ✅ None | ❌ Manual | ✅ | ⭐ Low |
| **2. Entity-Per-Region** | ❌ | ✅ | ❌ High | ✅ Auto | ✅ | ⭐⭐ Medium |
| **3. DynamicBuffer** | ❌ | ✅ | ✅ None | ✅ Auto | ✅ | ⭐⭐ Medium |
| **4. Enableable** | ❌ | ✅ | ❌ High | ✅ Auto | ✅ | ⭐⭐ Medium |
| **5. SharedComponent** | ❌ | ✅ | ❌ High | ❌ Manual | ✅ | ⭐⭐⭐ High |
| **6. Change Events** | ✅ | ⚠️ Partial | ✅ None | ✅ Auto | ✅ | ⭐⭐⭐ High |
| **7. Chunk Archetypes** | ❌ | ✅ | ❌ High | ✅ Auto | ✅ | ⭐⭐⭐ High |
| **8. Hybrid Entity+Singleton** | ✅ | ⚠️ Partial | ⚠️ Medium | ⚠️ Partial | ✅ | ⭐⭐⭐ High |

---

## Recommendation

**For this use case, Design 1 (Singleton Snapshot) is optimal** because:

1. **Lookup Performance**: O(1) hash map lookup is critical for pathfinding
2. **Read-Heavy**: System is read-mostly (pathfinding queries regions frequently)
3. **Burst Compatibility**: NativeParallelHashMap works perfectly in Burst jobs
4. **Simplicity**: Minimal complexity, clear ownership
5. **Memory**: Duplicate storage is minimal (just struct copies, blob refs are handles)

**However, if you need ECS-native iteration patterns**, consider **Design 8 (Hybrid)**:
- Use entities for "which regions exist" queries
- Use singleton hash map for O(1) data lookup
- Best of both worlds, but more complex

**Avoid Designs 2, 4, 7** for this use case because:
- Entity overhead is significant for potentially hundreds of regions
- O(n) lookup is too slow for pathfinding
- Structural changes (entity add/remove/enable) are expensive

---

## Hybrid Approach: Best of Both Worlds

If you want ECS-native patterns **and** O(1) lookup, consider a **two-tier system**:

```csharp
// Tier 1: ECS entities for iteration/queries
public struct RegionKey : IComponentData { public int3 Value; }
public struct RegionLoaded : IComponentData { } // Tag

// Tier 2: Singleton for O(1) lookup
public struct WorldGraphSnapshot : IComponentData
{
    public NativeParallelHashMap<int3, RegionEntry> Regions;
}

// Usage patterns:
// - Iterate: SystemAPI.Query<RegionKey>().WithAll<RegionLoaded>()
// - Lookup: snapshot.Regions[regionKey]
```

This gives you:
- ✅ ECS queries for "all loaded regions"
- ✅ O(1) lookup for pathfinding
- ✅ Change tracking on entities
- ⚠️ More complex (two data structures to maintain)