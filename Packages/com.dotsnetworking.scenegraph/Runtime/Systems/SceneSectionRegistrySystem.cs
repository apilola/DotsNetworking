using DotsNetworking.SceneGraph.Components;
using DotsNetworking.SceneGraph.Utils;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DotsNetworking.SceneGraph
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct SceneSectionRegistrySystem : ISystem
    {
        private const int InitialCapacity = 256;

        private EntityQuery unregisteredQuery;
        private EntityArchetype RegistryEntryArchetype;

        public void OnCreate(ref SystemState state)
        {
            //Registry entities have all components needed to represent a section's data
            //When they are created, we want them to all be in the same archetype and near eachother in memory
            RegistryEntryArchetype = state.EntityManager.CreateArchetype(
                typeof(SectionAddress),
                typeof(SectionBlob)
            );

            var manifest = SceneGraphManifest.I;
            if (manifest == null)
            {
                Debug.LogError("SceneGraphManifest is not loaded. Ensure it is included in the build.");
                state.Enabled = false;
                return;
            }

            if (!SystemAPI.HasSingleton<SceneSectionRegistry>())
            {
                var entity = state.EntityManager.CreateEntity(typeof(SceneSectionRegistry));
                state.EntityManager.SetComponentData(entity, new SceneSectionRegistry
                {
                    Map = new NativeParallelHashMap<SectionAddress, Entity>(manifest.SectionCount, Allocator.Persistent),
                });
            }

            ref var registry = ref SystemAPI.GetSingletonRW<SceneSectionRegistry>().ValueRW;

            using NativeArray<Entity> entities = new NativeArray<Entity>(manifest.SectionCount, Allocator.Temp);

            state.EntityManager.CreateEntity(RegistryEntryArchetype, entities);

            var i = 0;
            foreach (var subscene in manifest.Subscenes)
            {
                foreach (var section in subscene.Sections)
                {
                    var address = section.Address;
                    var entity = entities[i];

                    state.EntityManager.SetComponentData(entity, address);
                    NameEntity(ref state, entity, subscene, section);

                    if (!registry.Map.TryAdd(address, entity))
                    {
                        Debug.LogError($"Duplicate section address found in manifest: {address}");
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            state.EntityManager.DestroyEntity(entities.Slice(i, entities.Length - i));

            unregisteredQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                //Query includes SceneSection to avoid matching against entities created by other systems that happen to have SectionBlob and SectionAddress
                All = new[] { ComponentType.ReadOnly<SectionBlob>(), ComponentType.ReadOnly<SectionAddress>(), ComponentType.ReadOnly<SceneSection>() },
                None = new[] { ComponentType.ReadOnly<SceneSectionBlobRegistered>() },
            });

            state.RequireForUpdate<SceneSectionRegistry>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonRW<SceneSectionRegistry>(out var registry))
            {
                if (registry.ValueRW.Map.IsCreated)
                {
                    registry.ValueRW.Map.Dispose();
                    registry.ValueRW.Map = default;
                }
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            ref var registry = ref SystemAPI.GetSingletonRW<SceneSectionRegistry>().ValueRW;
            if (!registry.Map.IsCreated)
            {
                registry.Map = new NativeParallelHashMap<SectionAddress, Entity>(InitialCapacity, Allocator.Persistent);
            }

            int newCount = unregisteredQuery.CalculateEntityCount();
            if (newCount > 0)
            {
                int required = registry.Map.Count() + newCount;
                if (registry.Map.Capacity < required)
                {
                    registry.Map.Capacity = required;
                }
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (sectionBlob, address, _, entity) in SystemAPI.Query<RefRO<SectionBlob>, RefRO<SectionAddress>, Unity.Entities.SceneSection>()
                         .WithNone<SceneSectionBlobRegistered>()
                         .WithEntityAccess())
            {
                var sectionAddress = address.ValueRO;
                if (!registry.Map.TryGetValue(sectionAddress, out var registryEntity))
                {
                    registryEntity = state.EntityManager.CreateEntity(RegistryEntryArchetype);
                    state.EntityManager.SetComponentData<SectionAddress>(registryEntity, address.ValueRO);
                    registry.Map.Add(sectionAddress, registryEntity);
                }

                if (state.EntityManager.HasComponent<SectionBlob>(registryEntity))
                    ecb.SetComponent(registryEntity, sectionBlob.ValueRO);
                else
                    ecb.AddComponent(registryEntity, sectionBlob.ValueRO);

                if (state.EntityManager.HasComponent<SectionAddress>(registryEntity))
                    ecb.SetComponent(registryEntity, sectionAddress);
                else
                    ecb.AddComponent(registryEntity, sectionAddress);

                ecb.AddComponent(entity, new SceneSectionBlobRegistered
                {
                    Address = sectionAddress,
                    RegistryEntity = registryEntity,
                });
            }

            foreach (var (registered, entity) in SystemAPI.Query<RefRO<SceneSectionBlobRegistered>>()
                         .WithNone<SectionBlob>()
                         .WithEntityAccess())
            {
                var registryEntity = registered.ValueRO.RegistryEntity;
                if (registryEntity != Entity.Null && state.EntityManager.HasComponent<SectionBlob>(registryEntity))
                {
                    ecb.SetComponent(registryEntity, default(SectionBlob));
                }
                ecb.RemoveComponent<SceneSectionBlobRegistered>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void NameEntity(ref SystemState state, Entity entity, SubsceneDefinition subscene, SectionDefinition section)
        {
            var  sectionId = SceneGraphMath.UnpackSectionId(section.Address.SectionId);
            state.EntityManager.SetName(entity, $"Section: {Path.GetFileNameWithoutExtension(subscene.ScenePath)} {sectionId}");
        }
    }
}
