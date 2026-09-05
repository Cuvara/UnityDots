using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using NUnit.Framework;
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
            Assert.AreEqual(5f, ttl.RemainingSeconds, 0.01f);
        }
    }
}
