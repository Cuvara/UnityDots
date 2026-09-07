using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>Install/uninstall symmetry for the simulation systems, and their order once installed.</summary>
    public sealed class DotsSimulationBootstrapTests
    {
        private World _world;

        [SetUp]
        public void SetUp() => _world = new World("DotsSimulationBootstrapTests");

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }
        }

        [Test]
        public void InstallTwice_CreatesEachSystemOnce_AndVerifies()
        {
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            var moveToward = _world.GetExistingSystem<MoveTowardSystem>();

            DotsSimulationBootstrap.InstallSimulationSystems(_world);

            Assert.AreEqual(moveToward, _world.GetExistingSystem<MoveTowardSystem>(), "same handle, not a second system");
            var movement = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>());
            var lifecycle = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<LifecycleSystemGroup>());
            Assert.AreEqual(3, movement.Count);
            Assert.AreEqual(2, lifecycle.Count);
            Assert.IsTrue(DotsSimulationBootstrap.IsInstalled(_world));
            Assert.AreEqual(2, DotsModules.InstallCount(_world, DotsSimulationBootstrap.ModuleName));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
        }

        [Test]
        public void Uninstall_DestroysTheSystems_KeepsTheGroups_AndIsSafeTwice()
        {
            DotsSimulationBootstrap.InstallSimulationSystems(_world);

            DotsSimulationBootstrap.Uninstall(_world);
            DotsSimulationBootstrap.Uninstall(_world);

            Assert.IsFalse(DotsSimulationBootstrap.IsInstalled(_world));
            Assert.AreEqual(SystemHandle.Null, _world.GetExistingSystem<SpinSystem>());
            Assert.AreEqual(SystemHandle.Null, _world.GetExistingSystem<TimeToLiveSystem>());
            Assert.IsNotNull(_world.GetExistingSystemManaged<MovementSystemGroup>(), "groups are the shared ordering surface and stay");
            Assert.IsEmpty(SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>()));
            Assert.IsFalse(DotsModules.IsInstalled(_world, DotsSimulationBootstrap.ModuleName));
            Assert.DoesNotThrow(() => _world.GetExistingSystemManaged<SimulationSystemGroup>().Update(), "an emptied group still ticks");
        }

        [Test]
        public void Uninstall_LeavesTheViewSystems_ThatShareTheGroups()
        {
            DotsViewBootstrap.InstallSystems(_world);
            DotsSimulationBootstrap.InstallSimulationSystems(_world);

            DotsSimulationBootstrap.Uninstall(_world);

            var gameplay = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<GameplaySystemGroup>());
            CollectionAssert.Contains(gameplay, typeof(DotsEndSimulationCommandBufferSystem));
            Assert.AreNotEqual(SystemHandle.Null, _world.GetExistingSystem<EntityViewSpawnSystem>());
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
        }

        [Test]
        public void Reinstall_AfterUninstall_RestoresTheFullSet()
        {
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            DotsSimulationBootstrap.Uninstall(_world);
            DotsSimulationBootstrap.InstallSimulationSystems(_world);

            Assert.IsTrue(DotsSimulationBootstrap.IsInstalled(_world));
            Assert.AreEqual(3, SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>()).Count);
            Assert.AreEqual(1, DotsModules.InstallCount(_world, DotsSimulationBootstrap.ModuleName));
        }

        [Test]
        public void TwoWorlds_AreIndependent()
        {
            var other = new World("DotsSimulationBootstrapTests.Other");
            try
            {
                DotsSimulationBootstrap.InstallSimulationSystems(_world);
                DotsSimulationBootstrap.InstallSimulationSystems(other);

                DotsSimulationBootstrap.Uninstall(_world);

                Assert.IsFalse(DotsSimulationBootstrap.IsInstalled(_world));
                Assert.IsTrue(DotsSimulationBootstrap.IsInstalled(other));
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }
    }
}
