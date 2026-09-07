using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The minimap module: install/uninstall lifecycle and the buffer's "never stale" contract
    /// across spawn, move, despawn, empty world and scene-reload-shaped teardown.
    /// </summary>
    public sealed class MinimapModuleTests
    {
        private World _world;
        private SpawningViewAssetProvider _provider;
        private EntityViewRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _world = new World("MinimapModuleTests");
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

        /// <summary>One presentation pass through the public group.</summary>
        private void Tick() => _world.GetExistingSystemManaged<ViewSystemGroup>().Update();

        private Entity Mark(float3 at, int category = 0, bool isLocal = false, string viewKey = null)
        {
            var entityManager = _world.EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, LocalTransform.FromPosition(at));
            entityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(at) });
            entityManager.AddComponentData(entity, new MinimapMarker { Category = category, IsLocal = isLocal });
            if (viewKey != null) entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = viewKey });
            return entity;
        }

        private void Move(Entity entity, float3 to)
        {
            _world.EntityManager.SetComponentData(entity, LocalTransform.FromPosition(to));
            _world.EntityManager.SetComponentData(entity, new LocalToWorld { Value = float4x4.Translate(to) });
        }

        private static MinimapEntry Find(MinimapBuffer buffer, Entity entity)
        {
            for (var i = 0; i < buffer.Entries.Length; i++)
            {
                if (buffer.Entries[i].Entity == entity) return buffer.Entries[i];
            }

            Assert.Fail($"{entity} is not in the minimap buffer");
            return default;
        }

        // ---- lifecycle -------------------------------------------------------------------------

        [Test]
        public void Install_PublishesBuffer_CreatesSystem_InOrderAfterTheSync_SessionScoped()
        {
            DotsViewBootstrap.Install(_world, _registry);
            MinimapBootstrap.Install(_world, MinimapPlane.XZ);

            Assert.IsTrue(MinimapBootstrap.IsInstalled(_world));
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            Assert.IsNotNull(buffer);
            Assert.IsTrue(buffer.Entries.IsCreated);
            Assert.AreEqual(MinimapPlane.XZ, buffer.Plane);
            Assert.IsTrue(DotsModules.TryGetScope(_world, MinimapBootstrap.ModuleName, out var scope));
            Assert.AreEqual(DotsModuleScope.Session, scope);

            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewTransformSyncGroup>());
            Assert.Greater(members.IndexOf(typeof(MinimapDataSystem)), members.IndexOf(typeof(EntityViewTransformSyncSystem)),
                "reads the LocalToWorld the views were positioned from");
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
        }

        [Test]
        public void InstallTwice_OneBuffer_OneSystem_SameAllocation_PlaneFollowsTheNewestCall()
        {
            MinimapBootstrap.Install(_world, MinimapPlane.XZ);
            var first = MinimapBootstrap.InstalledBuffer(_world);
            MinimapBootstrap.Install(_world, MinimapPlane.XY);

            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MinimapBuffer>());
            Assert.AreEqual(1, query.CalculateEntityCount());
            Assert.AreSame(first, MinimapBootstrap.InstalledBuffer(_world), "a consumer's reference stays valid");
            Assert.AreEqual(MinimapPlane.XY, first.Plane);
            Assert.AreEqual(2, DotsModules.InstallCount(_world, MinimapBootstrap.ModuleName));

            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewTransformSyncGroup>());
            Assert.AreEqual(1, members.FindAll(t => t == typeof(MinimapDataSystem)).Count);
        }

        [Test]
        public void Install_RejectsNonPositiveCapacity_AndNullWorld()
        {
            Assert.Throws<ArgumentException>(() => MinimapBootstrap.Install(_world, initialCapacity: 0));
            Assert.IsFalse(MinimapBootstrap.IsInstalled(_world), "nothing half-installed");
            Assert.Throws<ArgumentNullException>(() => MinimapBootstrap.Install(null));
        }

        [Test]
        public void Uninstall_ReleasesTheList_RemovesSingletonAndSystem_AndIsSafeTwice()
        {
            DotsViewBootstrap.Install(_world, _registry);
            MinimapBootstrap.Install(_world);
            Mark(new float3(1f, 0f, 2f));
            Tick();
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            Assert.AreEqual(1, buffer.Count);

            MinimapBootstrap.Uninstall(_world);
            MinimapBootstrap.Uninstall(_world);

            Assert.IsFalse(buffer.Entries.IsCreated, "the native list is released, not leaked");
            Assert.AreEqual(0, buffer.Count, "a consumer still holding the reference reads empty");
            Assert.IsFalse(MinimapBootstrap.IsInstalled(_world));
            Assert.AreEqual(SystemHandle.Null, _world.GetExistingSystem<MinimapDataSystem>());
            Assert.IsFalse(DotsModules.IsInstalled(_world, MinimapBootstrap.ModuleName));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world), "the rest of the view tree is untouched");
            Assert.IsTrue(_world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MinimapMarker>()).CalculateEntityCount() == 1,
                "markers are the owner's and stay");
        }

        [Test]
        public void Uninstall_OnAWorldThatNeverHadIt_IsANoOp()
        {
            Assert.DoesNotThrow(() => MinimapBootstrap.Uninstall(_world));
            Assert.DoesNotThrow(() => MinimapBootstrap.Uninstall(null));
        }

        [Test]
        public void Reinstall_AfterUninstall_AllocatesAgain_AndProducesAgain()
        {
            MinimapBootstrap.Install(_world);
            MinimapBootstrap.Uninstall(_world);
            MinimapBootstrap.Install(_world);
            Mark(float3.zero);

            Tick();

            Assert.AreEqual(1, MinimapBootstrap.InstalledBuffer(_world).Count);
            Assert.AreEqual(1, DotsModules.InstallCount(_world, MinimapBootstrap.ModuleName));
        }

        [Test]
        public void WorldDisposal_WithoutUninstall_ReleasesTheListThroughTheSystem()
        {
            MinimapBootstrap.Install(_world);
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            Assert.IsTrue(buffer.Entries.IsCreated);

            _world.Dispose();

            Assert.IsFalse(buffer.Entries.IsCreated, "OnDestroy is the second release path");
        }

        [Test]
        public void UninstallAll_ThenDispose_LeavesNoNativeContainer()
        {
            // The scene-reload shape MODULE-LIFECYCLE.md prescribes.
            DotsViewBootstrap.Install(_world, _registry);
            MinimapBootstrap.Install(_world);
            Mark(float3.zero, viewKey: "goblin");
            Tick();
            var buffer = MinimapBootstrap.InstalledBuffer(_world);

            DotsModules.UninstallAll(_world);
            _world.Dispose();

            Assert.IsFalse(buffer.Entries.IsCreated);
            Assert.AreEqual(0, _provider.LiveInstances);
        }

        [Test]
        public void TwoWorlds_UninstallingOne_LeavesTheOther()
        {
            var other = new World("MinimapModuleTests.Other");
            try
            {
                MinimapBootstrap.Install(_world);
                MinimapBootstrap.Install(other);

                MinimapBootstrap.Uninstall(_world);

                Assert.IsFalse(MinimapBootstrap.IsInstalled(_world));
                Assert.IsTrue(MinimapBootstrap.IsInstalled(other));
                Assert.IsTrue(MinimapBootstrap.InstalledBuffer(other).Entries.IsCreated);
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }

        [Test]
        public void ScopeConflict_IsRefused()
        {
            MinimapBootstrap.Install(_world, scope: DotsModuleScope.Session);
            Assert.Throws<InvalidOperationException>(() => MinimapBootstrap.Install(_world, scope: DotsModuleScope.Root));
        }

        // ---- data contract ---------------------------------------------------------------------

        [Test]
        public void Spawn_Move_Despawn_KeepTheBufferConsistentWithTheEntitySet()
        {
            DotsViewBootstrap.Install(_world, _registry);
            MinimapBootstrap.Install(_world);
            var buffer = MinimapBootstrap.InstalledBuffer(_world);

            var a = Mark(new float3(1f, 5f, 2f), category: 7, viewKey: "goblin");
            var b = Mark(new float3(-3f, 0f, 4f), category: 2, isLocal: true);
            Tick();

            Assert.AreEqual(2, buffer.Count);
            var entryA = Find(buffer, a);
            Assert.AreEqual(new float2(1f, 2f), entryA.Position, "XZ plane: y is dropped");
            Assert.AreEqual(7, entryA.Category);
            Assert.IsFalse(entryA.IsLocal);
            Assert.AreNotEqual(0, entryA.ViewId, "it has a view, so the handle is carried");
            Assert.AreEqual(_world.EntityManager.GetComponentData<EntityViewLink>(a).ViewId, entryA.ViewId);
            Assert.AreEqual(-1f, entryA.HealthFraction);

            var entryB = Find(buffer, b);
            Assert.IsTrue(entryB.IsLocal);
            Assert.AreEqual(0, entryB.ViewId, "no view yet is still on the map");

            Move(a, new float3(10f, 0f, 20f));
            Tick();
            Assert.AreEqual(new float2(10f, 20f), Find(buffer, a).Position);

            _world.EntityManager.DestroyEntity(b);
            Tick();
            Assert.AreEqual(1, buffer.Count, "the despawned entity is gone the same frame");
            Assert.AreEqual(a, buffer.Entries[0].Entity);
        }

        [Test]
        public void LastEntityGone_ClearsTheBuffer_ThatFrame()
        {
            // The stale-marker regression: a system that requires a non-empty query stops updating
            // when the set empties and leaves the final entries behind.
            MinimapBootstrap.Install(_world);
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            var only = Mark(float3.zero);
            Tick();
            Assert.AreEqual(1, buffer.Count);
            var version = buffer.Version;

            _world.EntityManager.DestroyEntity(only);
            Tick();

            Assert.AreEqual(0, buffer.Count, "no stale marker");
            Assert.Greater(buffer.Version, version, "and the consumer can tell it was refreshed to empty");
        }

        [Test]
        public void EmptyWorld_Ticks_ReadZero_AndBumpVersion()
        {
            MinimapBootstrap.Install(_world);
            var buffer = MinimapBootstrap.InstalledBuffer(_world);

            Tick();
            Tick();

            Assert.AreEqual(0, buffer.Count);
            Assert.AreEqual(2u, buffer.Version);
        }

        [Test]
        public void MarkerRemoved_DropsTheEntry_EntityStays()
        {
            MinimapBootstrap.Install(_world);
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            var entity = Mark(float3.zero);
            Tick();

            _world.EntityManager.RemoveComponent<MinimapMarker>(entity);
            Tick();

            Assert.AreEqual(0, buffer.Count);
            Assert.IsTrue(_world.EntityManager.Exists(entity), "the map does not own the entity");
        }

        [Test]
        public void XYPlane_TakesXAndY()
        {
            MinimapBootstrap.Install(_world, MinimapPlane.XY);
            var entity = Mark(new float3(1f, 2f, 3f));
            Tick();

            Assert.AreEqual(new float2(1f, 2f), Find(MinimapBootstrap.InstalledBuffer(_world), entity).Position);
        }

        [Test]
        public void HealthFraction_ComesFromTheSimulationHealth_WhenPresent()
        {
            MinimapBootstrap.Install(_world);
            var hurt = Mark(float3.zero);
            _world.EntityManager.AddComponentData(hurt, new Health { Current = 25, Max = 100 });
            var zeroMax = Mark(float3.zero);
            _world.EntityManager.AddComponentData(zeroMax, new Health { Current = 5, Max = 0 });
            var over = Mark(float3.zero);
            _world.EntityManager.AddComponentData(over, new Health { Current = 150, Max = 100 });
            Tick();

            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            Assert.AreEqual(0.25f, Find(buffer, hurt).HealthFraction, 1e-5f);
            Assert.AreEqual(-1f, Find(buffer, zeroMax).HealthFraction, "no usable maximum means no data");
            Assert.AreEqual(1f, Find(buffer, over).HealthFraction, 1e-5f, "clamped");
        }

        [Test]
        public void ManyEntities_GrowTheListBeyondInitialCapacity()
        {
            MinimapBootstrap.Install(_world, initialCapacity: 2);
            for (var i = 0; i < 50; i++) Mark(new float3(i, 0f, 0f));

            Tick();

            Assert.AreEqual(50, MinimapBootstrap.InstalledBuffer(_world).Count);
        }

        [Test]
        public void Producer_IsIndependentOfTheViewModule()
        {
            // No DotsViewBootstrap.Install at all: entities without views, no registry, still mapped.
            MinimapBootstrap.Install(_world);
            Mark(new float3(2f, 0f, 2f));

            Tick();

            Assert.AreEqual(1, MinimapBootstrap.InstalledBuffer(_world).Count);
            Assert.AreEqual(0, MinimapBootstrap.InstalledBuffer(_world).Entries[0].ViewId);
        }

        [Test]
        public void Layout_SystemIsInternal_NotAutoCreated_InTheSyncGroup()
        {
            var type = typeof(MinimapDataSystem);
            Assert.IsFalse(type.IsPublic);
            Assert.IsNotEmpty(type.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false));
            var group = (UpdateInGroupAttribute)type.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.AreEqual(typeof(ViewTransformSyncGroup), group.GroupType);
        }
    }
}
