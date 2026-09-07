using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The camera module: config validation with actionable errors, install/uninstall idempotency,
    /// ordering after install, and two-world isolation.
    /// </summary>
    public sealed class CameraFollowBootstrapTests
    {
        private World _world;

        [SetUp]
        public void SetUp() => _world = new World("CameraFollowBootstrapTests");

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }
        }

        private static int ConfigCount(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowConfig>());
            return query.CalculateEntityCount();
        }

        [Test]
        public void Install_RejectsNaNOffset_NamingTheField()
        {
            var config = new CameraFollowConfig { Offset = new float3(0f, float.NaN, 0f) };

            var error = Assert.Throws<ArgumentException>(() => CameraFollowBootstrap.Install(_world, config));

            StringAssert.Contains("Offset.y", error.Message);
            StringAssert.Contains("finite", error.Message);
            Assert.IsFalse(CameraFollowBootstrap.IsInstalled(_world), "nothing is half-installed");
            Assert.IsNull(_world.GetExistingSystemManaged<CameraFollowSystem>());
        }

        [Test]
        public void Install_RejectsNegativeSmoothTime_AndNonPositiveMaxSpeed()
        {
            var smooth = Assert.Throws<ArgumentException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig { SmoothTime = -1f }));
            StringAssert.Contains("SmoothTime", smooth.Message);
            StringAssert.Contains(">= 0", smooth.Message);

            var speed = Assert.Throws<ArgumentException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig { MaxSpeed = 0f }));
            StringAssert.Contains("MaxSpeed", speed.Message);
            StringAssert.Contains("> 0", speed.Message);

            Assert.Throws<ArgumentException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig { MaxSpeed = float.PositiveInfinity }));
            Assert.Throws<ArgumentException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig { LookAtOffset = new float3(float.NegativeInfinity) }));
        }

        [Test]
        public void Install_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => CameraFollowBootstrap.Install(null, new CameraFollowConfig()));
            Assert.Throws<ArgumentNullException>(() => CameraFollowBootstrap.Install(_world, null));
        }

        [Test]
        public void DefaultConfig_IsValid_AndInstalls_InOrderAfterTheSyncGroup()
        {
            DotsViewBootstrap.InstallSystems(_world);
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());

            Assert.IsTrue(CameraFollowBootstrap.IsInstalled(_world));
            Assert.AreEqual(1, ConfigCount(_world));
            Assert.IsTrue(DotsModules.IsInstalled(_world, CameraFollowBootstrap.ModuleName));
            Assert.IsTrue(DotsModules.TryGetScope(_world, CameraFollowBootstrap.ModuleName, out var scope));
            Assert.AreEqual(DotsModuleScope.Session, scope, "session-scoped by default");

            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewSystemGroup>());
            Assert.Greater(members.IndexOf(typeof(CameraFollowSystem)), members.IndexOf(typeof(ViewTransformSyncGroup)));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world));
        }

        [Test]
        public void InstallTwice_OneConfig_OneSystem_InstallCountTwo()
        {
            var config = new CameraFollowConfig();
            CameraFollowBootstrap.Install(_world, config);
            CameraFollowBootstrap.Install(_world, config);

            Assert.AreEqual(1, ConfigCount(_world));
            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewSystemGroup>());
            Assert.AreEqual(1, members.FindAll(t => t == typeof(CameraFollowSystem)).Count);
            Assert.AreEqual(2, DotsModules.InstallCount(_world, CameraFollowBootstrap.ModuleName));
        }

        [Test]
        public void Install_WithANewConfig_ReplacesTheReferencedInstance()
        {
            var first = new CameraFollowConfig { SmoothTime = 0.1f };
            var second = new CameraFollowConfig { SmoothTime = 0.9f };
            CameraFollowBootstrap.Install(_world, first);

            CameraFollowBootstrap.Install(_world, second);

            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowConfig>());
            var installed = _world.EntityManager.GetComponentObject<CameraFollowConfig>(query.GetSingletonEntity());
            Assert.AreSame(second, installed);
            Assert.AreEqual(1, ConfigCount(_world));
        }

        [Test]
        public void Uninstall_RemovesConfigAndSystem_AndIsSafeTwice()
        {
            DotsViewBootstrap.InstallSystems(_world);
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());

            CameraFollowBootstrap.Uninstall(_world);
            CameraFollowBootstrap.Uninstall(_world);

            Assert.IsFalse(CameraFollowBootstrap.IsInstalled(_world));
            Assert.AreEqual(0, ConfigCount(_world));
            Assert.IsNull(_world.GetExistingSystemManaged<CameraFollowSystem>());
            Assert.IsFalse(SystemOrderVerifier.Contains(_world, _world.GetExistingSystemManaged<PresentationSystemGroup>(), typeof(CameraFollowSystem)));
            Assert.IsFalse(DotsModules.IsInstalled(_world, CameraFollowBootstrap.ModuleName));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world), "the rest of the view tree is untouched");
        }

        [Test]
        public void Uninstall_OnAWorldThatNeverHadIt_IsANoOp()
        {
            Assert.DoesNotThrow(() => CameraFollowBootstrap.Uninstall(_world));
            Assert.DoesNotThrow(() => CameraFollowBootstrap.Uninstall(null));
        }

        [Test]
        public void Reinstall_AfterUninstall_Works()
        {
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());
            CameraFollowBootstrap.Uninstall(_world);
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());

            Assert.IsTrue(CameraFollowBootstrap.IsInstalled(_world));
            Assert.IsNotNull(_world.GetExistingSystemManaged<CameraFollowSystem>());
            Assert.AreEqual(1, DotsModules.InstallCount(_world, CameraFollowBootstrap.ModuleName), "a fresh installation, not a continuation");
        }

        [Test]
        public void ScopeConflict_IsRefused()
        {
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig(), DotsModuleScope.Session);

            Assert.Throws<InvalidOperationException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig(), DotsModuleScope.Root));
        }

        [Test]
        public void TwoWorlds_UninstallingOne_LeavesTheOther()
        {
            var other = new World("CameraFollowBootstrapTests.Other");
            try
            {
                CameraFollowBootstrap.Install(_world, new CameraFollowConfig());
                CameraFollowBootstrap.Install(other, new CameraFollowConfig());

                CameraFollowBootstrap.Uninstall(_world);

                Assert.IsFalse(CameraFollowBootstrap.IsInstalled(_world));
                Assert.IsTrue(CameraFollowBootstrap.IsInstalled(other));
                Assert.IsNotNull(other.GetExistingSystemManaged<CameraFollowSystem>());
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }

        [Test]
        public void System_WithoutACamera_OrWithTwoTargets_DoesNotThrow()
        {
            // Camera.main is null in an edit-mode test; the system must return, not throw. Two
            // targets is the consumer error the system reports once instead of throwing on
            // GetSingleton every frame.
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());
            var entityManager = _world.EntityManager;
            for (var i = 0; i < 2; i++)
            {
                var entity = entityManager.CreateEntity();
                entityManager.AddComponentData(entity, new CameraFollowTarget());
                entityManager.AddComponentData(entity, new Unity.Transforms.LocalToWorld { Value = float4x4.identity });
            }

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Assert.DoesNotThrow(() => _world.GetExistingSystemManaged<CameraFollowSystem>().Update());
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        }
    }
}
