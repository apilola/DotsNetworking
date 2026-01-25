using DotsNetworking.WorldGraph.Utils;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DotsNetworking.WorldGraph.Editor
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct WorldGraphSceneSectionBakingSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
        }

        public void OnDestroy(ref SystemState state)
        {
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var pendingUpdates = new System.Collections.Generic.List<(Entity entity, SceneSection section)>();

            foreach (var (transformAuthoring, entity) in SystemAPI.Query<Unity.Entities.TransformAuthoring>().WithEntityAccess())
            {
                if (!entityManager.HasComponent<SceneSection>(entity))
                {
                    continue;
                }

                Vector3 unityPos = transformAuthoring.Position;
                float3 worldPos = new float3(unityPos.x, unityPos.y, unityPos.z);
                WorldGraphMath.WorldToGraph(worldPos, out int3 regionKey, out _, out _, out _);

                int sectionIndex = ComputeSectionIndex(regionKey);

                var currentSection = entityManager.GetSharedComponent<SceneSection>(entity);
                if (currentSection.Section != sectionIndex)
                {
                    currentSection.Section = sectionIndex;
                    pendingUpdates.Add((entity, currentSection));
                }
            }

            for (int i = 0; i < pendingUpdates.Count; i++)
            {
                var update = pendingUpdates[i];
                entityManager.SetSharedComponent(update.entity, update.section);
            }
        }

        private static int ComputeSectionIndex(int3 regionKey)
        {
            uint3 regionUnsigned = new uint3(
                (uint)math.max(0, regionKey.x),
                (uint)math.max(0, regionKey.y),
                (uint)math.max(0, regionKey.z));

            if (regionKey.x < 0 || regionKey.y < 0 || regionKey.z < 0)
            {
                uint packed = WorldGraphMath.PackRegionId(regionKey);
                return (int)(packed + 1u);
            }

            if (math.cmax(regionUnsigned) > Morton.MaxCoordinateValue32)
            {
                uint packed = WorldGraphMath.PackRegionId(regionKey);
                return (int)(packed + 1u);
            }

            uint morton = Morton.EncodeMorton32(regionUnsigned);
            return (int)(morton + 1u);
        }
    }
}
