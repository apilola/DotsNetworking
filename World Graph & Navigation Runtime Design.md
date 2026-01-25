# World Graph & Navigation Runtime Design

This document captures the current agreed‑upon architecture and design decisions for world navigation, runtime traversal state, occupancy, and server/client differences. It is intended to be **editable**, **iterative**, and to serve as a shared reference while the game is built.

---

## 1. High‑Level Goals

* Support **static navigation data** (regions / chunks / nodes) that is:

  * Burst‑friendly
  * Read‑only at runtime
  * Defined via **ScriptableObjects** (authored assets)
  * Loadable / unloadable per region via **Async C# Tasks**

* Support **dynamic traversal constraints** (doors, one‑way traversal, blocking) that:

  * Affect pathfinding
  * Change frequently
  * Are safe for parallel access

* Support **live entity occupancy** for:

  * NPC avoidance
  * Gameplay rules
  * Interest management
  * But **NOT required by pathfinding**

* Allow **client and server to diverge** in implementation strategy:

  * Client: rebuild runtime indices every frame
  * Server: incremental updates, scalable

---

## 2. Terminology & Spatial Hierarchy

### Grid & Lattice Model
* **Lattice Type**: Navigation occurs on the **vertices of an isometric triangular / hex-like lattice**.
  * **Isometric View**: The view is rendering-only; the simulation uses an integer grid with staggered staggering logic.
  * **Z-Spacing**: Rows are compressed by `sqrt(3)/2` relative to orthogonal grid.
* **Movement**:
  * **12 Neighbors**: 6 primary (edge-connected) and 6 secondary (composite turns).
  * Nodes have **6 first-order neighbors** (Hexagonal connectivity).

### Hierarchy Definitions

* **Region**

  * Sparse in the world
  * Size: `32 x 4 x 32` chunks

* **Chunk**

  * Sparse within a region
  * Size: `16 x 16` nodes (X,Z)

* **Node**

  * Dense within a chunk
  * Discrete traversal unit

* **NodeId**

  * A packed identifier combining:

    * `regionId`
    * `chunkMorton` (encodes chunk X,Y,Z)
    * `nodeIndex` (0..255, local X/Z)

NodeId is deterministic and can always be resolved back into:

* region
* chunk
* local node (x,z)

---

## 3. WorldGraph (Navigation Database)

### Purpose

The **WorldGraph** represents the *static navigation topology* and region lifecycle. It is:

* Read-mostly
* Safe for Burst jobs
* The only owner of navigation blobs

WorldGraph stores *what navigation data exists and where it lives*.

### Stored Data

WorldGraph contains:

* `RegionIdByKey`

  * Maps region keys (int3 or morton) → `regionId`

* `RegionEntry[]`

  * One per regionId
  * Fields:

    * Load state (NotLoaded / Loading / Loaded / Unloading)
    * `BlobAssetReference<Region>` (static nav data)
    * Pin / lock info for safe unload
    * Runtime base offsets/handles (see NavRuntime)

* Region pin / lock words

  * Atomic words guarding region unload
  * Prevent disposal while jobs are reading

### What WorldGraph Does NOT Contain

* Occupancy counts
* Door / dynamic exit states (those live in NavRuntime)
* Entity lists (those live in OccupancyRuntime)
* Interest policy (that lives in RegionInterestSystem)

WorldGraph is a **nav database**, not gameplay state.

---

## 4. WorldGraphSystem (Region Loading/Unloading + Structural Ownership)

### Purpose

The **WorldGraphSystem** is the *execution layer* for region streaming. It is responsible for:

* Orchestrating the **Async Load Pipeline** (ScriptableObject → Blob).
* Mapping requests to asset references (via a startup HashMap of weak references).
* Performing **all structural changes** required to support those regions in runtime singletons.
* Owning the authoritative transitions of `RegionEntry.State`.

This system bridges the gap between ECS (Main Thread) and C# Async Tasks (Background).

