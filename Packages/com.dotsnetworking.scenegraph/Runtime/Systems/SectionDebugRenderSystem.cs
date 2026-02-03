using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using DotsNetworking.SceneGraph.Utils;
using BovineLabs.Core.Camera;
using DotsNetworking.SceneGraph.Components;
using System;

namespace DotsNetworking.SceneGraph
{
    /// <summary>
    /// Debug rendering system that visualizes navigation nodes from SectionBlobComponent blob assets.
    /// Renders nodes as small crosses and optionally shows movement connections.
    /// Uses frustum culling to only render visible chunks.
    /// Only runs in Editor and when Gizmos are enabled.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct SectionDebugRenderSystem : ISystem
    {
        // Debug rendering configuration
        private const float NodeCrossSize = 0.1f;
        private const bool DrawConnections = true;
        private const float ConnectionAlpha = 0.5f;
        private const bool DrawChunkBoundsOnly = true;

        private NativeList<SectionEntry> cachedSections;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SceneSectionRegistry>();
            cachedSections = new NativeList<SectionEntry>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (cachedSections.IsCreated)
                cachedSections.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Get camera frustum planes for culling
            var frustumPlanes = default(CameraFrustumPlanes);
            bool hasFrustum = false;

            foreach (var (planes, _) in SystemAPI.Query<RefRO<CameraFrustumPlanes>, RefRO<CameraMain>>())
            {
                frustumPlanes = planes.ValueRO;
                hasFrustum = !frustumPlanes.IsDefault;
                break;
            }

            cachedSections.Clear();
            var registry = SystemAPI.GetSingleton<SceneSectionRegistry>();
            var manifest = SceneGraphManifest.I;
            if (manifest == null || !registry.Registry.IsCreated)
                return;

            if (cachedSections.Capacity < manifest.SectionCount)
                cachedSections.Capacity = manifest.SectionCount;

            foreach (var subscene in manifest.Subscenes)
            {
                foreach (var section in subscene.Sections)
                {
                    var address = section.Address;
                    using var blobHandle = registry.Registry.AcquireRead<BlobAssetReference<Section>>(address);
                    if (!blobHandle.IsAccessible)
                        continue;

                    var blobRef = blobHandle.Value;
                    if (!blobRef.IsCreated)
                        continue;

                    cachedSections.Add(new SectionEntry
                    {
                        Address = address,
                        BlobRef = blobRef,
                    });
                }
            }
            if (cachedSections.Length == 0)
                return;

            var stream = new NativeStream(cachedSections.Length, Allocator.TempJob);
            var job = new ChunkCullingJob
            {
                Sections = cachedSections.AsArray(),
                FrustumPlanes = frustumPlanes,
                UseFrustumCulling = hasFrustum,
                StreamWriter = stream.AsWriter(),
            };

            var handle = job.Schedule(cachedSections.Length, 1, state.Dependency);
            handle.Complete();
            state.Dependency = handle;

            var reader = stream.AsReader();
            for (int i = 0; i < cachedSections.Length; i++)
            {
                reader.BeginForEachIndex(i);
                while (reader.RemainingItemCount > 0)
                {
                    var item = reader.Read<ChunkDrawItem>();
                    var sectionEntry = cachedSections[item.SectionIndex];
                    if (!sectionEntry.BlobRef.IsCreated)
                        continue;

                    ref var section = ref sectionEntry.BlobRef.Value;
                    DrawChunk(ref section, item.SectionKey, item.ChunkIndex, item.ChunkIdx);
                }
                reader.EndForEachIndex();
            }

            stream.Dispose();
        }

        [BurstCompile]
        private struct ChunkCullingJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SectionEntry> Sections;
            [ReadOnly] public CameraFrustumPlanes FrustumPlanes;
            public bool UseFrustumCulling;
            public NativeStream.Writer StreamWriter;

