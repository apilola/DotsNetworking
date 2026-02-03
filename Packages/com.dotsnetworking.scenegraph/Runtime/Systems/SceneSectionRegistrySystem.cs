using System;
using System.IO;
using BovineLabs.Core.Extensions;
using DotsNetworking.SceneGraph.Collections;
using DotsNetworking.SceneGraph.Components;
using DotsNetworking.SceneGraph.Utils;
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
        private const int RegistryPageSize = 64;

        private EntityQuery unregisteredQuery;
        private EntityArchetype sectionArchetype;

        public void OnCreate(ref SystemState state)
        {
            var manifest = SceneGraphManifest.I;
            if (manifest == null)
            {
                Debug.LogError("SceneGraphManifest is not loaded. Ensure it is included in the build.");
                state.Enabled = false;
                return;
            }
            if (!SystemAPI.HasSingleton<SceneSectionRegistry>())
            {
                state.EntityManager.CreateSingleton(new SceneSectionRegistry
                {
                    Registry = NativeRegistryBuilder.Create<SectionAddress>(
                        Allocator.Persistent,
                        RegistryPageSize,
                        manifest.SectionCount,
                        typeof(BlobAssetReference<Section>),
                        typeof(Entity)),
                }, $"{nameof(SceneSectionRegistry)}");
            }

            sectionArchetype = state.EntityManager.CreateArchetype(typeof(SectionAddress));
            
            ref var registry = ref SystemAPI.GetSingletonRW<SceneSectionRegistry>().ValueRW;
            if (!registry.Registry.IsCreated)
            {
                registry.Registry = NativeRegistryBuilder.Create<SectionAddress>(
                    Allocator.Persistent,
                    RegistryPageSize,
                    manifest.SectionCount,
                    typeof(BlobAssetReference<Section>),
                    typeof(Entity));
            }
            EnsureManifestKeys(ref registry, manifest, state.EntityManager);

            unregisteredQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                // Query includes SceneSection to avoid matching against entities created by other systems.
                All = new[] { ComponentType.ReadOnly<SectionBlob>(), ComponentType.ReadOnly<SceneSection>() },
                None = new[] { ComponentType.ReadOnly<SceneSectionBlobRegistered>() },
            });

            state.RequireForUpdate<SceneSectionRegistry>();
        }

        private void EnsureManifestKeys(
            ref SceneSectionRegistry registry,
            SceneGraphManifest manifest,
            EntityManager entityManager)
        {
            var missingCount = 0;
            foreach (var subscene in manifest.Subscenes)
            {
                foreach (var section in subscene.Sections)
                {
                    if (!registry.Registry.TryGetIndex(section.Address, out _))
                        missingCount++;
                }
            }

            if (missingCount == 0)
                return;

            using var entities = new NativeArray<Entity>(missingCount, Allocator.Temp);
            entityManager.CreateEntity(sectionArchetype, entities);

            var i = 0;
            foreach (var subscene in manifest.Subscenes)
            {
                foreach (var section in subscene.Sections)
                {
                    var address = section.Address;
                    if (!registry.Registry.TryGetIndex(address, out _))
                    {
                        registry.Registry.RegisterKey(address);
                        var sectionEntity = entities[i++];
                        using (var handle = registry.Registry.AcquireWrite<Entity>(address))
                        {
                            handle.Value = sectionEntity;
                        }
                        entityManager.SetComponentData(sectionEntity, address);
                        SetName(entityManager, sectionEntity, subscene, address);
                    }
                }
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void SetName(EntityManager entityManager, Entity entity, SubsceneDefinition subscene, SectionAddress section)
        {
            var sceneName = Path.GetFileNameWithoutExtension(subscene.ScenePath);
            var sectionCoord = SceneGraphMath.UnpackSectionId(section.SectionId);

            entityManager.SetName(entity, $"Section: {sceneName} {sectionCoord}");
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonRW<SceneSectionRegistry>(out var registry))
            {
                if (registry.ValueRW.Registry.IsCreated)
                    registry.ValueRW.Registry.Dispose();

                registry.ValueRW.Registry = default;
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            ref var registry = ref SystemAPI.GetSingletonRW<SceneSectionRegistry>().ValueRW;
            if (!registry.Registry.IsCreated)
                throw new InvalidOperationException("SceneSectionRegistry is not initialized.");

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (sectionBlob, entity) in SystemAPI.Query<RefRO<SectionBlob>>()
                         .WithAll<Unity.Entities.SceneSection>()
                         .WithNone<SceneSectionBlobRegistered>()
                         .WithEntityAccess())
            {
                var sceneSection = state.EntityManager.GetSharedComponent<Unity.Entities.SceneSection>(entity);
                var sectionAddress = new SectionAddress(sceneSection.SceneGUID, (uint)sceneSection.Section);
                if (!registry.Registry.TryGetIndex(sectionAddress, out _))
                {
                    registry.Registry.RegisterKey(sectionAddress);
                }

                using (var handle = registry.Registry.AcquireWrite<BlobAssetReference<Section>>(sectionAddress))
                {
                    if (handle.IsAccessible)
                        handle.Value = sectionBlob.ValueRO.BlobRef;
                }

                ecb.AddComponent(entity, new SceneSectionBlobRegistered
                {
                    Address = sectionAddress,
                });
            }

            foreach (var (registered, entity) in SystemAPI.Query<RefRO<SceneSectionBlobRegistered>>()
                         .WithNone<SectionBlob>()
                         .WithEntityAccess())
            {
                var address = registered.ValueRO.Address;
                if (registry.Registry.TryGetIndex(address, out _))
                {
                    using (var handle = registry.Registry.AcquireWrite<BlobAssetReference<Section>>(address))
                    {
                        if (handle.IsAccessible)
                            handle.Value = default;
                    }
                }
                ecb.RemoveComponent<SceneSectionBlobRegistered>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