### Inputs

* Load/unload requests from RegionInterestSystem:

  * `RequestLoad(regionKey)`
  * `RequestUnload(regionKey)`

* Incoming "Blob Ready" queue (from background tasks).

### Outputs

* Updates to WorldGraph:

  * Region entries created (regionKey → regionId)
  * Region state transitions (NotLoaded ↔ Loaded)
  * Blob creation and disposal

* Feedback to RegionInterestSystem:

  * `UnloadDeferred(regionKey, reason)` (e.g. pinned, locked, or mid-load)

* Updates to NavRuntime:

  * Allocation of per-region runtime slices (node runtime indices)
  * Structural mapping additions for nodes that become available

* Optional updates to OccupancyRuntime:

  * Creation/initialization of per-region occupancy pages
  * Clearing/resetting pages on unload (policy-dependent)

### Asset Registry & Lookup

* **Source Data**: `ScriptableObject` assets holding raw navigation rules/data.
* **Registry**: A `HashMap` created at startup from a baked array of **Keyed Weak References**.
* **Lookup**: `RequestLoad(regionKey)` → Look up `WeakReference` in HashMap -> Start Async Load.

### Async Load Flow (Loading)

The loading process decouples asset I/O and conversion from the main game loop using C# Async/Await.

1. **Request Phase (Main Thread)**
   * System picks up `RequestLoad` for a region.
   * Checks for existing `RegionEntry`. If new, creates one with `State = requested`.
   * Adds to "Loading/Transient" list to prevent premature unload.
   * Looks up the corresponding Asset Reference.
   * **Initiates Async Task** passing the reference and region key.

2. **Async Task Phase (Background/Thread Pool)**
   * **Load Asset**: Resolves the weak reference (loading from disk/memory).
   * **Batched Blob Build**: 
     * Multiple pending regions can be batched into a single builder task.
     * Converts ScriptableObject data into `BlobBuilder` structures.
     * *Note: Memory safety must be managed carefully here (Allocators).*
   * **Result**: Produces a constructed `BlobAssetReference<Region>` (or raw byte buffer ready to finalize).
   * **Enqueue**: Pushes the result object (RegionKey, BlobRef) into a thread-safe "BlobReady" queue for the main thread.

3. **Commit Phase (Main Thread System Update)**
   * **Drain BlobReady Queue**:
     * Takes completed Blobs.
     * Updates `RegionEntry`: sets `NavBlob`, pins/locks, changes State → `Loaded`.
     * Removes from "Loading/Transient" list.
   * **Structural Updates**:
     * Updates `NavRuntime` to allocate indices for the newly loaded nodes.

### Unload Flow (Disposal)

Unloading is strictly Main Thread logic to ensure safety against job access.

1. **Request Phase (Main Thread)**
   * System picks up `RequestUnload` for a region.
   * Checks `RegionEntry.State`.

2. **Safety Check Phase**
   * **Transient Check**: Is the region currently in the "Async Load Flow"?
     * *Action*: Defer unload. The async task must finish first. 
   * **Pin/Lock Acquisition** (utilizing **Closeable Reader Lock** technique):
     * Atomically set the exclusive lock bit on the Pin Word (closing the gate to new readers).
     * **Drain Check**: 
       * If `Pin Count > 0`: Active readers exist. Region is now "Draining". Unload is unsafe this frame.
       * *Action*: Defer unload (feedback `UnloadDeferred`).
     * **Safety**: If `Pin Count == 0`, proceed to disposal.

2.5 **Mark Object as unload**

3. **Disposal Phase (If Safe)**
   * **Dispose Blob**: Frees unmanaged memory.
   * **Reset RegionEntry**: Sets state to `NotLoaded` (or `NeverExisted`).
   * **Notify**: Optional structural update to clear NavRuntime/OccupancyRuntime pages.

### Structural Change Rule

**All structural changes to singleton components occur here** at controlled sync points (Commit Phase).

