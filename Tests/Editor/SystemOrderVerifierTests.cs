using System.Linq;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The layout tests check what the attributes <i>say</i>; these check what Entities <i>did</i>
    /// after the bootstraps ran — the order in the master update list, subgroups included.
    /// </summary>
    public sealed class SystemOrderVerifierTests
    {
        private World _world;

        [SetUp]
        public void SetUp() => _world = new World("SystemOrderVerifierTests");

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated) _world.Dispose();
        }

        [Test]
        public void FullInstall_ProducesNoViolations()
        {
            DotsViewBootstrap.InstallSystems(_world);
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            CameraFollowBootstrap.InstallSystems(_world);

            var violations = SystemOrderVerifier.Verify(_world);

            Assert.IsEmpty(violations, string.Join("\n", violations));
            Assert.DoesNotThrow(() => SystemOrderVerifier.Assert(_world));
        }

        [Test]
        public void ViewGroup_MembersRun_InterpolationLifecycleSync_ThenCamera()
        {
            DotsViewBootstrap.InstallSystems(_world);
            CameraFollowBootstrap.InstallSystems(_world);

            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewSystemGroup>());

            CollectionAssert.AreEqual(
                new[] { typeof(ViewInterpolationGroup), typeof(ViewLifecycleGroup), typeof(ViewTransformSyncGroup), typeof(CameraFollowSystem) },
                members);
        }

        [Test]
        public void Subgroups_AreOrdered_DespawnBeforeSpawn_SyncBeforeOverlay()
        {
            DotsViewBootstrap.InstallSystems(_world);

            var lifecycle = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewLifecycleGroup>());
            var sync = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewTransformSyncGroup>());

            CollectionAssert.AreEqual(new[] { typeof(EntityViewDespawnSystem), typeof(EntityViewSpawnSystem) }, lifecycle);
            CollectionAssert.AreEqual(new[] { typeof(EntityViewTransformSyncSystem), typeof(ViewOverlaySystem) }, sync);
        }

        [Test]
        public void GameplayGroup_IsMovementLifecycleThenCommandBuffer_AfterBothBootstraps()
        {
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            DotsViewBootstrap.InstallSystems(_world);

            var gameplay = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<GameplaySystemGroup>());

            CollectionAssert.AreEqual(
                new[] { typeof(MovementSystemGroup), typeof(LifecycleSystemGroup), typeof(DotsEndSimulationCommandBufferSystem) },
                gameplay);
        }

        [Test]
        public void ASystemAddedToTheWrongGroup_IsReported_WithTheGroupItDeclares()
        {
            DotsViewBootstrap.InstallSystems(_world);

            // The exact mistake DotsNetcodeBootstrap once made: the attribute names a subgroup, the
            // add goes to the parent. Sorted, so Entities has had every chance to complain itself.
            var view = _world.GetExistingSystemManaged<ViewSystemGroup>();
            var stray = _world.CreateSystemManaged<StrayLifecycleSystem>();
            view.AddSystemToUpdateList(stray);
            view.SortSystems();

            var violations = SystemOrderVerifier.Verify(_world);

            Assert.AreEqual(1, violations.Count, string.Join("\n", violations));
            StringAssert.Contains(nameof(StrayLifecycleSystem), violations[0]);
            StringAssert.Contains(nameof(ViewLifecycleGroup), violations[0]);
            StringAssert.Contains(nameof(ViewSystemGroup), violations[0]);
            Assert.Throws<System.InvalidOperationException>(() => SystemOrderVerifier.Assert(_world));
        }

        [Test]
        public void AnUnsortedGroup_IsReported_WhenInsertionOrderContradictsTheAttributes()
        {
            // Spawn declares UpdateAfter(Despawn). Added the wrong way round and never sorted, the
            // master list runs them in insertion order — which is the bug SortSystems exists to fix.
            var presentation = _world.GetOrCreateSystemManaged<PresentationSystemGroup>();
            var view = _world.GetOrCreateSystemManaged<ViewSystemGroup>();
            var lifecycle = _world.GetOrCreateSystemManaged<ViewLifecycleGroup>();
            presentation.AddSystemToUpdateList(view);
            view.AddSystemToUpdateList(lifecycle);
            lifecycle.AddSystemToUpdateList(_world.GetOrCreateSystem<EntityViewSpawnSystem>());
            lifecycle.AddSystemToUpdateList(_world.GetOrCreateSystem<EntityViewDespawnSystem>());

            var violations = SystemOrderVerifier.Verify(_world);

            Assert.IsTrue(violations.Any(v => v.Contains(nameof(EntityViewSpawnSystem)) && v.Contains("SortSystems")),
                string.Join("\n", violations));

            presentation.SortSystems();
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world), "sorting resolves it");
        }

        [Test]
        public void Contains_FindsSystemsNestedInSubgroups()
        {
            DotsViewBootstrap.InstallSystems(_world);
            var presentation = _world.GetExistingSystemManaged<PresentationSystemGroup>();

            Assert.IsTrue(SystemOrderVerifier.Contains(_world, presentation, typeof(EntityViewSpawnSystem)));
            Assert.IsTrue(SystemOrderVerifier.Contains(_world, presentation, typeof(ViewOverlaySystem)));
            Assert.IsFalse(SystemOrderVerifier.Contains(_world, presentation, typeof(CameraFollowSystem)), "not installed");
        }

        /// <summary>Declares the lifecycle subgroup; the test adds it to the parent instead.</summary>
        [DisableAutoCreation]
        [UpdateInGroup(typeof(ViewLifecycleGroup))]
        private sealed class StrayLifecycleSystem : SystemBase
        {
            protected override void OnUpdate() { }
        }
    }
}
