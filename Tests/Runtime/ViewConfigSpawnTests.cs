using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests
{
    /// <summary>
    /// The config route through the spawn path, and proof the bare-key route still works unchanged.
    /// </summary>
    public sealed class ViewConfigSpawnTests
    {
        private World _world;
        private EntityManager _entityManager;
        private FakeViewAssetProvider _provider;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.ViewConfigTests");
            _entityManager = _world.EntityManager;
            _provider = new FakeViewAssetProvider();
            _registry = new EntityViewRegistry(_provider);
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("goblin", pool: 4, uniformScale: 2f, position: new Vector3(0f, 1f, 0f), layerId: 3, order: 7);

            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = "goblin", Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);
        }

        [TearDown]
        public void TearDown()
        {
            _catalog.Dispose();
            Object.DestroyImmediate(_library);
            Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private Entity CreateEntity(float3 position)
        {
            var entity = _entityManager.CreateEntity();
            _entityManager.AddComponentData(entity, LocalTransform.FromPosition(position));
            _entityManager.AddComponentData(entity, new LocalToWorld
            {
                Value = float4x4.TRS(position, quaternion.identity, new float3(1f, 1f, 1f)),
            });
            return entity;
        }

        private void Tick()
        {
            _world.GetExistingSystem<EntityViewDespawnSystem>().Update(_world.Unmanaged);
            _world.GetExistingSystem<EntityViewSpawnSystem>().Update(_world.Unmanaged);
            _world.GetExistingSystem<EntityViewTransformSyncSystem>().Update(_world.Unmanaged);
        }

        [Test]
        public void ConfigRef_SuppliesTheKey_OverridingTheRequestsOwn()
        {
            var entity = CreateEntity(float3.zero);
            _entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "ignored-key" });
            _entityManager.AddComponentData(entity, _catalog.CreateRef(_catalog.IndexOf("goblin")));

            Tick();

            var viewId = _entityManager.GetComponentData<EntityViewLink>(entity).ViewId;
            Assert.AreEqual("goblin", _registry.Get(viewId).name, "the fake provider names the instance after its key");
        }

        [Test]
        public void ConfigOffsets_AreAppliedAtSpawn_AndSurviveTheSync()
        {
            // The regression this guards: an offset applied only at spawn is erased by the very next
            // sync, which looks like the offset silently not working.
            var entity = CreateEntity(new float3(5f, 0f, 0f));
            _entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
            _entityManager.AddComponentData(entity, _catalog.CreateRef(_catalog.IndexOf("goblin")));

            Tick();

            var view = _registry.Get(_entityManager.GetComponentData<EntityViewLink>(entity).ViewId);
            Assert.AreEqual(new Vector3(5f, 1f, 0f), view.transform.position, "position offset applied");
            Assert.AreEqual(2f, view.transform.localScale.x, 1e-4f, "scale from the config");

            Tick(); // a second sync must not erase it

            Assert.AreEqual(new Vector3(5f, 1f, 0f), view.transform.position);
            Assert.AreEqual(2f, view.transform.localScale.x, 1e-4f);
        }

        [Test]
        public void ConfigRef_CarriesTheSortingKey_EvenThoughNothingAppliesItYet()
        {
            var entity = CreateEntity(float3.zero);
            _entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
            _entityManager.AddComponentData(entity, _catalog.CreateRef(_catalog.IndexOf("goblin")));

            Tick();

            var sorting = _entityManager.GetComponentData<ViewSortingKey>(entity);
            Assert.AreEqual(3, sorting.LayerId);
            Assert.AreEqual(7, sorting.Order);
        }

        [Test]
        public void BareKeyPath_IsUnchanged_NoConfigRefNoOffset()
        {
            var entity = CreateEntity(new float3(2f, 0f, 3f));
            _entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "torch" });

            Tick();

            Assert.IsTrue(_entityManager.HasComponent<EntityViewLink>(entity));
            Assert.IsFalse(_entityManager.HasComponent<ViewSortingKey>(entity), "no config, no sorting key");

            var view = _registry.Get(_entityManager.GetComponentData<EntityViewLink>(entity).ViewId);
            Assert.AreEqual("torch", view.name);
            Assert.AreEqual(new Vector3(2f, 0f, 3f), view.transform.position, "identity offset, exactly where the entity is");
            Assert.AreEqual(1f, view.transform.localScale.x, 1e-4f);
        }

        [Test]
        public void OutOfRangeConfigIndex_FallsBackToTheRequestKey_RatherThanSpawningTheWrongThing()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            var entity = CreateEntity(float3.zero);
            _entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "torch" });
            // Stamped with the live version so it is the RANGE check that fires, not the version one.
            _entityManager.AddComponentData(entity, new ViewConfigRef { Index = 99, Version = _catalog.Version });

            Tick();

            var view = _registry.Get(_entityManager.GetComponentData<EntityViewLink>(entity).ViewId);
            Assert.AreEqual("torch", view.name, "falls back rather than rendering an arbitrary archetype");

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        }
    }
}