Structural changes include:

* Growing `RegionEntry` storage and region id maps
* Allocating runtime slices in NavRuntime
* Creating/clearing per-region runtime containers (if paged)

Other systems may:

* enqueue requests
* read stable data
* perform atomic updates within pre-allocated runtime slices

But they do not resize/rehash/allocate shared singleton containers.

### Unload Deferral Feedback

If the system cannot unload because a region is locked/pinned:

* It can emit `UnloadDeferred(regionKey, reason)`
* RegionInterestSystem may extend its thrash delay and retry later

This provides a final safety gate while keeping policy decisions outside the loader.

---

## 5. Static Navigation Blob Data

### Stored Per Node (in blob)

* Local node index (x,z)
* Static traversal mask:

  * **12 movement directions** (6 primary hex-neighbors + 6 secondary composite turns)
  * Each direction has metadata bits for verticality.
  * Stored as a **64‑bit (ulong) walk mask**:
    * **12 bits**: Direction "Exists" / Passable flags.
    * **24 bits**: Verticality (2 bits per direction).
      * `00` = Same Level
      * `01` = Step Up (+1 Y)
      * `10` = Step Down (-1 Y)
    * **28 bits**: Reserved.

### Interpretation Rules

**Lattice Topology**: Staggered Grid (Isometric Hex-like).
* **Z-Spacing**: Nodes are spaced by `0.866 * NodeSize` in Z.
* **X-Stagger**: Every odd Z-row is shifted by `0.5 * NodeSize` in X.

**Neighbor Lookup**:
To find neighbor `(dx, dz)`:
1. Determine row parity of current node (Even/Odd Z).
2. Select appropriate offset table.
   * Even Row Neighbors: `(0,1), (-1,1), (-1,0), (1,0), (-1,-1), (0,-1)` etc.
   * Odd Row Neighbors:  `(0,1), (1,1), (-1,0), (1,0), (0,-1), (1,-1)` etc.
3. Apply `dy` from verticality bits.

Destination node is computed by:
1. `nx, nz` = Apply offset.
2. Wrap across chunk boundaries (standard arithmetic modulo 16, carrying stagger logic across chunks if necessary - note: chunks align on row boundaries so stagger logic is local).
3. Apply vertical delta `dChunkY`.

This allows:
* Vertical exits at chunk tops
* 12-way movement on a hexagonal lattice
* Isometric/Staggered spatial layout

No explicit edge tables are required as long as adjacency is regular.

---

## 5. NavRuntime (Dynamic Traversal State)

### Purpose

NavRuntime stores **dynamic traversal constraints** that affect pathfinding but cannot live in blobs.

### Stored Data

* `NodeId → OccId` (structural map)

  * Grows only
  * New nodes added at sync points

* `OccAtomicWord[occId]`

  * Stable array / list
  * Atomic 32‑bit word per node
  * Packed fields:

    * Occupancy count (lower bits)
    * Dynamic exit block mask (8 bits)
    * Optional lock bit

### Responsibilities

* Doors opening / closing
* One‑way traversal changes
* Temporary blocking

### Pathfinding Usage

Pathfinding:

* Reads static walk mask from blob
* Reads dynamic block mask from NavRuntime
* Effective mask = `static & ~dynamic`

NavRuntime is **read‑only for pathfinding jobs**.

---

## 6. OccupancyRuntime (Live Entity Presence)

### Purpose

Tracks **entities occupying nodes** for:

* NPC avoidance
* Gameplay rules
* Interaction logic

Pathfinding does **not** depend on this data.

### Client Strategy

* Rebuild every frame
* Use `NativeParallelMultiHashMap<NodeId, Entity>`
* Netcode ghost partitioning limits scale

### Server Strategy

* Incremental updates
* Node‑based linked lists or staged updates
* Optimized for large counts

### Stored Data (conceptual)

* Dynamic occupancy index
* Optional static / semi‑static occupants
* Entity → Node mapping (server)

