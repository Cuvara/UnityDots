using System;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Physics;
using Cuvara.DOTS.Simulation;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Physics
{
    /// <summary>Body composition per kind, validation, world membership, and the one-integrator rule.</summary>
    public sealed class PhysicsBodyFactoryTests
    {
        private World _world;
        private EntityManager _em;
        private ColliderLibrary _library;

        [SetUp]
        public void SetUp()
        {
            _world = new World("PhysicsBodyFactoryTests");
            _em = _world.EntityManager;
            _library = new ColliderLibrary();
        }

        [TearDown]
        public void TearDown()
        {
            DotsModules.UninstallAll(_world);
            _world.Dispose();
            _library.Dispose();
        }

        [Test]
        public void DynamicBody_HasColliderVelocityMassTagAndWorldIndex()
        {
            var entity = _em.CreateEntity();
            PhysicsBodyFactory.AddDynamicBody(_em, entity, _library, ColliderShape.Sphere, new float3(0.5f), mass: 2f);

            Assert.IsTrue(_em.HasComponent<PhysicsCollider>(entity));
            Assert.IsTrue(_em.HasComponent<PhysicsVelocity>(entity));
            Assert.IsTrue(_em.HasComponent<PhysicsMass>(entity));
            Assert.AreEqual(0.5f, _em.GetComponentData<PhysicsMass>(entity).InverseMass, 1e-5f);
            Assert.IsTrue(_em.HasComponent<PhysicsDrivenMovement>(entity), "Unity.Physics is the integrator");
            Assert.IsTrue(_em.HasComponent<PhysicsWorldIndex>(entity), "without this BuildPhysicsWorld never sees the body");
            Assert.IsTrue(_em.HasComponent<LocalTransform>(entity));
            Assert.IsTrue(_em.HasComponent<LocalToWorld>(entity));
            Assert.DoesNotThrow(() => PhysicsBodyValidation.AssertSingleIntegrator(_em, entity));
        }

        [Test]
        public void StaticBody_HasNoVelocity_NoMass_NoTag()
        {
            var entity = _em.CreateEntity();
            PhysicsBodyFactory.AddStaticBody(_em, entity, _library, ColliderShape.Box, new float3(2f, 1f, 2f));

            Assert.IsTrue(_em.HasComponent<PhysicsCollider>(entity));
            Assert.IsFalse(_em.HasComponent<PhysicsVelocity>(entity));
            Assert.IsFalse(_em.HasComponent<PhysicsMass>(entity));
            Assert.IsFalse(_em.HasComponent<PhysicsDrivenMovement>(entity), "nobody moves a static body");
            Assert.IsTrue(_em.HasComponent<PhysicsWorldIndex>(entity));
            Assert.DoesNotThrow(() => PhysicsBodyValidation.AssertSingleIntegrator(_em, entity));
        }

        [Test]
        public void KinematicBody_HasVelocity_InfiniteMass_AndTag()
        {
            var entity = _em.CreateEntity();
            PhysicsBodyFactory.AddKinematicBody(_em, entity, _library, ColliderShape.Capsule, new float3(0.3f, 1.8f, 0f));

            Assert.IsTrue(_em.HasComponent<PhysicsVelocity>(entity));
            Assert.AreEqual(0f, _em.GetComponentData<PhysicsMass>(entity).InverseMass, "kinematic: infinite mass, ignores forces");
            Assert.IsTrue(_em.HasComponent<PhysicsDrivenMovement>(entity));
        }

        [Test]
        public void LibraryBodies_ShareOneBlob_AndTheLibraryOwnsIt()
        {
            var a = _em.CreateEntity();
            var b = _em.CreateEntity();
            PhysicsBodyFactory.AddDynamicBody(_em, a, _library, ColliderShape.Sphere, new float3(0.5f));
            PhysicsBodyFactory.AddDynamicBody(_em, b, _library, ColliderShape.Sphere, new float3(0.5f));

            Assert.IsTrue(_em.GetComponentData<PhysicsCollider>(a).Value.Equals(_em.GetComponentData<PhysicsCollider>(b).Value));
            Assert.AreEqual(1, _library.Count);
            Assert.AreEqual(2, _library.TotalLeases);
        }

        [Test]
        public void CallerOwnedCollider_IsUsedAsIs()
        {
            var collider = PhysicsBodyFactory.CreateCollider(ColliderShape.Cylinder, new float3(0.5f, 2f, 0f));
            try
            {
                var entity = _em.CreateEntity();
                PhysicsBodyFactory.AddStaticBody(_em, entity, collider);
                Assert.IsTrue(_em.GetComponentData<PhysicsCollider>(entity).Value.Equals(collider));
                Assert.AreEqual(0, _library.Count, "the library was not involved");
            }
            finally
            {
                collider.Dispose();
            }
        }

        [Test]
        public void ExistingTransform_IsKept()
        {
            var entity = _em.CreateEntity();
            _em.AddComponentData(entity, LocalTransform.FromPosition(new float3(3f, 4f, 5f)));
            PhysicsBodyFactory.AddStaticBody(_em, entity, _library, ColliderShape.Sphere, new float3(1f));

            Assert.AreEqual(new float3(3f, 4f, 5f), _em.GetComponentData<LocalTransform>(entity).Position);
        }

        [Test]
        public void Validation_RejectsBadDimensionsMassAndFilter_NamingTheValue()
        {
            var entity = _em.CreateEntity();

            var radius = Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Sphere, new float3(0f)));
            StringAssert.Contains("radius", radius.Message);
            Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Box, new float3(1f, -1f, 1f)));
            Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Box, new float3(1f, float.NaN, 1f)));
            var capsule = Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Capsule, new float3(1f, 1f, 0f)));
            StringAssert.Contains("twice the radius", capsule.Message);
            Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Cylinder, new float3(1f, 0f, 0f)));

            var mass = Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.AddDynamicBody(_em, entity, _library, ColliderShape.Sphere, new float3(1f), mass: 0f));
            StringAssert.Contains("mass", mass.Message);
            Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.AddDynamicBody(_em, entity, _library, ColliderShape.Sphere, new float3(1f), mass: float.PositiveInfinity));

            var filter = Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.CreateCollider(ColliderShape.Sphere, new float3(1f), CollisionFilter.Zero));
            StringAssert.Contains("collide with nothing", filter.Message);

            Assert.Throws<ArgumentException>(() => PhysicsBodyFactory.AddStaticBody(_em, entity, default(BlobAssetReference<Collider>)));
            Assert.IsFalse(_em.HasComponent<PhysicsCollider>(entity), "nothing half-added");
            Assert.AreEqual(0, _library.Count, "a rejected mass did not leak a lease");
        }

        [Test]
        public void SingleIntegrator_IsAsserted_BothWays()
        {
            var velocityOnly = _em.CreateEntity();
            _em.AddComponentData(velocityOnly, new PhysicsVelocity());
            var error = Assert.Throws<InvalidOperationException>(() => PhysicsBodyValidation.AssertSingleIntegrator(_em, velocityOnly));
            StringAssert.Contains("PhysicsDrivenMovement", error.Message);

            var tagOnly = _em.CreateEntity();
            _em.AddComponent<PhysicsDrivenMovement>(tagOnly);
            Assert.Throws<InvalidOperationException>(() => PhysicsBodyValidation.AssertSingleIntegrator(_em, tagOnly));
        }

        [Test]
        public void DirectMovers_SkipAPhysicsDrivenBody_SoItIsIntegratedOnce()
        {
            DotsSimulationBootstrap.InstallSimulationSystems(_world);

            // Two entities with identical MoveData: one plain, one a dynamic body.
            var plain = _em.CreateEntity();
            _em.AddComponentData(plain, LocalTransform.Identity);
            _em.AddComponentData(plain, new MoveData { Velocity = new float3(10f, 0f, 0f), BoundsMin = new float3(-100f), BoundsMax = new float3(100f) });

            var body = _em.CreateEntity();
            _em.AddComponentData(body, LocalTransform.Identity);
            _em.AddComponentData(body, new MoveData { Velocity = new float3(10f, 0f, 0f), BoundsMin = new float3(-100f), BoundsMax = new float3(100f) });
            _em.AddComponentData(body, new MoveToward { Target = new float3(50f, 0f, 0f), Speed = 10f, StopDistance = 0f });
            PhysicsBodyFactory.AddDynamicBody(_em, body, _library, ColliderShape.Sphere, new float3(0.5f));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no PhysicsSystemGroup"));
            PhysicsMovementBootstrap.Install(_world);

            _world.SetTime(new Unity.Core.TimeData(0.1, 0.1f));
            _world.GetExistingSystemManaged<SimulationSystemGroup>().Update();

            Assert.AreEqual(1f, _em.GetComponentData<LocalTransform>(plain).Position.x, 1e-4f, "the direct mover moved the plain entity");
            Assert.AreEqual(0f, _em.GetComponentData<LocalTransform>(body).Position.x, 1e-4f, "and left the physics body to Unity.Physics");
            Assert.AreEqual(new float3(10f, 0f, 0f), _em.GetComponentData<PhysicsVelocity>(body).Linear, "which the bridge fed");
        }

        [Test]
        public void Bridge_IgnoresAnUntaggedVelocity()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no PhysicsSystemGroup"));
            PhysicsMovementBootstrap.Install(_world);
            var entity = _em.CreateEntity();
            _em.AddComponentData(entity, new MoveData { Velocity = new float3(1f, 0f, 0f) });
            _em.AddComponentData(entity, new PhysicsVelocity());

            _world.SetTime(new Unity.Core.TimeData(0.1, 0.1f));
            _world.GetExistingSystemManaged<SimulationSystemGroup>().Update();

            Assert.AreEqual(float3.zero, _em.GetComponentData<PhysicsVelocity>(entity).Linear, "no tag, no bridge — the entity failed AssertSingleIntegrator and is nobody's");
        }
    }
}
