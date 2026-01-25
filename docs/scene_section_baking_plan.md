# Scene Section Baking Plan (Region-Based)

## Goals
- Assign `SceneSection` directly in a custom baking system (not a Baker) so each entity ends up in a section derived from its region id.
- Ensure section index `0` is reserved and always present; all computed section indices must be shifted to start at `1`.
- Optionally attach section metadata to the section meta entity for debugging/streaming decisions.

## Key Docs Notes (Entities 6.5)
- `SceneSection` is a shared component on every entity and controls which section it belongs to.
- You **cannot** set `SceneSection` in a Baker; you must use a **custom baking system** to assign it.
- Section index `0` is always present (even if empty) and must load first.
- Section meta entities are created during resolve; use `SerializeUtility.GetSceneSectionEntity` during baking to attach metadata.

## Section Index Strategy
- Compute a stable region id for each entity (from world position or authoring data).
- Map region id → section index.
- Because section `0` is reserved, use:
  - `sectionIndex = regionMorton + 1`
- Keep indices non-consecutive if desired; Unity supports sparse section indices.

## Inputs & Dependencies
- Region id is derived from world graph rules:
  - Use `WorldGraphMath.WorldToGraph` to compute region key from position.
  - Pack region to a stable id (`PackRegionId`) or convert to a 3D morton code if desired.
- Scene GUID: obtained from baking context (e.g., `SceneSection.SceneGUID` or `SceneSystem.GetSceneGUID` usage in baking context).

## Plan
### 1) Authoring Data (optional)
- Add an authoring component that indicates the entity participates in region-based sectioning.
- Optional overrides:
  - Force region key
  - Force section index

### 2) Baking System: Region-Based SceneSection Assignment
- Create a baking system with `WorldSystemFilterFlags.BakingSystem`.
- Query entities that need custom sectioning (via a tag or authoring data).
- For each entity:
  1. Get world position (from `LocalToWorld` or authoring).
  2. Compute region key:
     - `WorldGraphMath.WorldToGraph(position, out regionKey, ...)`
  3. Convert region key to a stable index:
     - `regionMorton = Morton.EncodeMorton32((uint3)regionKey)`
     - or `regionId = PackRegionId(regionKey)` then reduce to int for section.
  4. Compute section index:
     - `sectionIndex = regionMorton + 1` (reserve `0`).
  5. Assign `SceneSection` shared component directly in the baking system.

### 3) Section Meta Entity Metadata (optional)
- For each unique section index created, call:
  - `SerializeUtility.GetSceneSectionEntity(sectionIndex, EntityManager, ref query, true)`
- Add metadata components (e.g., region bounds, region id, or debug labels).

### 4) Validation
- Verify that:
  - Entities are placed in section > 0.
  - Section 0 still exists in the subscene header.
  - Cross-section entity references obey the rule: entities can reference same section or section 0 only.

## Edge Cases
- Negative region coordinates: ensure morton/packing handles sign or normalize region indices before encoding.
- Very large region indices: confirm resulting section index fits in `int` and is deterministic.
- Open subscenes in Editor: all entities appear in section 0 until the subscene is closed.

## Suggested Implementation Skeleton (Baking System)
- `WorldGraphSceneSectionBakingSystem` (baking-only)
  - `OnCreate`: build queries for entities and section meta access.
  - `OnUpdate`:
    - iterate entities with a tag
    - compute section index from region id
    - set `SceneSection` shared component
    - add metadata to section meta entity if desired

## Deliverables
- New baking system file (Editor/ or Runtime/Authoring depending on use).
- Optional authoring component and baker to tag entities.
- Optional metadata component for section meta entities.

## Open Questions
- Should the region → section mapping use morton or packed region id?
- Should a fallback section be used for entities without valid region info?
- Do we need a debug UI to visualize section indices per region?
