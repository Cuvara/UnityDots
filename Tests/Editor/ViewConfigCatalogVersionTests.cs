using System;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The catalog's versioning contract: a rebuild is visible, stamped refs resolve, stale or
    /// unstamped refs are refused rather than resolved to a different record, and every world the
    /// catalog is installed in follows the rebuild.
    /// </summary>
    public sealed class ViewConfigCatalogVersionTests
    {
        private World _world;
        private SpawningViewAssetProvider _provider;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _goblin;
        private ViewConfig _torch;

        [SetUp]
        public void SetUp()
        {
            _world = new World("ViewConfigCatalogVersionTests");
            _provider = new SpawningViewAssetProvider();
            _registry = new EntityViewRegistry(_provider);
            DotsViewBootstrap.Install(_world, _registry);

            _goblin = ScriptableObject.CreateInstance<ViewConfig>();
            _goblin.Configure("goblin", pool: 4);
            _torch = ScriptableObject.CreateInstance<ViewConfig>();
            _torch.Configure("torch", pool: 2);

            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = _goblin },
                new ViewArchetypeLibrary.Entry { Name = "torch", Config = _torch });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);
        }

        [TearDown]
        public void TearDown()
        {
            _catalog.Dispose();
            UnityEngine.Object.DestroyImmediate(_library);
            UnityEngine.Object.DestroyImmediate(_goblin);
            UnityEngine.Object.DestroyImmediate(_torch);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private void Tick()
        {
            _world.GetExistingSystem<EntityViewDespawnSystem>().Update(_world.Unmanaged);
            _world.GetExistingSystem<EntityViewSpawnSystem>().Update(_world.Unmanaged);
        }

        private Entity Request(string fallbackKey, ViewConfigRef configRef)
        {
            var entityManager = _world.EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = fallbackKey });
            entityManager.AddComponentData(entity, configRef);
            entityManager.AddComponentData(entity, LocalTransform.Identity);
            entityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            return entity;
        }

        private string SpawnedName(Entity entity) =>
            _registry.Get(_world.EntityManager.GetComponentData<EntityViewLink>(entity).ViewId).name;

        private ViewConfigTableReference InstalledTable()
        {
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewConfigTableReference>());
            return query.GetSingleton<ViewConfigTableReference>();
        }

        [Test]
        public void Version_StartsAtOne_AndIncrementsPerBuild_IntoTheBlob()
        {
            Assert.AreEqual(1, _catalog.Version);
            Assert.AreEqual(1, _catalog.Table.Value.Version);

            _catalog.Build(_library);

            Assert.AreEqual(2, _catalog.Version);
            Assert.AreEqual(2, _catalog.Table.Value.Version);
        }

        [Test]
        public void CreateRef_IsStamped_AndRejectsOutOfRange_AndUnbuilt()
        {
            var configRef = _catalog.CreateRef(_catalog.IndexOf("torch"));
            Assert.AreEqual(1, configRef.Index);
            Assert.AreEqual(_catalog.Version, configRef.Version);

            Assert.IsTrue(_catalog.TryCreateRef("goblin", out var byName));
            Assert.AreEqual(0, byName.Index);
            Assert.IsFalse(_catalog.TryCreateRef("wyvern", out _));

            var error = Assert.Throws<ArgumentOutOfRangeException>(() => _catalog.CreateRef(7));
            StringAssert.Contains("2 record(s)", error.Message);

            using var unbuilt = new ViewConfigCatalog();
            Assert.Throws<InvalidOperationException>(() => unbuilt.CreateRef(0));
        }

        [Test]
        public void StampedRef_ResolvesToItsRecord()
        {
            var entity = Request("fallback", _catalog.CreateRef(_catalog.IndexOf("torch")));

            Tick();

            Assert.AreEqual("torch", SpawnedName(entity));
        }

        [Test]
        public void UnstampedRef_IsRefused_AndFallsBackToTheRequestKey()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("version 0.*unstamped"));
            var entity = Request("fallback", new ViewConfigRef { Index = 1 });

            Tick();

            Assert.AreEqual("fallback", SpawnedName(entity), "an index without a version never picks a record");
        }

        [Test]
        public void RefFromBeforeARebuild_NeverResolvesToTheRecordNowAtThatIndex()
        {
            // Issue a ref to "torch" (index 1), then rebuild with the entries swapped so index 1 is
            // "goblin". Before versioning this spawned a goblin for an entity that asked for a torch.
            var stale = _catalog.CreateRef(_catalog.IndexOf("torch"));
            _library.Configure(
                new ViewArchetypeLibrary.Entry { Name = "torch", Config = _torch },
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = _goblin });
            _catalog.Build(_library);
            Assert.AreEqual("goblin", _catalog[1].ViewKey.ToString(), "the swap happened");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("rebuilt"));
            var entity = Request("fallback", stale);
            Tick();

            Assert.AreEqual("fallback", SpawnedName(entity), "refused, not silently swapped");

            var fresh = Request("fallback", _catalog.CreateRef(_catalog.IndexOf("torch")));
            Tick();
            Assert.AreEqual("torch", SpawnedName(fresh), "a re-issued ref resolves");
        }

        [Test]
        public void Rebuild_RepublishesIntoEveryInstalledWorld_SoNoSingletonPointsAtTheFreedBlob()
        {
            var other = new World("ViewConfigCatalogVersionTests.Other");
            try
            {
                _catalog.Install(other);
                Assert.AreEqual(2, _catalog.InstalledWorldCount);

                _catalog.Build(_library);

                Assert.AreEqual(2, InstalledTable().Table.Value.Version);
                using var query = other.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewConfigTableReference>());
                Assert.AreEqual(2, query.GetSingleton<ViewConfigTableReference>().Table.Value.Version);
            }
            finally
            {
                _catalog.Uninstall(other);
                other.Dispose();
            }
        }

        [Test]
        public void Uninstall_RemovesTheSingleton_AndIsSafeTwice()
        {
            _catalog.Uninstall(_world);
            _catalog.Uninstall(_world);

            Assert.IsFalse(_catalog.IsInstalled(_world));
            Assert.AreEqual(0, _catalog.InstalledWorldCount);
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewConfigTableReference>());
            Assert.IsTrue(query.IsEmpty);
            Assert.IsTrue(_catalog.Table.IsCreated, "the blob is the catalog's, not the world's");
        }

        [Test]
        public void Dispose_RemovesTheSingletonFromInstalledWorlds()
        {
            _catalog.Dispose();

            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewConfigTableReference>());
            Assert.IsTrue(query.IsEmpty, "nothing in the world references the freed blob");
            Assert.IsFalse(_catalog.Table.IsCreated);
            Assert.AreEqual(0, _catalog.InstalledWorldCount);
        }

        [Test]
        public void DisposedWorld_IsPruned_NotHeld()
        {
            var other = new World("ViewConfigCatalogVersionTests.Disposed");
            _catalog.Install(other);
            other.Dispose();

            Assert.AreEqual(1, _catalog.InstalledWorldCount);
            Assert.DoesNotThrow(() => _catalog.Build(_library), "a rebuild skips the disposed world");
        }

        [Test]
        public void InstallTwice_OneSingleton()
        {
            _catalog.Install(_world);

            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewConfigTableReference>());
            Assert.AreEqual(1, query.CalculateEntityCount());
            Assert.AreEqual(1, _catalog.InstalledWorldCount);
        }

        [Test]
        public void ViewKeys_ListsDistinctKeys_ForThePrefabReplacementDiff()
        {
            var before = _catalog.ViewKeys();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = "goblin", Config = _goblin });
            _catalog.Build(_library);
            var after = _catalog.ViewKeys();

            before.ExceptWith(after);
            CollectionAssert.AreEquivalent(new[] { "torch" }, before, "the keys a provider may now drop");
        }
    }
}
