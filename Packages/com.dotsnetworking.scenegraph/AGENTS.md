# AGENTS.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Package Scope

This is `com.dotsnetworking.scenegraph`, a Unity package implementing scene graph and navigation for DOTS Networking. Work only within `Packages/com.dotsnetworking.scenegraph` unless explicitly asked to modify other areas. Keep package name aligned with folder structure.

## Unity Environment

- **Unity**: 6000.3.2f1 (Unity 6)
- **DOTS/ECS**: 1.0+
- **Dependencies**: Unity.Entities, Unity.Collections, Unity.Burst, Unity.Mathematics, BovineLabs.Core
- **Unsafe code**: Enabled (required for blob assets and morton encoding)
- **This is a Unity package**, not a standalone project - build/test via Unity Editor

## Common Commands

Since this is a Unity package, there are **no CLI build/test commands**. Development happens entirely in Unity Editor:

- Open parent project in Unity Editor (DotsNetworking.sln)
- Use Unity's Test Runner window for any tests (currently no active tests exist)
- Assembly definitions control compilation automatically
- No linting/formatting commands configured - follow existing code style

## Architecture Overview

### Spatial Hierarchy (3 Levels)

**Section** (32×4×32 chunks)
- Sparse in world space
- Identified by: Scene GUID + Section ID (30-bit morton-encoded int3, ±512 range per axis)
- Maps to Unity's SceneSection for streaming
- Contains: sparse BlobArray of chunks + 64KB morton lookup table (O(1) chunk access)

**Chunk** (16×16 nodes in XZ)
- Sparse within section
- Identified by: 15-bit morton code (5 bits per axis, 0-31 range)
- Y layer implicit in section hierarchy

**Node** (discrete traversal unit)
- Dense within chunk (256 nodes)
- Identified by: 8-bit morton code (4 bits per axis, 0-15 XZ range)
- Stores: Y height (float) + MovementFlags (ulong exit mask)

### Critical: Staggered Lattice Topology

Navigation uses **staggered isometric hex-like lattice**, NOT regular grid:

- Node spacing: X = 0.5, Z = 0.5 × √3/2 ≈ 0.433
- **Stagger**: Every odd Z-row shifts +0.25 in X (half node)
- Movement: 12-way (6 primary hex neighbors + 6 secondary composite)
- Verticality: Each direction encodes up/down layer transitions (2 bits)

**Always use `SceneGraphMath.WorldToGraph()` for world position conversions.** Do NOT treat nodes as regular grid cells - neighbor calculations depend on row parity.

### Key Files

**NavigationBlobs.cs** - Immutable navigation data:
```
Section (root blob)
  ├─ BlobArray<Chunk> (sparse)
  └─ BlobArray<short> ChunkLookup (32768 entries, morton → chunk index)
     
Chunk
  ├─ ushort MortonCode (15-bit, position within section)
  └─ BlobArray<Node> (256 dense, indexed by local morton)
     
Node
  ├─ float Y (height)
  └─ MovementFlags ExitMask (12 directions + verticality + unreachable)
```

**SceneGraphTypes.cs** - Runtime addressing:
- `SectionAddress`: SceneGUID + SectionId
- `ChunkAddress`: SceneGUID + SectionId + ChunkMorton  
- `NodeAddress`: SceneGUID + SectionId + ChunkMorton + NodeIndex
- `SectionEntry`: lifecycle state, blob ref, pins, runtime indices
- `SectionState`: NotLoaded → Loading → Loaded → Unloading
- `MovementFlags`: 64-bit ulong (bits 0-11: existence, 12-35: verticality, 63: unreachable)

**SceneGraphConstants.cs** - Spatial dimensions, morton limits, movement thresholds

**SceneGraphMath.cs** - Coordinate system:
- `WorldToGraph()`: float3 → section/chunk/node (handles stagger)
- `GraphToWorldBase()`: inverse transform to lattice point
- `PackSectionId()` / `UnpackSectionId()`: int3 ↔ morton uint (10 bits/axis, biased)
- `EncodeChunkToMorton()` / `DecodeMortonToChunk()`: int3 ↔ 15-bit morton
- `EncodeNodeToMorton()`: int2 → 8-bit morton

