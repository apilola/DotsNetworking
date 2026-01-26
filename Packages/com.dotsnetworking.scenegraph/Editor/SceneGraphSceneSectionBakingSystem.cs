using DotsNetworking.SceneGraph.Utils;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DotsNetworking.SceneGraph.Editor
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct SceneGraphSceneSectionBakingSystem : ISystem
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
                SceneGraphMath.WorldToGraph(worldPos, out int3 sectionKey, out _, out _, out _);

                int sectionIndex = ComputeSectionIndex(sectionKey);

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

        private static int ComputeSectionIndex(int3 sectionKey)
        {
            return (int)SceneGraphMath.PackSectionId(sectionKey);
        }
    }
}



