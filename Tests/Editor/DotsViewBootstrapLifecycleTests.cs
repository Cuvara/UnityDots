using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The view module's lifecycle contract: install twice, uninstall twice, replace the registry,
    /// a scene-reload-shaped session teardown, two worlds, and repeated full cycles.
    /// </summary>
    public sealed class DotsViewBootstrapLifecycleTests
    {
        private World _world;
        private SpawningViewAssetProvider _provider;
        private EntityViewRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _world = new World("DotsViewBootstrapLifecycleTests");
            _provider = new SpawningViewAssetProvider();
            _registry = new EntityViewRegistry(_provider);
        }

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }
        }

        /// <summary>
        /// One presentation frame through the real group, so despawn → spawn → sync → overlay run in
        /// the order the bootstrap sorted them — the same drive the overlay consumer tests use.
        /// </summary>
        private static void Tick(World world) => world.GetExistingSystemManaged<ViewSystemGroup>().Update();

        private static Entity CreateRequest(World world, string key)
        {
            var entityManager = world.EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = key });
            entityManager.AddComponentData(entity, LocalTransform.Identity);
            entityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            return entity;
        }

        private static int SingletonCount<T>(World world) where T : IComponentData
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            return query.CalculateEntityCount();
        }

        [Test]
        public void InstallTwice_OneSingleton_OneRecord_NoDuplicateViews()
        {
            DotsViewBootstrap.Install(_world, _registry);
            DotsViewBootstrap.Install(_world, _registry);
            CreateRequest(_world, "goblin");

            Tick(_world);
            Tick(_world);

            Assert.AreEqual(1, SingletonCount<EntityViewRegistryReference>(_world));
            Assert.AreEqual(1, DotsModules.Installed(_world).Count);
            Assert.AreEqual(2, DotsModules.InstallCount(_world, DotsViewBootstrap.ModuleName));
            Assert.AreEqual(1, _registry.Count);
            Assert.AreEqual(1, _provider.AcquireCount);
            Assert.AreSame(_registry, DotsViewBootstrap.InstalledRegistry(_world));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
        }

        [Test]
        public void InstallTwice_DoesNotDuplicateSystems()
        {
            DotsViewBootstrap.Install(_world, _registry);
            var before = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewLifecycleGroup>());

            DotsViewBootstrap.Install(_world, _registry);
            var after = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewLifecycleGroup>());

            CollectionAssert.AreEqual(before, after);
            Assert.AreEqual(2, after.Count);
        }

        [Test]
        public void Uninstall_RecyclesViews_HandsRequestsBack_AndIsSafeTwice()
        {
            DotsViewBootstrap.Install(_world, _registry);
            var entity = CreateRequest(_world, "goblin");
            Tick(_world);
            Assert.AreEqual(1, _provider.LiveInstances);

            DotsViewBootstrap.Uninstall(_world);
            DotsViewBootstrap.Uninstall(_world);

            Assert.AreEqual(0, _provider.LiveInstances, "every view went back to the provider");
            Assert.AreEqual(0, _registry.Count);
            Assert.IsFalse(DotsViewBootstrap.IsInstalled(_world));
            Assert.IsFalse(DotsModules.IsInstalled(_world, DotsViewBootstrap.ModuleName));
            Assert.IsFalse(_world.EntityManager.HasComponent<EntityViewLink>(entity), "no handle into a registry the world no longer presents through");
            Assert.IsFalse(_world.EntityManager.HasComponent<EntityViewLinkCleanup>(entity));
            Assert.IsFalse(_world.EntityManager.HasComponent<ViewTransformOffset>(entity));
            Assert.IsTrue(_world.EntityManager.HasComponent<EntityViewRequest>(entity), "the entity asks again on the next install");
            Assert.AreEqual("goblin", _world.EntityManager.GetComponentData<EntityViewRequest>(entity).ViewKey.ToString());

            // Temporary disable: the systems are still there and idle.
            Assert.AreNotEqual(SystemHandle.Null, _world.GetExistingSystem<EntityViewSpawnSystem>());
            Assert.IsNotNull(_world.GetExistingSystemManaged<ViewSystemGroup>());
            Assert.DoesNotThrow(() => Tick(_world), "idle systems tick harmlessly without a registry");
            Assert.AreEqual(0, _provider.LiveInstances);
        }

        [Test]
        public void Reinstall_AfterUninstall_RespawnsTheSameEntities_Once()
        {
            DotsViewBootstrap.Install(_world, _registry);
            CreateRequest(_world, "goblin");
            CreateRequest(_world, "torch");
            Tick(_world);
            DotsViewBootstrap.Uninstall(_world);

            DotsViewBootstrap.Install(_world, _registry);
            Tick(_world);
            Tick(_world);

            Assert.AreEqual(2, _registry.Count);
            Assert.AreEqual(2, _provider.LiveInstances, "each entity has exactly one view again");
            Assert.AreEqual(4, _provider.AcquireCount, "two before, two after");
        }

        [Test]
        public void ReplacingTheRegistry_MovesEveryView_NoDuplicates_NoStaleHandles()
        {
            DotsViewBootstrap.Install(_world, _registry);
            var a = CreateRequest(_world, "goblin");
            var b = CreateRequest(_world, "torch");
            Tick(_world);

            var replacementProvider = new SpawningViewAssetProvider();
            var replacement = new EntityViewRegistry(replacementProvider);
            DotsViewBootstrap.Install(_world, replacement);

            Assert.AreEqual(0, _registry.Count, "the old registry gave everything back");
            Assert.AreEqual(0, _provider.LiveInstances);
            Assert.IsFalse(_world.EntityManager.HasComponent<EntityViewLink>(a));
            Assert.IsTrue(_world.EntityManager.HasComponent<EntityViewRequest>(b));

            Tick(_world);

            Assert.AreEqual(2, replacement.Count);
            Assert.AreEqual(2, replacementProvider.LiveInstances);
            Assert.AreEqual(0, _provider.LiveInstances, "nothing came back to life in the old provider");
            Assert.AreSame(replacement, DotsViewBootstrap.InstalledRegistry(_world));
            Assert.AreEqual(1, SingletonCount<EntityViewRegistryReference>(_world));

            var viewId = _world.EntityManager.GetComponentData<EntityViewLink>(a).ViewId;
            Assert.IsNotNull(replacement.Get(viewId), "the link resolves in the registry that is installed");
        }

        [Test]
        public void SessionTeardown_LeavesTheRootScopedViewModule_AndItsViews()
        {
            DotsViewBootstrap.Install(_world, _registry, DotsModuleScope.Root);
            var camera = new CameraFollowConfig();
            CameraFollowBootstrap.Install(_world, camera, DotsModuleScope.Session);
            CreateRequest(_world, "goblin");
            Tick(_world);

            // What a scene reload does: every session-owned module goes, the root ones stay.
            var removed = DotsModules.UninstallScope(_world, DotsModuleScope.Session);

            Assert.AreEqual(1, removed);
            Assert.IsFalse(CameraFollowBootstrap.IsInstalled(_world));
            Assert.IsTrue(DotsViewBootstrap.IsInstalled(_world));
            Assert.AreEqual(1, _registry.Count, "root-scoped views survive the session");
            Assert.IsTrue(DotsModules.TryGetScope(_world, DotsViewBootstrap.ModuleName, out var scope));
            Assert.AreEqual(DotsModuleScope.Root, scope);
        }

        [Test]
        public void TwoWorlds_UninstallingOne_LeavesTheOthersViewsAndSingletons()
        {
            var other = new World("DotsViewBootstrapLifecycleTests.Other");
            var otherProvider = new SpawningViewAssetProvider();
            var otherRegistry = new EntityViewRegistry(otherProvider);
            try
            {
                DotsViewBootstrap.Install(_world, _registry);
                DotsViewBootstrap.Install(other, otherRegistry);
                CreateRequest(_world, "goblin");
                CreateRequest(other, "goblin");
                CreateRequest(other, "torch");
                Tick(_world);
                Tick(other);

                DotsModules.UninstallAll(_world);

                Assert.AreEqual(0, _registry.Count);
                Assert.AreEqual(2, otherRegistry.Count, "the other world's views are untouched");
                Assert.AreEqual(2, otherProvider.LiveInstances);
                Assert.IsTrue(DotsViewBootstrap.IsInstalled(other));
                Assert.IsTrue(DotsModules.IsInstalled(other, DotsViewBootstrap.ModuleName));
                Assert.IsFalse(DotsViewBootstrap.IsInstalled(_world));

                Tick(other);
                Assert.AreEqual(2, otherRegistry.Count, "and keep working");
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }

        [Test]
        public void RepeatedFullCycles_LeaveNoViews_NoRecords_AndReleaseTheOverlayBuffer()
        {
            // The overlay buffer is the one native container the view module allocates. It is
            // observable after the world is gone because the buffer object is managed and OnDestroy
            // nulls its list — so a world disposed without disposing it would show IsCreated == true.
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var world = new World("cycle-" + cycle);
                var provider = new SpawningViewAssetProvider();
                var registry = new EntityViewRegistry(provider);
                DotsViewBootstrap.Install(world, registry, DotsModuleScope.Session);

                var entity = CreateRequest(world, "goblin");
                world.EntityManager.AddComponentData(entity, new ViewOverlayAnchor { WorldOffset = new float3(0f, 2f, 0f) });
                Tick(world);

                ViewOverlayBuffer buffer;
                using (var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewOverlayBuffer>()))
                {
                    buffer = world.EntityManager.GetComponentObject<ViewOverlayBuffer>(query.GetSingletonEntity());
                }

                Assert.IsTrue(buffer.Entries.IsCreated);
                Assert.AreEqual(1, buffer.Count);

                DotsModules.UninstallAll(world);
                Assert.AreEqual(0, provider.LiveInstances, $"cycle {cycle}: views recycled before disposal");
                Assert.AreEqual(0, DotsModules.Installed(world).Count);

                world.Dispose();
                Assert.IsFalse(buffer.Entries.IsCreated, $"cycle {cycle}: the overlay list was disposed with the world");
            }

            var strays = System.Array.FindAll(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None), g => g.name == "goblin");
            Assert.AreEqual(0, strays.Length, "no view GameObject outlived its world");
        }
    }
}