            public void Execute(int index)
            {
                var sectionEntry = Sections[index];
                if (!sectionEntry.BlobRef.IsCreated)
                    return;

                var sectionKey = SceneGraphMath.UnpackSectionId(sectionEntry.Address.SectionId);

                if (UseFrustumCulling)
                {
                    var sectionAABB = GetSectionAABB(sectionKey);
                    if (!FrustumPlanes.AnyIntersect(sectionAABB))
                        return;
                }

                ref var section = ref sectionEntry.BlobRef.Value;
                StreamWriter.BeginForEachIndex(index);
                for (int i = 0; i < section.Chunks.Length; i++)
                {
                    ref var chunk = ref section.Chunks[i];
                    var chunkIdx = SceneGraphMath.DecodeMortonToChunk(chunk.MortonCode);

                    if (UseFrustumCulling)
                    {
                        var chunkAABB = GetChunkAABB(sectionKey, chunkIdx);
                        if (!FrustumPlanes.AnyIntersect(chunkAABB))
                            continue;
                    }

                    StreamWriter.Write(new ChunkDrawItem
                    {
                        SectionIndex = index,
                        ChunkIndex = i,
                        SectionKey = sectionKey,
                        ChunkIdx = chunkIdx,
                    });
                }
                StreamWriter.EndForEachIndex();
            }
        }

        private struct ChunkDrawItem
        {
            public int SectionIndex;
            public int ChunkIndex;
            public int3 SectionKey;
            public int3 ChunkIdx;
        }

        private struct SectionEntry
        {
            public BlobAssetReference<Section> BlobRef;
            public SectionAddress Address;
        }

        private static void DrawChunk(ref Section section, int3 sectionKey, int chunkIndex, int3 chunkIdx)
        {
            if (DrawChunkBoundsOnly)
            {
                var chunkAabb = GetChunkAABB(sectionKey, chunkIdx);
                DrawAabb(chunkAabb, Color.white);
                return;
            }

            ref var chunk = ref section.Chunks[chunkIndex];
            return;
            // Iterate through all 256 nodes in the chunk
            for (int nodeIdx = 0; nodeIdx < chunk.Nodes.Length; nodeIdx++)
            {
                ref var node = ref chunk.Nodes[nodeIdx];
                var nodeLocal = SceneGraphMath.DecodeMortonToNode((byte)nodeIdx);

                // Calculate world position of this node
                var basePos = SceneGraphMath.GraphToWorldBase(sectionKey, chunkIdx, nodeLocal);
                var worldPos = new float3(basePos.x, node.Y, basePos.z);

                // Determine node color based on flags
                Color nodeColor = GetNodeColor(node.ExitMask);

                // Draw node as a small cross
                //DrawNodeCross(worldPos, nodeColor);

                // Optionally draw movement connections
                if (DrawConnections)
                {
                    //DrawNodeConnections(worldPos, node.ExitMask, sectionKey, chunkIdx, nodeLocal, ref section);
                }
            }
        }

        /// <summary>
        /// Calculates the AABB for a chunk in world space.
        /// </summary>
        private static AABB GetChunkAABB(int3 sectionKey, int3 chunkIdx)
        {
            // Match SceneGraphTool.DrawChunkBounds for consistent sizing
            float3 chunkBase = new float3(
                sectionKey.x * SceneGraphConstants.SectionSizeX + chunkIdx.x * SceneGraphConstants.ChunkSizeX,
                sectionKey.y * SceneGraphConstants.SectionSizeY + chunkIdx.y * SceneGraphConstants.ChunkHeight,
                sectionKey.z * SceneGraphConstants.SectionSizeZ + chunkIdx.z * SceneGraphConstants.ChunkSizeZ
            );

            float3 size = new float3(
                SceneGraphConstants.ChunkSizeX,
                SceneGraphConstants.ChunkHeight,
                SceneGraphConstants.ChunkSizeZ
            );

            var center = chunkBase + size * 0.5f;
            var extents = size * 0.5f;

            return new AABB()
            {
                Center = center,
                Extents = extents
            };
        }

        /// <summary>
        /// Calculates the AABB for a section in world space.
        /// </summary>
        private static AABB GetSectionAABB(int3 sectionKey)
        {
            // World position of the section's origin (node 0,0 in chunk 0,0,0)
            var sectionOrigin = SceneGraphMath.GraphToWorldBase(sectionKey, int3.zero, int2.zero);

            const float sectionWidth = SceneGraphConstants.SectionSizeX + SceneGraphConstants.NodeSize * 0.5f;
            const float sectionDepth = SceneGraphConstants.SectionSizeZ;
            const float sectionHeight = SceneGraphConstants.SectionSizeY;

            const float halfSectionWidth = sectionWidth * 0.5f;
            const float halfSectionDepth = sectionDepth * 0.5f;
            const float halfSectionHeight = sectionHeight * 0.5f;

            float3 extents = new float3(halfSectionWidth, halfSectionHeight, halfSectionDepth);


            var center = new float3(
                sectionOrigin.x,
                sectionOrigin.y,
                sectionOrigin.z
            ) + extents;

            return new AABB
            {
                Center = center,
                Extents = extents
            };
        }

        private static void DrawAabb(AABB aabb, Color color)
        {
            var c = aabb.Center;
            var e = aabb.Extents;

            var v0 = new Vector3(c.x - e.x, c.y - e.y, c.z - e.z);
            var v1 = new Vector3(c.x + e.x, c.y - e.y, c.z - e.z);
            var v2 = new Vector3(c.x + e.x, c.y - e.y, c.z + e.z);
            var v3 = new Vector3(c.x - e.x, c.y - e.y, c.z + e.z);

            var v4 = new Vector3(c.x - e.x, c.y + e.y, c.z - e.z);
            var v5 = new Vector3(c.x + e.x, c.y + e.y, c.z - e.z);
            var v6 = new Vector3(c.x + e.x, c.y + e.y, c.z + e.z);
            var v7 = new Vector3(c.x - e.x, c.y + e.y, c.z + e.z);

            // Bottom
            Debug.DrawLine(v0, v1, color);
            Debug.DrawLine(v1, v2, color);
            Debug.DrawLine(v2, v3, color);
            Debug.DrawLine(v3, v0, color);

            // Top
            Debug.DrawLine(v4, v5, color);
            Debug.DrawLine(v5, v6, color);
            Debug.DrawLine(v6, v7, color);
            Debug.DrawLine(v7, v4, color);

            // Sides
            Debug.DrawLine(v0, v4, color);
            Debug.DrawLine(v1, v5, color);
            Debug.DrawLine(v2, v6, color);
            Debug.DrawLine(v3, v7, color);
        }

        private static Color GetNodeColor(MovementFlags flags)
        {
            if ((flags & MovementFlags.Unreachable) != 0)
                return Color.red;

            // Count available exits for coloring
            int exitCount = CountExits(flags);
            if (exitCount == 0)
                return new Color(0.5f, 0.5f, 0.5f); // Gray for isolated nodes
            if (exitCount <= 3)
                return Color.yellow;
            if (exitCount <= 6)
                return Color.green;

            return Color.cyan; // Well-connected node
        }

        private static int CountExits(MovementFlags flags)
        {
            int count = 0;
            if ((flags & MovementFlags.N) != 0) count++;
            if ((flags & MovementFlags.NE) != 0) count++;
            if ((flags & MovementFlags.EN) != 0) count++;
            if ((flags & MovementFlags.E) != 0) count++;
            if ((flags & MovementFlags.ES) != 0) count++;
            if ((flags & MovementFlags.SE) != 0) count++;
            if ((flags & MovementFlags.S) != 0) count++;
            if ((flags & MovementFlags.SW) != 0) count++;
            if ((flags & MovementFlags.WS) != 0) count++;
            if ((flags & MovementFlags.W) != 0) count++;
            if ((flags & MovementFlags.WN) != 0) count++;
            if ((flags & MovementFlags.NW) != 0) count++;
            return count;
        }

        private static void DrawNodeCross(float3 pos, Color color)
        {
            // Draw a small cross at the node position
            var halfSize = NodeCrossSize * 0.5f;

            // Horizontal cross (X-Z plane)
            Debug.DrawLine(
                new Vector3(pos.x - halfSize, pos.y, pos.z),
                new Vector3(pos.x + halfSize, pos.y, pos.z),
                color);
            Debug.DrawLine(
                new Vector3(pos.x, pos.y, pos.z - halfSize),
                new Vector3(pos.x, pos.y, pos.z + halfSize),
                color);

            // Vertical line (small upward tick)
            Debug.DrawLine(
                new Vector3(pos.x, pos.y, pos.z),
                new Vector3(pos.x, pos.y + halfSize, pos.z),
                color);
        }

        private static void DrawNodeConnections(
            float3 fromPos,
            MovementFlags flags,
            int3 sectionKey,
            int3 chunkIdx,
            int2 nodeLocal,
            ref Section section)
        {
            // Draw lines to neighboring nodes based on exit flags
            // Using the isometric/staggered grid layout

            // Primary directions with their grid offsets
            // Note: Due to staggered grid, offsets depend on row parity
            bool isOddRow = ((GetGlobalZ(sectionKey, chunkIdx, nodeLocal) & 1) != 0);

            // Draw each enabled direction
            if ((flags & MovementFlags.N) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.N, isOddRow), flags, MovementFlags.N_Up, MovementFlags.N_Down);

            if ((flags & MovementFlags.NE) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.NE, isOddRow), flags, MovementFlags.NE_Up, MovementFlags.NE_Down);

            if ((flags & MovementFlags.E) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.E, isOddRow), flags, MovementFlags.E_Up, MovementFlags.E_Down);

            if ((flags & MovementFlags.SE) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.SE, isOddRow), flags, MovementFlags.SE_Up, MovementFlags.SE_Down);

            if ((flags & MovementFlags.S) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.S, isOddRow), flags, MovementFlags.S_Up, MovementFlags.S_Down);

            if ((flags & MovementFlags.SW) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.SW, isOddRow), flags, MovementFlags.SW_Up, MovementFlags.SW_Down);

            if ((flags & MovementFlags.W) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.W, isOddRow), flags, MovementFlags.W_Up, MovementFlags.W_Down);

            if ((flags & MovementFlags.NW) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.NW, isOddRow), flags, MovementFlags.NW_Up, MovementFlags.NW_Down);

            // Secondary directions (diagonal alternates)
            if ((flags & MovementFlags.EN) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.EN, isOddRow), flags, MovementFlags.EN_Up, MovementFlags.EN_Down);

            if ((flags & MovementFlags.ES) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.ES, isOddRow), flags, MovementFlags.ES_Up, MovementFlags.ES_Down);

            if ((flags & MovementFlags.WS) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.WS, isOddRow), flags, MovementFlags.WS_Up, MovementFlags.WS_Down);

            if ((flags & MovementFlags.WN) != 0)
                DrawConnectionLine(fromPos, GetNeighborOffset(Direction.WN, isOddRow), flags, MovementFlags.WN_Up, MovementFlags.WN_Down);
        }

        private static long GetGlobalZ(int3 sectionKey, int3 chunkIdx, int2 nodeLocal)
        {
            long globalChunkZ = (long)sectionKey.z * SceneGraphConstants.SectionSizeChunksZ + chunkIdx.z;
            return globalChunkZ * SceneGraphConstants.ChunkSizeNodesZ + nodeLocal.y;
        }

        private enum Direction
        {
            N, NE, EN, E, ES, SE, S, SW, WS, W, WN, NW
        }

        private static float3 GetNeighborOffset(Direction dir, bool isOddRow)
        {
            // Staggered grid offsets for isometric layout
            // Odd rows are offset by +0.5 in X
            float nodeSize = SceneGraphConstants.NodeSize;
            float nodeSpacingZ = SceneGraphConstants.NodeSpacingZ;

            // For staggered/isometric grids, diagonal neighbors depend on row parity
            float xOffsetDiag = isOddRow ? nodeSize * 0.5f : -nodeSize * 0.5f;

            return dir switch
            {
                // Primary axial directions
                Direction.N => new float3(isOddRow ? nodeSize * 0.5f : -nodeSize * 0.5f, 0, nodeSpacingZ),
                Direction.S => new float3(isOddRow ? -nodeSize * 0.5f : nodeSize * 0.5f, 0, -nodeSpacingZ),
                Direction.E => new float3(nodeSize, 0, 0),
                Direction.W => new float3(-nodeSize, 0, 0),

                // Diagonals (NE/SE/SW/NW - primary)
                Direction.NE => new float3(isOddRow ? nodeSize : 0, 0, nodeSpacingZ),
                Direction.SE => new float3(isOddRow ? nodeSize : 0, 0, -nodeSpacingZ),
                Direction.SW => new float3(isOddRow ? 0 : -nodeSize, 0, -nodeSpacingZ),
                Direction.NW => new float3(isOddRow ? 0 : -nodeSize, 0, nodeSpacingZ),

                // Secondary diagonals (EN/ES/WS/WN)
                Direction.EN => new float3(nodeSize + (isOddRow ? nodeSize * 0.5f : -nodeSize * 0.5f), 0, nodeSpacingZ),
                Direction.ES => new float3(nodeSize + (isOddRow ? -nodeSize * 0.5f : nodeSize * 0.5f), 0, -nodeSpacingZ),
                Direction.WS => new float3(-nodeSize + (isOddRow ? -nodeSize * 0.5f : nodeSize * 0.5f), 0, -nodeSpacingZ),
                Direction.WN => new float3(-nodeSize + (isOddRow ? nodeSize * 0.5f : -nodeSize * 0.5f), 0, nodeSpacingZ),

                _ => float3.zero
            };
        }

        private static void DrawConnectionLine(float3 fromPos, float3 offset, MovementFlags flags, MovementFlags upFlag, MovementFlags downFlag)
        {
            // Calculate the target position with optional vertical offset
            float yOffset = 0f;
            if ((flags & upFlag) != 0)
                yOffset = SceneGraphConstants.MaxSlopeHeight;
            else if ((flags & downFlag) != 0)
                yOffset = -SceneGraphConstants.MaxSlopeHeight;

            var toPos = fromPos + offset + new float3(0, yOffset, 0);

            // Draw a semi-transparent connection line (only draw half to avoid double-drawing)
            var midPoint = (fromPos + toPos) * 0.5f;
            Color connectionColor = new Color(0.3f, 0.6f, 1f, ConnectionAlpha);

            // Vertical movement gets different color
            if (yOffset > 0)
                connectionColor = new Color(0.3f, 1f, 0.3f, ConnectionAlpha); // Green for upward
            else if (yOffset < 0)
                connectionColor = new Color(1f, 0.5f, 0.3f, ConnectionAlpha); // Orange for downward

            Debug.DrawLine(
                new Vector3(fromPos.x, fromPos.y, fromPos.z),
                new Vector3(midPoint.x, midPoint.y, midPoint.z),
                connectionColor);
        }
    }
}
