using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    public sealed class ArchetypeFactoryTests
    {
        private World _world;
        private EntityArchetypePreset _preset;

        [SetUp]
        public void SetUp()
        {
            _world = new World("ArchetypeFactoryTests");
            _preset = ScriptableObject.CreateInstance<EntityArchetypePreset>();
            _preset.entityType = "mob";
            _preset.viewKey = "goblin";
            _preset.hasHealth = true;
            _preset.defaultHp = 50;
            _preset.defaultMaxHp = 50;
            _preset.hasMoveData = true;
            _preset.hasTimeToLive = false;
            _preset.hasOverlayAnchor = true;
            _preset.overlayOffset = new Vector3(0, 1.5f, 0);
        }

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated) _world.Dispose();
            Object.DestroyImmediate(_preset);
        }

        [Test]
        public void Create_AddsTransform()
        {
            var pos = new float3(10, 0, 5);
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset, pos);

            Assert.IsTrue(_world.EntityManager.HasComponent<LocalTransform>(entity));
            var t = _world.EntityManager.GetComponentData<LocalTransform>(entity);
            Assert.AreEqual(10f, t.Position.x, 0.01f);
        }

        [Test]
        public void Create_AddsViewRequest()
        {
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsTrue(_world.EntityManager.HasComponent<EntityViewRequest>(entity));
            var req = _world.EntityManager.GetComponentData<EntityViewRequest>(entity);
            Assert.AreEqual("goblin", req.ViewKey.ToString());
        }

        [Test]
        public void Create_AddsHealth()
        {
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsTrue(_world.EntityManager.HasComponent<Health>(entity));
            var h = _world.EntityManager.GetComponentData<Health>(entity);
            Assert.AreEqual(50, h.Current);
            Assert.AreEqual(50, h.Max);
        }

        [Test]
        public void Create_AddsMoveData()
        {
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsTrue(_world.EntityManager.HasComponent<MoveData>(entity));
        }

        [Test]
        public void Create_AddsOverlayAnchor()
        {
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsTrue(_world.EntityManager.HasComponent<ViewOverlayAnchor>(entity));
            var anchor = _world.EntityManager.GetComponentData<ViewOverlayAnchor>(entity);
            Assert.AreEqual(1.5f, anchor.WorldOffset.y, 0.01f);
        }

        [Test]
        public void Create_NoViewKey_SkipsViewRequest()
        {
            _preset.viewKey = "";
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsFalse(_world.EntityManager.HasComponent<EntityViewRequest>(entity));
        }

        [Test]
        public void Create_NoHealth_SkipsHealth()
        {
            _preset.hasHealth = false;
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsFalse(_world.EntityManager.HasComponent<Health>(entity));
        }

        [Test]
        public void Create_WithTTL_AddsTTL()
        {
            _preset.hasTimeToLive = true;
            _preset.timeToLive = 5f;
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            Assert.IsTrue(_world.EntityManager.HasComponent<TimeToLive>(entity));
            var ttl = _world.EntityManager.GetComponentData<TimeToLive>(entity);
            Assert.AreEqual(5f, ttl.Remaining, 0.01f);
        }

        [Test]
        public void Create_WithZeroTTL_SkipsTheComponent_AsTheValidatorWarns()
        {
            _preset.hasTimeToLive = true;
            _preset.timeToLive = 0f;

            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);

            Assert.IsFalse(_world.EntityManager.HasComponent<TimeToLive>(entity));
            Assert.IsTrue(ViewConfigValidator.ValidatePreset(_preset).Has(ViewConfigIssue.IneffectiveTimeToLive));
        }

        [Test]
        public void Create_NullPreset_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => ArchetypeFactory.Create(_world.EntityManager, null));
        }

        [Test]
        public void Create_EveryComponentOff_LeavesOnlyTransforms()
        {
            _preset.viewKey = "";
            _preset.hasHealth = false;
            _preset.hasMoveData = false;
            _preset.hasTimeToLive = false;
            _preset.hasOverlayAnchor = false;

            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);
            var types = _world.EntityManager.GetComponentTypes(entity, Allocator.Temp);

            // Every entity carries Entities' own Simulate tag; everything else here is the factory's.
            var ours = 0;
            for (var i = 0; i < types.Length; i++)
            {
                if (types[i] != ComponentType.ReadWrite<Simulate>()) ours++;
            }

            Assert.AreEqual(2, ours, "LocalTransform and LocalToWorld, nothing else");
            Assert.IsTrue(_world.EntityManager.HasComponent<LocalTransform>(entity));
            Assert.IsTrue(_world.EntityManager.HasComponent<LocalToWorld>(entity));
            types.Dispose();
        }

        [Test]
        public void Create_EveryComponentOn_HasTheFullSet_WithItsInitialValues()
        {
            _preset.hasTimeToLive = true;
            _preset.timeToLive = 2.5f;
            _preset.defaultHp = 30;
            _preset.defaultMaxHp = 60;

            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset, new float3(1f, 2f, 3f));
            var em = _world.EntityManager;

            Assert.IsTrue(em.HasComponent<EntityViewRequest>(entity));
            Assert.IsTrue(em.HasComponent<Health>(entity));
            Assert.IsTrue(em.HasComponent<MoveData>(entity));
            Assert.IsTrue(em.HasComponent<TimeToLive>(entity));
            Assert.IsTrue(em.HasComponent<ViewOverlayAnchor>(entity));

            Assert.AreEqual(30, em.GetComponentData<Health>(entity).Current);
            Assert.AreEqual(60, em.GetComponentData<Health>(entity).Max);
            Assert.AreEqual(2.5f, em.GetComponentData<TimeToLive>(entity).Remaining, 1e-5f);
            Assert.AreEqual(float3.zero, em.GetComponentData<MoveData>(entity).Velocity);
            Assert.AreEqual(new float3(1f, 2f, 3f), em.GetComponentData<LocalToWorld>(entity).Position);
            Assert.AreEqual(1f, em.GetComponentData<LocalTransform>(entity).Scale);
            Assert.IsTrue(math.all(em.GetComponentData<LocalTransform>(entity).Rotation.value == quaternion.identity.value));
        }

        [Test]
        public void CreateBatch_CreatesOneEntityPerPosition_InOrder_AllIdentical()
        {
            var positions = new NativeArray<float3>(5, Allocator.Temp);
            for (var i = 0; i < positions.Length; i++) positions[i] = new float3(i, 0f, -i);
            var output = new NativeList<Entity>(Allocator.Temp);

            ArchetypeFactory.CreateBatch(_world.EntityManager, _preset, positions, output);

            Assert.AreEqual(5, output.Length);
            var archetype = _world.EntityManager.GetChunk(output[0]).Archetype;
            for (var i = 0; i < output.Length; i++)
            {
                Assert.AreEqual(new float3(i, 0f, -i), _world.EntityManager.GetComponentData<LocalTransform>(output[i]).Position);
                Assert.AreEqual(archetype, _world.EntityManager.GetChunk(output[i]).Archetype, "a batch from one preset is one archetype");
                Assert.AreEqual(50, _world.EntityManager.GetComponentData<Health>(output[i]).Current);
            }

            positions.Dispose();
            output.Dispose();
        }

        [Test]
        public void CreateBatch_RejectsUncreatedContainers()
        {
            var positions = new NativeArray<float3>(1, Allocator.Temp);
            Assert.Throws<System.ArgumentException>(() => ArchetypeFactory.CreateBatch(_world.EntityManager, _preset, positions, default));
            Assert.Throws<System.ArgumentException>(() => ArchetypeFactory.CreateBatch(_world.EntityManager, _preset, default, new NativeList<Entity>(Allocator.Temp)));
            positions.Dispose();
        }

        [Test]
        public void Create_WithoutTheViewModuleInstalled_StillCreates_AndTheRequestWaitsHarmlessly()
        {
            // No DotsViewBootstrap.Install in this fixture: the entity carries a request nothing
            // consumes, which is the contract for an optional module that is simply absent.
            var entity = ArchetypeFactory.Create(_world.EntityManager, _preset);

            Assert.IsTrue(_world.EntityManager.HasComponent<EntityViewRequest>(entity));
            Assert.IsFalse(_world.EntityManager.HasComponent<EntityViewLink>(entity));
            Assert.IsFalse(DotsViewBootstrap.IsInstalled(_world));
        }
    }
}
