# World Graph Baking & Storage Strategy

## 1. Goal
Create a workflow to manually bake world geometry into static navigation data, stored efficiently for runtime streaming.

## 2. Storage Format: ScriptableObject vs. Binary
We will use **ScriptableObjects** (`RegionGraphData`) as the storage container.

### Why not raw Binaries?
*   **Asset Management**: Unity handles file paths, GUIDs, and movement of ScriptableObjects automatically. Raw files require manual IO handling.
*   **Safety**: Storing raw `BlobAsset` bytes is risky. If the `Blob` schema changes (schema evolution), raw binary dumps become invalid and cause hard crashes.
*   **Workflow**: ScriptableObjects can be inspected in the Inspector.

### Why not SubScenes/Entities?
*   We need granular **streaming** control (`RequestLoad(regionKey)`). SubScenes are powerful but tie us to the Entity conversion pipeline, which can be overkill for static data we just want to "load and read".

### The `RegionGraphData` Asset
This asset acts as an **intermediate format**. 
*   **Disk representation**: Flat Arrays (`float[] Heights`, `ulong[] Connectivity`).
*   **Runtime**: Converted to `BlobAssetReference<Region>` via `BlobBuilder` (Burst-compiled for speed) upon loading.
*   *Optimization*: If loading is too slow, we can cache the raw `byte[]` in the SO later, but we start with robust arrays.

## 3. Usage Workflow

1.  **Scene Setup**: Designer places level geometry.
2.  **Visualization**:
    *   Open **World Graph Overlay**.
    *   Set **Geometry Layer** (Ground) and **Obstacle Layer** (Walls).
    *   Visualize the grid to ensure alignment.
3.  **Baking**:
    *   In the Overlay, select a target Region (or "Bake Current Region").
    *   Tool performs the scanning (Jobified Raycasts + Overlaps).
    *   Tool generates a `RegionGraphData` asset at `Assets/Data/Regions/Region_{x}_{y}_{z}.asset`.
4.  **Runtime Loading**:
    *   `WorldGraphSystem` requests the asset.
    *   `OnLoad`: Asset data is blitted into a Blob.
    *   Blob is added to the Graph.

## 4. Baking Implementation Plan

### Step A: The Asset Data Structure
Create `RegionGraphData.cs`:
```csharp
public class RegionGraphData : ScriptableObject {
    public int3 RegionCoordinate;
    // Serialized as flat arrays to avoid object overhead
    public float[] NodeHeights; 
    public ulong[] NodeFlags; 
}
```

### Step B: The Baker
Create `WorldGraphBakingService` (Static Editor Class):
*   Method `BakeRegion(int3 region, LayerMask geometry, LayerMask obstacles)`
*   Reuses the logic from `WorldGraphTool` (Scanning).
*   Saves the asset using `AssetDatabase`.

### Step C: The UI
*   Add "Baking Actions" foldout to the Debug Overlay.
*   Button: "Bake Current Region".

## 5. Performance Notes
*   **Disk Size**: A full 32x4x32 chunk region (524k nodes) using `float`+`ulong` is ~6MB per region uncompressed.
*   **Compression**: Unity compresses ScriptableObjects in build.
*   **Memory**: The `RegionGraphData` object is transient. We load it, build the blob, then `Resources.UnloadAsset` it immediately.