---

## 7. RegionInterestSystem (Interest Management)

### Purpose

The **RegionInterestSystem** determines *which regions should be loaded or unloaded* based on player and entity activity. It is the **policy layer** that drives WorldGraph loading, but it does **not** perform loading itself.

It is designed to be:

* Largely asynchronous (jobs where possible)
* Decoupled from WorldGraph internals
* The sole authority on *when* regions are requested or released

### Responsibilities

The RegionInterestSystem:

* Observes entity movement (players, NPCs, cameras, AI focus points)
* Determines which regions are **in interest** and which are **out of interest**
* Applies hysteresis / delay to prevent load–unload thrashing
* Issues **load requests** and **unload requests** to the WorldGraphSystem
* Receives **deferral feedback** when unload is blocked (e.g., region still pinned)

### What It Does NOT Do

* It does **not** load or unload blobs
* It does **not** mutate WorldGraph region state directly
* It does **not** dispose navigation data

### Interaction with WorldGraph

The interaction is queue-based:

* RegionInterestSystem → WorldGraphSystem:

  * `RequestLoad(regionKey)`
  * `RequestUnload(regionKey)`

* WorldGraphSystem → RegionInterestSystem (feedback):

  * `UnloadDeferred(regionKey, reason)`

Reasons for deferral may include:

* Region is pinned by active jobs
* Region is currently locked for unload
* Region is mid-load

Upon receiving a deferral, the RegionInterestSystem may:

* Extend the unload delay
* Cancel unload if interest has returned
* Retry later

### Relationship to Pins / Locks

The RegionInterestSystem **does not manage pins directly**.

Pins are acquired by:

* Pathfinding jobs
* Navigation queries
* Any system that needs to read region blobs

The RegionInterestSystem only observes the *effects* of pins via deferral feedback. This keeps interest policy separate from low-level safety mechanisms.

### Client vs Server Behavior

* **Client**:

  * Interest is driven by local player(s)
  * Region loads prioritize visual relevance
  * Unloads may be aggressive

* **Server**:

  * Interest is driven by players + AI
  * Regions may remain loaded longer
  * Unloads are conservative to avoid churn

---

## 8. Separation of Responsibilities

| System               | Owns                   | Writes              | Reads                   |
| -------------------- | ---------------------- | ------------------- | ----------------------- |
| WorldGraph           | Regions, nav blobs     | Load/unload         | Pathfinding, NavRuntime |
| RegionInterestSystem | Load policy            | Requests            | Movement, feedback      |
| NavRuntime           | Doors, traversal state | Gameplay systems    | Pathfinding             |
| OccupancyRuntime     | Entity presence        | Movement / gameplay | NPC logic               |
| Pathfinding          | Routes                 | —                   | WorldGraph + NavRuntime |

---

## 8. Sync Points & Concurrency Rules

### Sync Points (once per frame or explicit)

* Adding new region entries
* Adding new node → occId mappings
* Growing runtime arrays

### Parallel‑Safe Operations

* Atomic updates to `OccAtomicWord`
* Read‑only access to blobs
* Read‑only access to runtime arrays

### Forbidden Patterns

* Mutating values inside a `NativeParallelHashMap` from multiple threads
* Disposing blobs without pin / lock checks

---

## 9. Client vs Server Differences

| Aspect          | Client              | Server            |
| --------------- | ------------------- | ----------------- |
| Region loading  | Interest‑based      | Often broader     |
| Occupancy index | Rebuilt each frame  | Incremental       |
| Scale           | Limited by interest | Potentially large |
| Authority       | Predictive          | Authoritative     |

Design intentionally allows divergence while sharing core concepts.

---

## 10. Open / Future Considerations

* Edge-specific costs (slope, terrain)
* Exceptional adjacency (portals, elevators)
* Region paging strategies on server
* Snapshotting NavRuntime for debugging

---

