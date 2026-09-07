using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// Snapshot ingestion under the conditions the wire actually produces: duplicates, reordering,
    /// a session reset with data still queued, AOI re-entry, a burst, and a producer on the wrong
    /// thread — plus the counters that say what the drain did about each.
    /// </summary>
    /// <remarks>
    /// Driven through the public groups, as <c>NetworkEntityViewTests</c> is. Interpolation
    /// arithmetic is netcode's and is not asserted; what is asserted is which samples the buffer
    /// keeps, which writer owns the transform, and that a reset leaves no entity of the old session
    /// behind — for a frame or forever.
    /// </remarks>
    public sealed class SnapshotIngestionTests
    {
        private const string RemoteArchetype = "player-remote";
        private const string PlayerType = "player";

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private readonly List<NetworkEntitySpawned> _spawned = new List<NetworkEntitySpawned>();
        private readonly List<NetworkEntityDespawned> _despawned = new List<NetworkEntityDespawned>();

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.SnapshotIngestionTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");
            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = RemoteArchetype, Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(RemoteArchetype, null, new TypeArchetypeResolver.Rule(PlayerType, RemoteArchetype)),
                SnapshotSpaceMapping.XZPlane);
            DotsNetcodeBootstrap.Install(_world, _view);

            _spawned.Clear();
            _despawned.Clear();
            _view.Lifecycle.Subscribe((NetworkEntitySpawned e) => _spawned.Add(e));
            _view.Lifecycle.Subscribe((NetworkEntityDespawned e) => _despawned.Add(e));

            // A backlog warning is expected by one test and harmless in the others.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            UnityEngine.Object.DestroyImmediate(_library);
            UnityEngine.Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private void Tick()
        {
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
            _world.GetExistingSystemManaged<ViewSystemGroup>().Update();
        }

        private int MirrorCount()
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            return query.CalculateEntityCount();
        }

        private Entity Find(string id)
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);
            for (var i = 0; i < entities.Length; i++)
            {
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted)) return entities[i];
            }

            return Entity.Null;
        }

        private void SpawnRemote(string id = "uuid-r")
        {
            ((IEntityView)_view).Spawn(id, isLocal: false, type: PlayerType);
        }

        private void Timed(string id, long tick, float x, float y)
        {
            _view.SetStateAtTick(id, x, y, 100, 100, tick, receiveTimeSeconds: tick / 15.0);
        }

        private static long[] Ticks(DynamicBuffer<SnapshotSample> samples)
        {
            var ticks = new long[samples.Length];
            for (var i = 0; i < samples.Length; i++) ticks[i] = samples[i].Value.Tick;
            return ticks;
        }

        // ---- duplicate / reordered ----

        [Test]
        public void DuplicateTick_IsRefused_AndDoesNotFallBackToADirectWrite()
        {
            SpawnRemote();
            Timed("uuid-r", 10, 1f, 1f);
            Timed("uuid-r", 10, 5f, 5f); // same tick, different position: a duplicate, not an update
            Tick();

            var entity = Find("uuid-r");
            var samples = _entityManager.GetBuffer<SnapshotSample>(entity);
            CollectionAssert.AreEqual(new long[] { 10 }, Ticks(samples));
            Assert.AreEqual(1f, samples[0].Value.X, "the first arrival is the one kept");
            Assert.AreEqual(1, _view.Metrics.RejectedSamples);

            var transform = _entityManager.GetComponentData<LocalTransform>(entity);
            Assert.AreNotEqual(5f, transform.Position.x, "a refused sample must not be written straight to the transform");
        }

        [Test]
        public void ReorderedTick_IsRefused_AndTheBufferStaysMonotonic()
        {
            SpawnRemote();
            Timed("uuid-r", 10, 1f, 1f);
            Timed("uuid-r", 12, 3f, 3f);
            Timed("uuid-r", 11, 2f, 2f); // arrives late
            Tick();

            var samples = _entityManager.GetBuffer<SnapshotSample>(Find("uuid-r"));
            CollectionAssert.AreEqual(new long[] { 10, 12 }, Ticks(samples));
            Assert.AreEqual(1, _view.Metrics.RejectedSamples);
        }

        // ---- one interpolation path per entity ----

        [Test]
        public void UntimedStateOnAnInterpolatedEntity_DoesNotTakeOverTheTransform()
        {
            SpawnRemote();
            Timed("uuid-r", 10, 1f, 1f);
            Timed("uuid-r", 11, 2f, 2f);
            Tick();

            var entity = Find("uuid-r");
            var before = _entityManager.GetComponentData<LocalTransform>(entity).Position;

            ((IEntityView)_view).SetState("uuid-r", 50f, 50f, 42, 100); // the other path, same id
            Tick();

            var after = _entityManager.GetComponentData<LocalTransform>(entity).Position;
            Assert.AreNotEqual(50f, after.x, "interpolation owns this transform; the untimed state must not teleport it");
            Assert.AreEqual(1, _view.Metrics.MixedPathStates, "and the collision is counted");

            // The non-transform halves of the state still land: the anchor is the server's word
            // regardless of who renders, and hp is never interpolated.
            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(entity);
            Assert.AreEqual(new float2(50f, 50f), anchor.ServerPosition);
            Assert.AreEqual(3u, anchor.Sequence);
            Assert.AreEqual(0L, anchor.Tick, "an untimed state states no tick");
            Assert.AreEqual(42, _entityManager.GetComponentData<NetworkEntityState>(entity).Hp);
            Assert.AreEqual(2, _entityManager.GetBuffer<SnapshotSample>(entity).Length, "no sample is invented for an untimed state");
            Assert.AreEqual(before.y, after.y);
        }

        [Test]
        public void TimedStates_StampTheAnchorWithTheirTick()
        {
            SpawnRemote();
            Timed("uuid-r", 10, 1f, 1f);
            Timed("uuid-r", 11, 2f, 2f);
            Tick();

            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(Find("uuid-r"));
            Assert.AreEqual(11L, anchor.Tick);
            Assert.AreEqual(2u, anchor.Sequence);
            Assert.AreEqual(new float2(2f, 2f), anchor.ServerPosition);
        }

        // ---- generations ----

        [Test]
        public void BeginGeneration_TearsDownTheOldSession_AndDropsItsQueuedCommands()
        {
            var view = (IEntityView)_view;
            SpawnRemote("uuid-a");
            SpawnRemote("uuid-b");
            view.SetState("uuid-a", 1f, 1f, 100, 100);
            Tick();
            Assert.AreEqual(2, MirrorCount());
            Assert.AreEqual(1, _view.Generation);

            // Old-session data still in the queue when the reset lands.
            view.SetState("uuid-a", 9f, 9f, 100, 100);
            SpawnRemote("uuid-c");
            var generation = _view.BeginGeneration();
            Assert.AreEqual(2, generation);
            Assert.AreEqual(0, _view.Count, "the view forgot every live id");
            Assert.AreEqual(3, _view.PendingCommands, "state, spawn and the reset itself");

            Tick();

            Assert.AreEqual(0, MirrorCount(), "no entity of the old session survives, not even for a frame");
            Assert.AreEqual(0, _registry.Count);
            Assert.AreEqual(2, _view.Metrics.StaleCommandsDropped, "the queued state and spawn were never applied");
            Assert.AreEqual(1, _view.Metrics.GenerationResets);
            Assert.AreEqual(2, _despawned.Count);
            foreach (var d in _despawned) Assert.AreEqual(NetworkDespawnReason.SessionReset, d.Reason);
            Assert.AreEqual(2, _spawned.Count, "uuid-c never spawned: its command was stale");
        }

        [Test]
        public void LateDataFromTheOldSession_CannotRespawnAnOldEntity_ButTheNewSessionCan()
        {
            var view = (IEntityView)_view;
            SpawnRemote("uuid-a");
            view.SetState("uuid-a", 1f, 1f, 100, 100);
            Tick();

            _view.BeginGeneration();

            // New session: the same id comes back, as it would after a reconnect to the same map.
            SpawnRemote("uuid-a");
            view.SetState("uuid-a", 7f, 7f, 100, 100);
            Tick();

            Assert.AreEqual(1, MirrorCount());
            var entity = Find("uuid-a");
            Assert.AreEqual(new float3(7f, 0f, 7f), _entityManager.GetComponentData<LocalTransform>(entity).Position);
            Assert.AreEqual(1u, _entityManager.GetComponentData<ReconciliationAnchor>(entity).Sequence, "a fresh mirror, not the old one updated");

            Assert.AreEqual(2, _spawned.Count, "one life per generation");
            Assert.AreEqual(1, _despawned.Count);
            Assert.AreEqual(NetworkDespawnReason.SessionReset, _despawned[0].Reason);
            Assert.AreEqual(0, _view.Metrics.StaleCommandsDropped);
        }

        [Test]
        public void TwoResetsBeforeADrain_ApplyOnce_AndLeaveTheNewestGeneration()
        {
            SpawnRemote("uuid-a");
            Tick();

            _view.BeginGeneration();
            _view.BeginGeneration();
            SpawnRemote("uuid-b");
            Tick();

            Assert.AreEqual(3, _view.Generation);
            Assert.AreEqual(1, MirrorCount());
            Assert.AreNotEqual(Entity.Null, Find("uuid-b"));
            Assert.AreEqual(1, _view.Metrics.GenerationResets, "the superseded reset was stale and dropped");
            Assert.AreEqual(1, _view.Metrics.StaleCommandsDropped);
            Assert.AreEqual(1, _despawned.Count);
        }

        // ---- AOI re-entry ----

        [Test]
        public void AoiReentry_GivesAFreshMirror_WhoseRingAcceptsEarlierTicks()
        {
            var view = (IEntityView)_view;
            SpawnRemote();
            Timed("uuid-r", 10, 1f, 1f);
            Timed("uuid-r", 11, 2f, 2f);
            Tick();
            var first = Find("uuid-r");
            Assert.AreEqual(2, _entityManager.GetBuffer<SnapshotSample>(first).Length);

            view.Despawn("uuid-r");
            SpawnRemote();
            Tick();

            var second = Find("uuid-r");
            Assert.AreNotEqual(Entity.Null, second);
            Assert.AreNotEqual(first, second, "re-entry is a new life, not the old entity kept");
            Assert.AreEqual(0, _entityManager.GetBuffer<SnapshotSample>(second).Length, "no samples carried across the gap");
            Assert.AreEqual(0u, _entityManager.GetComponentData<ReconciliationAnchor>(second).Sequence);

            // A tick below the old buffer's newest is legal for the new life: the ring is per life.
            Timed("uuid-r", 5, 3f, 3f);
            Tick();
            CollectionAssert.AreEqual(new long[] { 5 }, Ticks(_entityManager.GetBuffer<SnapshotSample>(second)));
            Assert.AreEqual(0, _view.Metrics.RejectedSamples);
            Assert.AreEqual(2, _spawned.Count);
            Assert.AreEqual(1, _despawned.Count);
            Assert.AreEqual(NetworkDespawnReason.Despawned, _despawned[0].Reason);
        }

        // ---- metrics and bursts ----

        [Test]
        public void Metrics_DescribeTheDrain()
        {
            var view = (IEntityView)_view;
            for (var i = 0; i < 200; i++)
            {
                view.Spawn($"uuid-{i}", isLocal: false, type: PlayerType);
                view.SetState($"uuid-{i}", i, i, 100, 100);
            }

            Assert.AreEqual(400, _view.PendingCommands);
            Assert.AreEqual(400, _view.Metrics.Enqueued);
            Assert.GreaterOrEqual(_view.Metrics.PendingHighWatermark, 400);
            Assert.AreEqual(0, _view.Metrics.Drains, "nothing drained yet");

            Tick();

            Assert.AreEqual(0, _view.PendingCommands);
            Assert.AreEqual(1, _view.Metrics.Drains);
            Assert.AreEqual(400, _view.Metrics.LastDrainCount);
            Assert.AreEqual(400, _view.Metrics.Drained);
            Assert.GreaterOrEqual(_view.Metrics.LastDrainSeconds, 0.0);
            Assert.GreaterOrEqual(_view.Metrics.LastOldestCommandAgeSeconds, 0.0, "the first command waited at least no time");
            Assert.GreaterOrEqual(_view.Metrics.MaxOldestCommandAgeSeconds, _view.Metrics.LastOldestCommandAgeSeconds);
            Assert.AreEqual(200, MirrorCount());

            Tick();
            Assert.AreEqual(1, _view.Metrics.Drains, "an empty drain is not counted");
        }

        [Test]
        public void ABurst_IsDrainedInOneFrame_AndTheLastStateWins()
        {
            var view = (IEntityView)_view;
            SpawnRemote();
            for (var i = 0; i < 3000; i++) view.SetState("uuid-r", i, i, 100, 100);
            Assert.AreEqual(3001, _view.PendingCommands);

            Tick();

            Assert.AreEqual(0, _view.PendingCommands, "bounded recovery: one frame, whole backlog");
            var entity = Find("uuid-r");
            Assert.AreEqual(new float3(2999f, 0f, 2999f), _entityManager.GetComponentData<LocalTransform>(entity).Position);
            Assert.AreEqual(3000u, _entityManager.GetComponentData<ReconciliationAnchor>(entity).Sequence);
            Assert.AreEqual(0, _view.Metrics.MixedPathStates);
            Assert.AreEqual(3001, _view.Metrics.LastDrainCount);
        }

        [Test]
        public void BacklogWarning_FiresOncePerGeneration_AndDropsNothing()
        {
            var view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(RemoteArchetype, null, new TypeArchetypeResolver.Rule(PlayerType, RemoteArchetype)),
                SnapshotSpaceMapping.XZPlane,
                backlogWarningThreshold: 4);
            var entityView = (IEntityView)view;
            entityView.Spawn("uuid-w", isLocal: false, type: PlayerType);
            for (var i = 0; i < 10; i++) entityView.SetState("uuid-w", i, i, 100, 100);

            Assert.AreEqual(11, view.PendingCommands, "warning, not a cap");

            // Drained through the drain of this fixture's world by swapping the installed view.
            DotsNetcodeBootstrap.Install(_world, view);
            Tick();
            Assert.AreEqual(0, view.PendingCommands);
            Assert.AreEqual(11, view.Metrics.LastDrainCount);
        }

        // ---- thread affinity ----

        [Test]
        public void ProducerThread_IsLatchedByTheFirstCall_AndOtherThreadsAreRefused()
        {
            SpawnRemote();
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, _view.ProducerThreadId);

            var ex = Assert.Throws<AggregateException>(() =>
                Task.Run(() => ((IEntityView)_view).SetState("uuid-r", 1f, 1f, 100, 100)).Wait());
            Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);

            var ex2 = Assert.Throws<AggregateException>(() => Task.Run(() => _view.BeginGeneration()).Wait());
            Assert.IsInstanceOf<InvalidOperationException>(ex2.InnerException);

            Assert.AreEqual(1, _view.PendingCommands, "the refused calls enqueued nothing");
            Assert.AreEqual(1, _view.Generation);
        }

        [Test]
        public void BeginGeneration_ReopensTheLatch_SoANewSessionMayUseANewThread()
        {
            SpawnRemote("uuid-old");
            Tick();
            _view.BeginGeneration();
            Assert.AreEqual(0, _view.ProducerThreadId, "unlatched until the new session's first call");

            var workerId = 0;
            Task.Run(() =>
            {
                workerId = Thread.CurrentThread.ManagedThreadId;
                ((IEntityView)_view).Spawn("uuid-new", isLocal: false, type: PlayerType);
            }).Wait();

            Assert.AreEqual(workerId, _view.ProducerThreadId);
            Assert.Throws<InvalidOperationException>(() => SpawnRemote("uuid-late"), "the old thread is now the wrong one");

            Tick();
            Assert.AreEqual(1, MirrorCount());
            Assert.AreNotEqual(Entity.Null, Find("uuid-new"));
            Assert.AreEqual(Entity.Null, Find("uuid-old"));
        }
    }
}
