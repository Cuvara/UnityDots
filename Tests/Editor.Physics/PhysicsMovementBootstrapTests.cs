using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Physics;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Physics.Systems;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Physics
{
    /// <summary>
    /// The physics-movement module: install/uninstall idempotency, its place in the movement group,
    /// the missing-pipeline precondition, and two-world isolation. Compiled only with
    /// <c>com.unity.physics</c> present — the same gate as the assembly under test.
    /// </summary>
    public sealed class PhysicsMovementBootstrapTests
    {
        private World _world;

        [SetUp]
        public void SetUp() => _world = new World("PhysicsMovementBootstrapTests");

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
        public void Install_WithoutAPhysicsPipeline_WarnsByDefault_AndThrowsWhenRequired()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no PhysicsSystemGroup"));
            PhysicsMovementBootstrap.Install(_world);
            Assert.IsTrue(PhysicsMovementBootstrap.IsInstalled(_world));

            var other = new World("PhysicsMovementBootstrapTests.Strict");
            try
            {
                var error = Assert.Throws<InvalidOperationException>(
                    () => PhysicsMovementBootstrap.Install(other, requirePhysicsPipeline: true));
                StringAssert.Contains(nameof(PhysicsSystemGroup), error.Message);
                Assert.IsFalse(PhysicsMovementBootstrap.IsInstalled(other), "nothing is half-installed");
            }
            finally
            {
                other.Dispose();
            }
        }

        [Test]
        public void Install_WithAPipeline_IsSilent_AndOrdersInsideTheMovementGroup()
        {
            _world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            DotsViewBootstrap.InstallSystems(_world);

            LogAssert.NoUnexpectedReceived();
            PhysicsMovementBootstrap.Install(_world);

            var movement = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>());
            CollectionAssert.Contains(movement, typeof(PhysicsMovementBridge));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
            Assert.IsTrue(DotsModules.TryGetScope(_world, PhysicsMovementBootstrap.ModuleName, out var scope));
            Assert.AreEqual(DotsModuleScope.Session, scope);
        }

        [Test]
        public void InstallTwice_OneBridge()
        {
            _world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
            PhysicsMovementBootstrap.Install(_world);
            var handle = _world.GetExistingSystem<PhysicsMovementBridge>();

            PhysicsMovementBootstrap.Install(_world);

            Assert.AreEqual(handle, _world.GetExistingSystem<PhysicsMovementBridge>());
            var movement = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>());
            Assert.AreEqual(1, movement.FindAll(t => t == typeof(PhysicsMovementBridge)).Count);
            Assert.AreEqual(2, DotsModules.InstallCount(_world, PhysicsMovementBootstrap.ModuleName));
        }

        [Test]
        public void Uninstall_DestroysTheBridge_KeepsTheGroup_AndIsSafeTwice()
        {
            _world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
            DotsSimulationBootstrap.InstallSimulationSystems(_world);
            PhysicsMovementBootstrap.Install(_world);

            PhysicsMovementBootstrap.Uninstall(_world);
            PhysicsMovementBootstrap.Uninstall(_world);

            Assert.IsFalse(PhysicsMovementBootstrap.IsInstalled(_world));
            Assert.AreEqual(SystemHandle.Null, _world.GetExistingSystem<PhysicsMovementBridge>());
            Assert.IsFalse(DotsModules.IsInstalled(_world, PhysicsMovementBootstrap.ModuleName));
            Assert.IsTrue(DotsSimulationBootstrap.IsInstalled(_world), "the neighbouring simulation systems in the same group stay");
            Assert.AreEqual(3, SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<MovementSystemGroup>()).Count);
            Assert.DoesNotThrow(() => PhysicsMovementBootstrap.Uninstall(null));
        }

        [Test]
        public void SessionTeardown_ThroughDotsModules_RemovesTheBridge()
        {
            _world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
            PhysicsMovementBootstrap.Install(_world, DotsModuleScope.Session);

            DotsModules.UninstallScope(_world, DotsModuleScope.Session);

            Assert.IsFalse(PhysicsMovementBootstrap.IsInstalled(_world), "the core tore down a system it cannot name");
        }

        [Test]
        public void TwoWorlds_AreIndependent()
        {
            var other = new World("PhysicsMovementBootstrapTests.Other");
            try
            {
                _world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
                other.GetOrCreateSystemManaged<PhysicsSystemGroup>();
                PhysicsMovementBootstrap.Install(_world);
                PhysicsMovementBootstrap.Install(other);

                PhysicsMovementBootstrap.Uninstall(_world);

                Assert.IsFalse(PhysicsMovementBootstrap.IsInstalled(_world));
                Assert.IsTrue(PhysicsMovementBootstrap.IsInstalled(other));
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }
    }
}