# Appendix A: WorldGraph Structs (Draft)

This appendix defines the core data structures discussed so far. Names are suggestions; adjust to match coding conventions.

> Notes
>
> * Native containers are shown as fields for clarity. In Entities, these typically live on singleton components and are allocated/disposed by the owning system (WorldGraphSystem).
> * IDs (`regionId`, `nodeId`, `occId`) are intended to be stable handles.
> * When reuse of ids is introduced, add `Generation` and validate `(id, generation)` pairs.

---

## A1. Region State

```csharp
public enum RegionState : byte
{
    NeverExisted = 0,
    NotLoaded    = 1,
    RequestedLoad= 2,
    Loading      = 3,
    Loaded       = 4,
    RequestedUnload = 5,
    Unloading    = 6,
}
```

---

## A2. RegionEntry

Represents one region’s lifecycle, owning its static navigation blob, and pointing to runtime slices.

```csharp
public struct RegionEntry
{
    public RegionState State;

    // Static nav data (immutable blob): only valid when State == Loaded.
    public BlobAssetReference<Region> NavBlob;

    // Pin/lock index for region-level safety (optional). If per-region pins are used,
    // this index points into a stable array/list of atomic pin words.
    public int RegionPinId;

    // Runtime slice addressing (NavRuntime / OccupancyRuntime). These are offsets into
    // flat arrays sized/allocated by WorldGraphSystem at structural sync points.
    public int BaseNodeRuntimeIndex; // base offset for nodes in this region
    public int NodeRuntimeCount;     // number of node runtime slots for this region

    // Optional for id reuse / debug
    public uint Generation;
}
```

---

## A3. WorldGraph (Singleton)

Stores region addressing and region entries. This is the navigation database.

```csharp
public struct WorldGraph : IComponentData
{
    // Region addressing
    public NativeParallelHashMap<int3, int> RegionIdByKey; // regionKey -> regionId

    // Region registry
    public NativeList<RegionEntry> RegionsById;            // regionId -> RegionEntry

    // Region-level pin words (optional) (lock bit + pin count). Stable storage.
    public NativeList<int> RegionPinWords;

    // Optional: free-list for region id reuse
    public NativeQueue<int> FreeRegionIds;
}
```

---

## A4. WorldGraph Requests + Feedback (Singleton)

Queues used to communicate with WorldGraphSystem.

```csharp
public struct WorldGraphRequests : IComponentData
{
    public NativeQueue<int3> RequestLoadQ;   // regionKey
    public NativeQueue<int3> RequestUnloadQ; // regionKey

    // Optional dedupe sets
    public NativeParallelHashSet<int3> PendingLoad;
    public NativeParallelHashSet<int3> PendingUnload;
}

public enum UnloadDeferReason : byte
{
    StillPinned = 1,
    StillLoading = 2,
    Locked = 3,
}

public struct WorldGraphFeedback : IComponentData
{
    public NativeQueue<int3> UnloadDeferredQ; // regionKey (or regionId)
    // Optionally include reason in a parallel queue of the same length:
    public NativeQueue<UnloadDeferReason> UnloadDeferredReasonQ;
}
```

---

## A5. Region Interest Inputs (Singleton)

Movement → Interest notifications are intentionally separated from WorldGraph.

```csharp
public struct MovementRegionNotifications : IComponentData
{
    public NativeQueue<EntityRegionChange> Q;
}

public struct EntityRegionChange
{
    public Entity Entity;
    public int3 OldRegionKey;
    public int3 NewRegionKey;
}
```

---

## A6. NodeId and Node Runtime Indexing

### NodeId

A packed identifier combining region + chunk + local node. Recommended as `ulong`.

```csharp
// Example packing (illustrative)
// [ regionId: 24 bits ][ chunkMorton: 12 bits ][ nodeIndex: 8 bits ][ reserved: 20 bits ]
public readonly struct NodeId
{
    public readonly ulong Value;
    public NodeId(ulong v) => Value = v;
}
```