**Morton.cs** - Low-level spatial encoding (2D/3D, 32/64-bit variants)

**NavigationAssetProvider.cs** - Blob loading:
- Loads `BlobAssetHandler` via `Resources.Load` / `LoadAsync`
- Returns the handler (call `TryGetBlob` for the ref)
- Optional ref counting and unload cleanup

### Design Philosophy (from parent design doc)

Follows **World Graph & Navigation Runtime Design**:

- **Static navigation** (blobs) vs **dynamic state** (NavRuntime) separation
- **Async loading** with pin/lock safety (closeable reader lock pattern)
- **Client/Server divergence**: client rebuilds per-frame, server incremental
- **Occupancy** (OccupancyRuntime) separate from pathfinding
- **Interest management** (RegionInterestSystem) drives loading, doesn't own blobs
- **Structural changes** at sync points only (WorldGraphSystem)

**Key insight**: Pathfinding reads static walk mask (blob) + dynamic block mask (NavRuntime). Occupancy is gameplay-only, NOT pathfinding input.

## Development Patterns

### Spatial Math
- Use `SceneGraphMath` for all world ↔ graph conversions
- Use `Unity.Mathematics` types (int3, float3) over complex types
- Morton encoding is spatial addressing (enables O(1) chunk lookup), not just optimization

### Blob Assets
- Immutable once created
- Use BlobBuilder for construction
- Dispose only after pin/lock checks
- Prefer BlobArray over nested containers

### ECS Patterns
- Singletons for shared state (WorldGraph, NavRuntime, OccupancyRuntime)
- `NativeParallelHashMap` for structural mappings (grow at sync points)
- `NativeList` for stable indexed storage
- Atomic ops for runtime state (door masks, occupancy)
- Jobs acquire pins before reading blobs

### Async Loading Workflow
- ScriptableObject → async task → blob conversion → main thread commit
- `NativeQueue` for cross-thread results
- State updates main-thread only
- Defer unload if pinned or mid-load

### Coordinate System Reference
```
World space:                 float3 (Unity coordinates)
Section key:                 int3 (can be negative, ±512 range)
Section ID:                  uint (30-bit morton with bias)
Chunk index (in section):    int3 (0-31 per axis)
Chunk morton:                ushort (15-bit)
Local node index:            int2 (0-15 XZ)
Node morton:                 byte (8-bit)
```

## Code Style

- Prefer simple DOTS/ECS patterns over clever abstractions
- ASCII text only
- Comments only when logic non-obvious (stagger math, pin/lock atomics)
- Follow existing naming: **Section** (not Region), **Node** (not Cell), **Exit/Movement** (not Edge)
- Package separation: SceneGraph ≠ WorldGraph (sibling package)

## Critical Gotchas

1. **Stagger logic**: Neighbor offsets depend on Z-row parity (even/odd). Use design doc offset tables.

2. **Negative modulo**: C# `%` returns negative. Explicitly normalize:
   ```csharp
   if (localX < 0) localX += ChunkSize;
   ```

3. **Morton limits**: 
   - Sections: 10 bits/axis (±512 range)
   - Chunks: 5 bits/axis (0-31 range)  
   - Nodes: 4 bits/axis (0-15 range)

4. **Verticality encoding**: MovementFlags encode BOTH direction existence AND vertical delta. Check both bits.

5. **Section 0 reserved**: Unity's scene section system reserves index 0. Map section IDs with +1 offset.

6. **Blob lifetime**: Never cache blob refs across structural changes. Re-read from SectionEntry.

7. **Asset reloads**: If a blob asset is reimported/unloaded, reacquire the blob via `NavigationAssetProvider`.

8. **No README**: This package has no README.md. Refer to parent design doc (`World Graph & Navigation Runtime Design.md`) for system-level architecture.
