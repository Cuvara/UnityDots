using System;
using System.Collections.Generic;
using System.Linq;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Physics;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Physics
{
    /// <summary>
    /// The physics-events module: install/uninstall, ordering after the simulation, two-world
    /// isolation — and, with Unity.Physics' own systems stepped in a test world, real trigger and
    /// collision events arriving as enter/stay/exit.
    /// </summary>
    public sealed class PhysicsEventsBootstrapTests
    {
        private World _world;
        private ColliderLibrary _library;

        private sealed class ListPublisher<T> : IDotsPublisher<T>
        {
            public readonly List<T> Published = new List<T>();
            public void Publish(T message) => Published.Add(message);
        }

        [SetUp]
        public void SetUp()
        {
            _world = new World("PhysicsEventsBootstrapTests");
            _library = new ColliderLibrary();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }

            _library.Dispose();
        }

        /// <summary>
        /// Adds every Unity.Physics system to the world's root groups, the way the default
        /// bootstrap would, so <c>PhysicsSystemGroup</c> really builds, steps and exports.
        /// </summary>
        private static void AddPhysicsPipeline(World world)
        {
            var physicsSystems = TypeManager.GetSystems(WorldSystemFilterFlags.Default)
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("Unity.Physics"))
                .ToList();
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, physicsSystems);

            // No gravity: the bodies below must stay where they are put.
            var step = PhysicsStep.Default;
            step.Gravity = float3.zero;
            var entity = world.EntityManager.CreateEntity();
            world.EntityManager.AddComponentData(entity, step);
        }

        private void StepPhysics(float dt = 1f / 60f)
        {
            _world.SetTime(new Unity.Core.TimeData(_world.Time.ElapsedTime + dt, dt));
            _world.GetExistingSystemManaged<PhysicsSystemGroup>().Update();
        }

        private Entity Body(float3 at, bool dynamic, Material? material = null, float radius = 0.5f)
        {
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, LocalTransform.FromPosition(at));
            if (dynamic) PhysicsBodyFactory.AddDynamicBody(_world.EntityManager, entity, _library, ColliderShape.Sphere, new float3(radius), 1f, null, material);
            else PhysicsBodyFactory.AddStaticBody(_world.EntityManager, entity, _library, ColliderShape.Sphere, new float3(radius), null, material);
            return entity;
        }

        [Test]
        public void Install_WithoutAPipeline_WarnsByDefault_ThrowsWhenRequired()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no PhysicsSimulationGroup"));
            PhysicsEventsBootstrap.Install(_world);
            Assert.IsTrue(PhysicsEventsBootstrap.IsInstalled(_world));

            var strict = new World("PhysicsEventsBootstrapTests.Strict");
            try
            {
                Assert.Throws<InvalidOperationException>(() => PhysicsEventsBootstrap.Install(strict, requirePhysicsPipeline: true));
                Assert.IsFalse(PhysicsEventsBootstrap.IsInstalled(strict));
            }
            finally
            {
                strict.Dispose();
            }
        }

        [Test]
        public void InstallTwice_OneBuffer_OneCollector_OrderedAfterSimulation()
        {
            AddPhysicsPipeline(_world);
            PhysicsEventsBootstrap.Install(_world);
            PhysicsEventsBootstrap.Install(_world);

            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PhysicsEventBuffer>());
            Assert.AreEqual(1, query.CalculateEntityCount());
            var members = SystemOrderVerifier.MembersInUpdateOrder(_world, _world.GetExistingSystemManaged<PhysicsSystemGroup>());
            Assert.AreEqual(1, members.FindAll(t => t == typeof(PhysicsEventCollectorSystem)).Count);
            Assert.Greater(members.IndexOf(typeof(PhysicsEventCollectorSystem)), members.IndexOf(typeof(PhysicsSimulationGroup)));
            Assert.AreEqual(2, DotsModules.InstallCount(_world, PhysicsEventsBootstrap.ModuleName));
            Assert.IsEmpty(SystemOrderVerifier.Verify(_world, _world.GetExistingSystemManaged<PhysicsSystemGroup>()));
        }

        [Test]
        public void Uninstall_RemovesBufferAndCollector_AndIsSafeTwice()
        {
            AddPhysicsPipeline(_world);
            PhysicsEventsBootstrap.Install(_world);

            PhysicsEventsBootstrap.Uninstall(_world);
            PhysicsEventsBootstrap.Uninstall(_world);

            Assert.IsFalse(PhysicsEventsBootstrap.IsInstalled(_world));
            Assert.IsNull(PhysicsEventsBootstrap.Buffer(_world));
            Assert.IsNull(_world.GetExistingSystemManaged<PhysicsEventCollectorSystem>());
            Assert.IsFalse(DotsModules.IsInstalled(_world, PhysicsEventsBootstrap.ModuleName));
            Assert.DoesNotThrow(() => StepPhysics(), "the pipeline itself still steps");
        }

        [Test]
        public void TwoWorlds_AreIndependent()
        {
            var other = new World("PhysicsEventsBootstrapTests.Other");
            try
            {
                AddPhysicsPipeline(_world);
                AddPhysicsPipeline(other);
                PhysicsEventsBootstrap.Install(_world);
                PhysicsEventsBootstrap.Install(other);

                PhysicsEventsBootstrap.Uninstall(_world);

                Assert.IsFalse(PhysicsEventsBootstrap.IsInstalled(_world));
                Assert.IsTrue(PhysicsEventsBootstrap.IsInstalled(other));
            }
            finally
            {
                DotsModules.UninstallAll(other);
                other.Dispose();
            }
        }

        [Test]
        public void TriggerVolume_ReportsEnterStayExit_ThroughTheRealPipeline()
        {
            AddPhysicsPipeline(_world);
            var triggers = new ListPublisher<EntityTriggerEvent>();
            PhysicsEventsBootstrap.Install(_world, triggerPublisher: triggers);
            var buffer = PhysicsEventsBootstrap.Buffer(_world);

            var zone = Body(float3.zero, dynamic: false, PhysicsBodyFactory.TriggerMaterial(), radius: 2f);
            var mover = Body(new float3(0.5f, 0f, 0f), dynamic: true);
            var pair = new PhysicsPairKey(zone, mover);

            StepPhysics();
            Assert.AreEqual(1, buffer.Step);
            Assert.AreEqual(1, buffer.Triggers.Count, string.Join(", ", buffer.Triggers));
            Assert.AreEqual(PhysicsContactPhase.Enter, buffer.Triggers[0].Phase);
            Assert.AreEqual(pair.A, buffer.Triggers[0].EntityA, "canonical pair order");
            Assert.AreEqual(pair.B, buffer.Triggers[0].EntityB);
            Assert.IsEmpty(buffer.Collisions, "a trigger is not a collision");

            StepPhysics();
            Assert.AreEqual(PhysicsContactPhase.Stay, buffer.Triggers[0].Phase);

            _world.EntityManager.SetComponentData(mover, LocalTransform.FromPosition(new float3(50f, 0f, 0f)));
            StepPhysics();
            Assert.AreEqual(PhysicsContactPhase.Exit, buffer.Triggers[0].Phase);
            Assert.IsFalse(buffer.Triggers[0].AnyEntityDestroyed);

            StepPhysics();
            Assert.IsEmpty(buffer.Triggers, "exit once");

            CollectionAssert.AreEqual(
                new[] { PhysicsContactPhase.Enter, PhysicsContactPhase.Stay, PhysicsContactPhase.Exit },
                triggers.Published.Select(t => t.Phase).ToArray(),
                "the publisher saw the same sequence");
        }

        [Test]
        public void DestroyedEntity_ExitsWithTheFlag_AndAReusedIndexIsANewPair()
        {
            AddPhysicsPipeline(_world);
            PhysicsEventsBootstrap.Install(_world);
            var buffer = PhysicsEventsBootstrap.Buffer(_world);

            var zone = Body(float3.zero, dynamic: false, PhysicsBodyFactory.TriggerMaterial(), radius: 2f);
            var mover = Body(float3.zero, dynamic: true);
            StepPhysics();
            Assert.AreEqual(PhysicsContactPhase.Enter, buffer.Triggers[0].Phase);

            _world.EntityManager.DestroyEntity(mover);
            var replacement = Body(float3.zero, dynamic: true);
            Assert.AreEqual(mover.Index, replacement.Index, "Entities reuses the freed index immediately in a fresh world");
            Assert.AreNotEqual(mover.Version, replacement.Version);

            StepPhysics();

            Assert.AreEqual(2, buffer.Triggers.Count, string.Join(", ", buffer.Triggers));
            Assert.AreEqual(PhysicsContactPhase.Exit, buffer.Triggers[0].Phase);
            Assert.IsTrue(buffer.Triggers[0].AnyEntityDestroyed);
            Assert.AreEqual(mover, buffer.Triggers[0].EntityB, "the exit names the dead entity, version and all");
            Assert.AreEqual(PhysicsContactPhase.Enter, buffer.Triggers[1].Phase);
            Assert.AreEqual(replacement, buffer.Triggers[1].EntityB, "the newcomer is a new pair, not a stay");
            Assert.AreEqual(zone, buffer.Triggers[1].EntityA);
        }

        [Test]
        public void Collision_ReportsAggregatedContact_WithNormalFromAToB()
        {
            AddPhysicsPipeline(_world);
            var collisions = new ListPublisher<EntityCollision>();
            PhysicsEventsBootstrap.Install(_world, collisionPublisher: collisions);
            var buffer = PhysicsEventsBootstrap.Buffer(_world);

            // A dynamic sphere resting inside a static one along +x; both raise collision events.
            var anchor = Body(float3.zero, dynamic: false, PhysicsBodyFactory.CollisionEventMaterial(), radius: 1f);
            var ball = Body(new float3(1.5f, 0f, 0f), dynamic: true, PhysicsBodyFactory.CollisionEventMaterial(), radius: 1f);
            var pair = new PhysicsPairKey(anchor, ball);

            StepPhysics();

            Assert.AreEqual(1, buffer.Collisions.Count, string.Join(", ", buffer.Collisions));
            var hit = buffer.Collisions[0];
            Assert.AreEqual(PhysicsContactPhase.Enter, hit.Phase);
            Assert.AreEqual(pair.A, hit.EntityA);
            Assert.GreaterOrEqual(hit.ContactCount, 1);
            Assert.IsTrue(math.all(math.isfinite(hit.Normal)));
            Assert.IsTrue(math.all(math.isfinite(hit.Position)));
            var expectedSign = pair.A == anchor ? 1f : -1f; // anchor→ball is +x
            Assert.Greater(hit.Normal.x * expectedSign, 0.9f, "normal points from A toward B");
            Assert.AreEqual(1, collisions.Published.Count);
        }

        [Test]
        public void RepeatedInstallStepUninstall_LeavesNoModuleAndNoLeases()
        {
            AddPhysicsPipeline(_world);
            for (var cycle = 0; cycle < 5; cycle++)
            {
                PhysicsEventsBootstrap.Install(_world);
                var zone = Body(float3.zero, dynamic: false, PhysicsBodyFactory.TriggerMaterial(), radius: 2f);
                var mover = Body(float3.zero, dynamic: true);
                StepPhysics();
                Assert.AreEqual(1, PhysicsEventsBootstrap.Buffer(_world).Triggers.Count, $"cycle {cycle}");

                PhysicsEventsBootstrap.Uninstall(_world);
                _library.Release(_world.EntityManager.GetComponentData<PhysicsCollider>(zone).Value);
                _library.Release(_world.EntityManager.GetComponentData<PhysicsCollider>(mover).Value);
                _world.EntityManager.DestroyEntity(zone);
                _world.EntityManager.DestroyEntity(mover);
            }

            Assert.AreEqual(0, DotsModules.Installed(_world).Count);
            Assert.AreEqual(0, _library.TotalLeases, "every collider lease returned");
            Assert.AreEqual(0, _library.Count, "collider memory back to baseline");
        }
    }
}