### NodeRuntimeIndex

Pathfinding and traversal systems should prefer `nodeRuntimeIndex` for fast array access.

* `nodeRuntimeIndex = regionEntry.BaseNodeRuntimeIndex + localNodeLinearIndex`
* `localNodeLinearIndex` is derived from chunk+node within that region’s loaded chunk set.

(Exact mapping depends on how sparse chunks are represented. The simplest early approach is to allocate per-loaded-chunk blocks of 256 nodes and map `(chunkIdWithinRegion, nodeIndex)` → `localNodeLinearIndex`.)

---

## A7. NavRuntime (Singleton)

Dynamic traversal state that pathfinding must consult (doors, dynamic exit blocks, etc.).

```csharp
public struct NavRuntime : IComponentData
{
    // Structural mapping (grows): nodeId -> occId
    public NativeParallelHashMap<ulong, int> NodeToOccId;

    // Runtime per-node atomic word (stable). Indexed by occId.
    // Packed fields (suggestion):
    // - bits 0..15  : occupancy count (optional if needed for movement rules)
    // - bits 16..23 : dynamic blocked directions mask (8 bits)
    // - bit 31      : lock bit (optional)
    public NativeList<int> OccAtomicWord;

    // Static per-node info not stored in blobs (optional). If static walkmask is in blob,
    // you may not need this.
    public NativeList<uint> Reserved;

    // Structural add queue for newly referenced nodes
    public NativeQueue<ulong> NewNodeQ;
}
```

> If occupancy counts are NOT used by pathfinding, they can be moved to OccupancyRuntime instead, and NavRuntime’s atomic word can hold only dynamic door masks.

---

## A8. OccupancyRuntime (Singleton)

Live entity presence for avoidance/interaction. Not required by pathfinding.

```csharp
public struct OccupancyRuntime : IComponentData
{
    // Client: rebuilt each frame
    public NativeParallelMultiHashMap<ulong, Entity> DynamicOccupants;

    // Region-load-time static occupants (optional)
    public NativeParallelMultiHashMap<ulong, Entity> StaticOccupants;

    // Optional: per-node occupancy counts (atomic) for NPC avoidance if needed.
    // If you keep counts here, use stable storage indexed by nodeRuntimeIndex or occId.
    public NativeList<int> NodeOccCounts;
}
```

---

## A9. Static Navigation Blob Types (for reference)

```csharp
public struct Region
{
    public BlobArray<Chunk> Chunks;
}

public struct Chunk
{
    public ushort MortonCode; //Origin (world position) can be calculated be de-composing xyz chunk id from chunk. And multiplication from chunk size.
    public int LayerMask;
    public BlobArray<Node> Nodes;
}

public struct Node
{
    public ushort MortonCode;
    public float y;

    // Suggested: 32-bit static walk mask (8 directions × 3 vertical bits)
    public uint WalkMask32;
}
```

---

# Appendix B: Traversal Mask Interpretation (Draft)

This appendix summarizes the rule for computing neighbor chunk/node from `WalkMask32`.

* Directions: 8 compass directions (N,NW,W,SW,S,SE,E,NE)
* Each direction has 3 bits: `same`, `up`, `down`.
* For each set bit, pathfinding expands a neighbor edge.

Neighbor computation uses:

1. Current chunk coord decoded from chunk morton.
2. Current local node `(x,z)` within 16×16.
3. `(dx,dz)` implied by direction.
4. Wrap `(x+dx,z+dz)` across [0..15] to compute `(dChunkX,dChunkZ)`.
5. Apply `dChunkY` based on selected vertical bit.

Result:

* Destination chunk coord `(cx+dChunkX, cy+dChunkY, cz+dChunkZ)`
* Destination local node `(nxWrapped, nzWrapped)`

This supports:

* Exits into chunk above for top nodes
* Border exits that change vertical layer
* Diagonal chunk transitions
