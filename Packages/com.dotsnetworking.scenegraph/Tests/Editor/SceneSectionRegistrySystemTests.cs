using System;
using DotsNetworking.SceneGraph.Components;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DotsNetworking.SceneGraph.Tests.Editor
{
    [TestFixture]
    public sealed class SceneSectionRegistrySystemTests
    {
        private World world;
        private EntityManager manager;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            try
            {
                SceneSectionRegistryTestSceneBuilder.Build();
            }
            catch (Exception ex)
            {
                Debug.LogError("SceneSectionRegistrySystemTests OneTimeSetUp failed.");
                Debug.LogException(ex);
                throw;
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            new SceneSectionRegistryTestSceneBuilder().Cleanup();
        }

        [SetUp]
        public void SetUp()
        {
            world = new World("SceneSectionRegistrySystemTests");
            manager = world.EntityManager;
            world.GetOrCreateSystem<SceneSectionRegistrySystem>();
        }

        [TearDown]
        public void TearDown()
        {
            if (world != null && world.IsCreated)
            {
                world.Dispose();
            }
        }

        [Test]
        public void OnCreate_PopulatesRegistryFromManifest()
        {
            var systemHandle = world.GetOrCreateSystem<SceneSectionRegistrySystem>();
            systemHandle.Update(world.Unmanaged);

            var manifest = SceneGraphManifest.I;
            Assert.IsNotNull(manifest);
            Assert.Greater(manifest.SectionCount, 0, "Test scene did not bake any sections.");

            var registry = manager.CreateEntityQuery(typeof(SceneSectionRegistry)).GetSingleton<SceneSectionRegistry>();
            Assert.IsTrue(registry.Map.IsCreated);
            Assert.AreEqual(manifest.SectionCount, registry.Map.Count());

            foreach (var subscene in manifest.Subscenes)
            {
                foreach (var section in subscene.Sections)
                {
                    Assert.IsTrue(registry.Map.TryGetValue(section.Address, out var registryEntity));
                    Assert.IsTrue(manager.HasComponent<SectionAddress>(registryEntity));
                    Assert.IsTrue(manager.HasComponent<SectionBlob>(registryEntity));

                    var address = manager.GetComponentData<SectionAddress>(registryEntity);
                    Assert.AreEqual(section.Address, address);
                }
            }
        }

        [Test]
        public void OnUpdate_RegistersAndUnregistersSectionBlob()
        {
            var systemHandle = world.GetOrCreateSystem<SceneSectionRegistrySystem>();
            systemHandle.Update(world.Unmanaged);

            var manifest = SceneGraphManifest.I;
            Assert.IsNotNull(manifest);
            Assert.Greater(manifest.SubsceneCount, 0);
            Assert.Greater(manifest.Subscenes[0].Sections.Count, 0);

            var address = manifest.Subscenes[0].Sections[0].Address;

            var owner = manager.CreateEntity(typeof(SectionBlob), typeof(SectionAddress));
            manager.SetComponentData(owner, address);
            manager.AddSharedComponent(owner, new SceneSection
            {
                SceneGUID = address.SceneGuid,
                Section = (int)address.SectionId
            });

            systemHandle.Update(world.Unmanaged);

            Assert.IsTrue(manager.HasComponent<SceneSectionBlobRegistered>(owner));
            var registered = manager.GetComponentData<SceneSectionBlobRegistered>(owner);
            Assert.AreEqual(address, registered.Address);

            var registry = manager.CreateEntityQuery(typeof(SceneSectionRegistry)).GetSingleton<SceneSectionRegistry>();
            Assert.IsTrue(registry.Map.TryGetValue(address, out var registryEntity));
            Assert.AreEqual(registryEntity, registered.RegistryEntity);
            Assert.IsTrue(manager.HasComponent<SectionBlob>(registryEntity));

            manager.RemoveComponent<SectionBlob>(owner);
            systemHandle.Update(world.Unmanaged);

            Assert.IsFalse(manager.HasComponent<SceneSectionBlobRegistered>(owner));
            var registryBlob = manager.GetComponentData<SectionBlob>(registryEntity);
            Assert.IsFalse(registryBlob.BlobRef.IsCreated);
        }

        [Test]
        public void OnUpdate_ClearsRegistryWhenOwnerDestroyed()
        {
            var systemHandle = world.GetOrCreateSystem<SceneSectionRegistrySystem>();
            systemHandle.Update(world.Unmanaged);

            var manifest = SceneGraphManifest.I;
            Assert.IsNotNull(manifest);
            Assert.Greater(manifest.SubsceneCount, 0);
            Assert.Greater(manifest.Subscenes[0].Sections.Count, 0);

            var address = manifest.Subscenes[0].Sections[0].Address;

            var blobRef = CreateTestBlob();
            try
            {
                var owner = manager.CreateEntity(typeof(SectionBlob), typeof(SectionAddress));
                manager.SetComponentData(owner, address);
                manager.SetComponentData(owner, new SectionBlob { BlobRef = blobRef });
                manager.AddSharedComponent(owner, new SceneSection
                {
                    SceneGUID = address.SceneGuid,
                    Section = (int)address.SectionId
                });

                systemHandle.Update(world.Unmanaged);

                var registry = manager.CreateEntityQuery(typeof(SceneSectionRegistry)).GetSingleton<SceneSectionRegistry>();
                Assert.IsTrue(registry.Map.TryGetValue(address, out var registryEntity));

                var before = manager.GetComponentData<SectionBlob>(registryEntity);
                Assert.IsTrue(before.BlobRef.IsCreated);

                manager.DestroyEntity(owner);
                systemHandle.Update(world.Unmanaged);

                var after = manager.GetComponentData<SectionBlob>(registryEntity);
                Assert.IsFalse(after.BlobRef.IsCreated);
            }
            finally
            {
                if (blobRef.IsCreated)
                    blobRef.Dispose();
            }
        }

        private static BlobAssetReference<Section> CreateTestBlob()
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<Section>();
            builder.Allocate(ref root.Chunks, 0);
            builder.Allocate(ref root.ChunkLookup, 0);
            return builder.CreateBlobAssetReference<Section>(Allocator.Persistent);
        }
    }
}
