using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

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

        // ---- System behaviour, with a real Camera in the edit-mode scene -------------------------

        private Camera _camera;

        private Camera MakeCamera(float3 at)
        {
            var go = new GameObject("CameraFollowBootstrapTests.Camera");
            _camera = go.AddComponent<Camera>();
            _camera.transform.position = at;
            return _camera;
        }

        private Entity MakeTarget(float3 at, World world = null)
        {
            var entityManager = (world ?? _world).EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new CameraFollowTarget());
            entityManager.AddComponentData(entity, new Unity.Transforms.LocalToWorld { Value = float4x4.Translate(at) });
            return entity;
        }

        private void Tick(float dt = 1f / 60f)
        {
            _world.SetTime(new Unity.Core.TimeData(_world.Time.ElapsedTime + dt, dt));
            _world.GetExistingSystemManaged<CameraFollowSystem>().Update();
        }

        [TearDown]
        public void DestroyCamera()
        {
            if (_camera != null) UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            _camera = null;
        }

        [Test]
        public void System_WithoutACamera_DoesNotThrow_AndHolds()
        {
            // Camera.main is null in an edit-mode scene and no camera is supplied.
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());
            MakeTarget(new float3(5f, 0f, 5f));

            Assert.DoesNotThrow(() => Tick());
        }

        [Test]
        public void SuppliedCamera_IsDriven_AndFirstFrameSnaps()
        {
            var camera = MakeCamera(new float3(100f, 100f, 100f));
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 0.5f };
            CameraFollowBootstrap.Install(_world, config);
            MakeTarget(new float3(5f, 0f, 5f));

            Tick();

            Assert.AreEqual((Vector3)(new float3(5f, 0f, 5f) + config.Offset), camera.transform.position,
                "the first frame snaps: a camera left at the scene origin does not glide to the player on load");
            Assert.AreEqual(float3.zero, _world.GetExistingSystemManaged<CameraFollowSystem>().Velocity);
        }

        [Test]
        public void TwoTargets_HoldAndReport_HoldsAndReportsOnce_ThenFollowsWhenOneRemains()
        {
            var camera = MakeCamera(new float3(1f, 1f, 1f));
            var config = new CameraFollowConfig { Camera = camera };
            CameraFollowBootstrap.Install(_world, config);
            MakeTarget(new float3(5f, 0f, 5f));
            var second = MakeTarget(new float3(-5f, 0f, -5f));

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("found 2 entities tagged CameraFollowTarget"));
            Tick();
            Tick(); // reported once, not per frame — LogAssert would fail on a second unexpected error
            Assert.AreEqual((Vector3)new float3(1f, 1f, 1f), camera.transform.position, "held still");

            _world.EntityManager.DestroyEntity(second);
            Tick();
            Assert.AreEqual((Vector3)(new float3(5f, 0f, 5f) + config.Offset), camera.transform.position, "follows the one that remains");
        }

        [Test]
        public void TwoTargets_FollowLowestIndex_FollowsDeterministically_WithoutLogging()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, MultipleTargets = CameraFollowMultiTargetPolicy.FollowLowestIndex };
            CameraFollowBootstrap.Install(_world, config);
            var first = MakeTarget(new float3(5f, 0f, 5f));
            MakeTarget(new float3(-5f, 0f, -5f));

            Tick();

            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(first, _world.GetExistingSystemManaged<CameraFollowSystem>().CurrentTarget);
            Assert.AreEqual((Vector3)(new float3(5f, 0f, 5f) + config.Offset), camera.transform.position);
        }

        [Test]
        public void TargetSwitch_Snap_JumpsToTheNewTarget_Smooth_GlidesFromWhereItWas()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 0.5f, MaxSpeed = 1000f };
            CameraFollowBootstrap.Install(_world, config);
            var a = MakeTarget(new float3(0f, 0f, 0f));
            Tick();
            var atA = camera.transform.position;

            _world.EntityManager.DestroyEntity(a);
            MakeTarget(new float3(200f, 0f, 0f));
            Tick();
            Assert.AreEqual((Vector3)(new float3(200f, 0f, 0f) + config.Offset), camera.transform.position, "Snap policy: the respawn does not glide");

            config.TargetSwitch = CameraFollowSwitchPolicy.Smooth;
            _world.EntityManager.DestroyEntity(_world.GetExistingSystemManaged<CameraFollowSystem>().CurrentTarget);
            MakeTarget(new float3(0f, 0f, 0f));
            Tick();
            var afterSmoothSwitch = camera.transform.position;
            Assert.AreNotEqual(atA, afterSmoothSwitch, "Smooth policy: one frame in, it has not arrived");
            Assert.Less(afterSmoothSwitch.x, 200f + config.Offset.x, "but it is on its way");
        }

        [Test]
        public void Teleport_BeyondTeleportDistance_Snaps_AndResetsVelocity()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 0.5f, MaxSpeed = 1000f, TeleportDistance = 50f };
            CameraFollowBootstrap.Install(_world, config);
            var target = MakeTarget(float3.zero);
            Tick();
            _world.EntityManager.SetComponentData(target, new Unity.Transforms.LocalToWorld { Value = float4x4.Translate(new float3(3f, 0f, 0f)) });
            Tick();
            Assert.AreNotEqual(float3.zero, _world.GetExistingSystemManaged<CameraFollowSystem>().Velocity, "damping toward a nearby move");

            _world.EntityManager.SetComponentData(target, new Unity.Transforms.LocalToWorld { Value = float4x4.Translate(new float3(900f, 0f, 0f)) });
            Tick();

            Assert.AreEqual((Vector3)(new float3(900f, 0f, 0f) + config.Offset), camera.transform.position, "server teleport: snapped");
            Assert.AreEqual(float3.zero, _world.GetExistingSystemManaged<CameraFollowSystem>().Velocity);
        }

        [Test]
        public void ResetSmoothing_MakesTheNextFrameSnap()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 1f, MaxSpeed = 1000f };
            CameraFollowBootstrap.Install(_world, config);
            var target = MakeTarget(float3.zero);
            Tick();
            _world.EntityManager.SetComponentData(target, new Unity.Transforms.LocalToWorld { Value = float4x4.Translate(new float3(10f, 0f, 0f)) });
            Tick();
            Assert.AreNotEqual((Vector3)(new float3(10f, 0f, 0f) + config.Offset), camera.transform.position, "still damping");

            CameraFollowBootstrap.ResetSmoothing(_world); // what a reconnect calls
            Tick();

            Assert.AreEqual((Vector3)(new float3(10f, 0f, 0f) + config.Offset), camera.transform.position);
            Assert.AreEqual(float3.zero, _world.GetExistingSystemManaged<CameraFollowSystem>().Velocity);
            Assert.DoesNotThrow(() => CameraFollowBootstrap.ResetSmoothing(null));
        }

        [Test]
        public void ZeroDeltaTime_LeavesTheCameraExactlyWhereItWas()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 0.5f };
            CameraFollowBootstrap.Install(_world, config);
            var target = MakeTarget(float3.zero);
            Tick();
            _world.EntityManager.SetComponentData(target, new Unity.Transforms.LocalToWorld { Value = float4x4.Translate(new float3(10f, 0f, 0f)) });
            var before = camera.transform.position;
            var rotationBefore = camera.transform.rotation;

            Tick(0f);

            Assert.AreEqual(before, camera.transform.position, "a paused frame is not a jump");
            Assert.AreEqual(rotationBefore, camera.transform.rotation);
        }

        [Test]
        public void DestroyedTarget_LeavesFiniteValues_AndTheCameraWhereItWas()
        {
            var camera = MakeCamera(float3.zero);
            var config = new CameraFollowConfig { Camera = camera, SmoothTime = 0.2f };
            CameraFollowBootstrap.Install(_world, config);
            var target = MakeTarget(new float3(4f, 0f, 4f));
            Tick();
            var last = camera.transform.position;

            _world.EntityManager.DestroyEntity(target);
            for (var i = 0; i < 5; i++) Tick();

            Assert.AreEqual(last, camera.transform.position, "no target: hold");
            Assert.IsTrue(CameraFollowMath.IsFinite(camera.transform.position));
            Assert.IsTrue(CameraFollowMath.IsFinite(_world.GetExistingSystemManaged<CameraFollowSystem>().Velocity));
        }

        [Test]
        public void DestroyedSuppliedCamera_IdlesWithoutThrowing()
        {
            var camera = MakeCamera(float3.zero);
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig { Camera = camera });
            MakeTarget(float3.zero);
            Tick();

            UnityEngine.Object.DestroyImmediate(camera.gameObject);
            _camera = null;

            Assert.DoesNotThrow(() => Tick());
        }

        [Test]
        public void Install_RejectsNegativeTeleportDistance()
        {
            var error = Assert.Throws<ArgumentException>(
                () => CameraFollowBootstrap.Install(_world, new CameraFollowConfig { TeleportDistance = -1f }));
            StringAssert.Contains("TeleportDistance", error.Message);
        }

        [Test]
        public void Camera_RunsAfterInterpolationLifecycleAndSync()
        {
            DotsViewBootstrap.InstallSystems(_world);
            CameraFollowBootstrap.Install(_world, new CameraFollowConfig());

            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<ViewSystemGroup>());
            var camera = members.IndexOf(typeof(CameraFollowSystem));
            Assert.Greater(camera, members.IndexOf(typeof(ViewInterpolationGroup)), "after interpolation: sees the interpolated remote pose");
            Assert.Greater(camera, members.IndexOf(typeof(ViewLifecycleGroup)));
            Assert.Greater(camera, members.IndexOf(typeof(ViewTransformSyncGroup)), "after sync: sees what was rendered");
        }
    }
}
