using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// The network lifecycle contract, scripted: <c>IEntityView</c> call sequences in — the ones
    /// <c>WorldViewBinder</c> produces for a keyframe, a delta, a repeated keyframe, an
    /// area-of-interest exit and re-entry, and a session reset — and the exact documented event
    /// sequence out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every event is recorded as a one-line string (<c>"S:id"</c>, <c>"D:id:Reason"</c>) and the
    /// assertions compare whole sequences, because the contract is about order and count, not about
    /// any single event being seen. Driven through the public groups, as every test in this assembly
    /// is.
    /// </para>
    /// <para>
    /// The binder itself is not used, for the reason <c>NetworkEntityViewTests</c> gives: it needs
    /// <c>WorldState</c>, a second optional dependency. What it would call is called directly, in
    /// its order — spawn before first state, a <c>Despawn</c> per id on <c>Reset</c>.
    /// </para>
    /// </remarks>
    public sealed class NetworkLifecycleEventTests
    {
        private const string LocalArchetype = "player-local";
        private const string RemoteArchetype = "player-remote";
        private const string PlayerType = "player";

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private List<string> _events;
        private List<Entity> _spawnedEntities;
        private List<IDisposable> _subscriptions;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.NetworkLifecycleEventTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");

            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(
                new ViewArchetypeLibrary.Entry { Name = LocalArchetype, Config = _config },
                new ViewArchetypeLibrary.Entry { Name = RemoteArchetype, Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = NewView();
            DotsNetcodeBootstrap.Install(_world, _view);

            _events = new List<string>();
            _spawnedEntities = new List<Entity>();
            _subscriptions = new List<IDisposable>();
            Record(_view.Lifecycle);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            UnityEngine.Object.DestroyImmediate(_library);
            UnityEngine.Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private DotsEntityView NewView(NetworkEntityLifecycle lifecycle = null) => new DotsEntityView(
            _catalog,
            new TypeArchetypeResolver(LocalArchetype, null, new TypeArchetypeResolver.Rule(PlayerType, RemoteArchetype)),
            SnapshotSpaceMapping.XZPlane,
            writeHealth: false,
            lifecycle: lifecycle);

        /// <summary>Attaches the two recording handlers to <paramref name="lifecycle"/>.</summary>
        private void Record(NetworkEntityLifecycle lifecycle)
        {
            _subscriptions.Add(lifecycle.Subscribe((NetworkEntitySpawned e) =>
            {
                _events.Add("S:" + e.EntityId);
                _spawnedEntities.Add(e.Entity);
            }));
            _subscriptions.Add(lifecycle.Subscribe((NetworkEntityDespawned e) =>
                _events.Add("D:" + e.EntityId + ":" + e.Reason)));
        }

        private void Tick()
        {
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
            _world.GetExistingSystemManaged<ViewSystemGroup>().Update();
        }

        private Entity Find(string id)
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);

            for (var i = 0; i < entities.Length; i++)
            {
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted))
                {
                    return entities[i];
                }
            }

            return Entity.Null;
        }

        private int MirrorCount()
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            return query.CalculateEntityCount();
        }

        // -- keyframe, delta, repeated keyframe -------------------------------------------------

        [Test]
        public void Keyframe_PublishesOneSpawnPerId_InCommandOrder_AfterTheEntityIsComplete()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.SetState("uuid-a", 1f, 2f, 100, 100);
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.SetState("uuid-me", 3f, 4f, 100, 100);

            NetworkEntitySpawned? local = null;
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntitySpawned e) =>
            {
                // The entity is complete when the handler runs: every adapter-owned component is on
                // it and it is queryable by id.
                Assert.IsTrue(_entityManager.Exists(e.Entity));
                Assert.IsTrue(_entityManager.HasComponent<NetworkEntity>(e.Entity));
                Assert.IsTrue(_entityManager.HasComponent<ReconciliationAnchor>(e.Entity));
                Assert.AreEqual(new FixedString64Bytes(e.EntityId), _entityManager.GetComponentData<NetworkEntity>(e.Entity).Id);
                if (e.IsLocal) local = e;
            }));

            Tick();

            CollectionAssert.AreEqual(new[] { "S:uuid-a", "S:uuid-me" }, _events);
            Assert.IsTrue(local.HasValue, "IsLocal reached the event");
            Assert.AreEqual("uuid-me", local.Value.EntityId);
            Assert.AreEqual(PlayerType, local.Value.EntityType);
            Assert.AreEqual(Find("uuid-me"), local.Value.Entity, "the event names the mirror the drain created");
        }

        [Test]
        public void Delta_ThenRepeatedKeyframe_PublishesNoSecondSpawn()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.SetState("uuid-a", 1f, 1f, 100, 100);
            Tick();

            // Delta: state only.
            view.SetState("uuid-a", 2f, 2f, 100, 100);
            Tick();

            // Repeated keyframe from a caller that bypasses WorldViewBinder's own _live filter: a
            // second Spawn for an id already present. The view refuses it; had it reached the drain,
            // the drain would refuse it too. Either way: no event, no second entity.
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.SetState("uuid-a", 3f, 3f, 100, 100);
            Tick();

            CollectionAssert.AreEqual(new[] { "S:uuid-a" }, _events);
            Assert.AreEqual(1, MirrorCount());
            Assert.AreEqual(1, _view.Lifecycle.SpawnedCount);
            Assert.AreEqual(0, _view.Lifecycle.DespawnedCount);
        }

        // -- AOI exit and re-entry -------------------------------------------------------------

        [Test]
        public void AoiExit_ThenReentry_IsDespawnThenSpawn_WithADifferentEntity()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            view.Despawn("uuid-a");
            Tick();

            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            CollectionAssert.AreEqual(new[] { "S:uuid-a", "D:uuid-a:Despawned", "S:uuid-a" }, _events);
            Assert.AreEqual(2, _spawnedEntities.Count);
            Assert.AreNotEqual(_spawnedEntities[0], _spawnedEntities[1],
                "two lives of one id are two entities; index reuse is told apart by version");
        }

        [Test]
        public void Despawned_CarriesTheSameEntityTheSpawnDid_AndIsPublishedBeforeTheDestroy()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.SetState("uuid-a", 5f, 6f, 100, 100);
            Tick();

            var existedInHandler = false;
            var entityInHandler = Entity.Null;
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntityDespawned e) =>
            {
                entityInHandler = e.Entity;
                existedInHandler = _entityManager.Exists(e.Entity);
                if (existedInHandler)
                {
                    // Readable for a despawn effect: the last transform the drain wrote.
                    Assert.AreEqual(new Unity.Mathematics.float3(5f, 0f, 6f),
                        _entityManager.GetComponentData<LocalTransform>(e.Entity).Position);
                }
            }));

            view.Despawn("uuid-a");
            Tick();

            Assert.IsTrue(existedInHandler, "for a wire despawn the entity still exists while handlers run");
            Assert.AreEqual(_spawnedEntities[0], entityInHandler, "same Entity, version included");
            Assert.IsFalse(_entityManager.Exists(entityInHandler), "and it is gone once the drain moves on");
        }

        // -- session reset (reconnect / map transfer) ------------------------------------------

        [Test]
        public void SessionReset_DespawnsEveryId_OnceEach_InTheBindersOrder()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            Tick();
            _events.Clear();

            // What WorldViewBinder.Reset does: a Despawn per live id, in its set's order.
            view.Despawn("uuid-me");
            view.Despawn("uuid-a");
            view.Despawn("uuid-b");
            Tick();

            CollectionAssert.AreEqual(
                new[] { "D:uuid-me:Despawned", "D:uuid-a:Despawned", "D:uuid-b:Despawned" }, _events);
            Assert.AreEqual(0, MirrorCount());

            // A teardown after a drained reset has nothing left to report.
            DotsNetcodeBootstrap.Uninstall(_world, destroyMirrors: true);
            Assert.AreEqual(3, _events.Count, "no duplicate despawn from the teardown");
        }

        [Test]
        public void Reconnect_ResetThenKeyframe_IsOneFullDespawnSpawnCycle()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            Tick();

            // Reset, then the new session's keyframe lists the same ids again.
            view.Despawn("uuid-a");
            view.Despawn("uuid-me");
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            Tick();

            CollectionAssert.AreEqual(new[]
            {
                "S:uuid-a", "S:uuid-me",
                "D:uuid-a:Despawned", "D:uuid-me:Despawned",
                "S:uuid-a", "S:uuid-me",
            }, _events);
            Assert.AreEqual(2, MirrorCount());
        }

        // -- teardown --------------------------------------------------------------------------

        [Test]
        public void Teardown_PublishesOneDespawnPerPresentId_AndAQueuedDespawnIsThenSilent()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            Tick();
            _events.Clear();

            // Enqueued and never drained before the teardown — the binder's Reset racing the
            // session's disposal.
            view.Despawn("uuid-a");

            DotsNetcodeBootstrap.Uninstall(_world, destroyMirrors: true);

            CollectionAssert.AreEquivalent(new[] { "D:uuid-a:Teardown", "D:uuid-b:Teardown" }, _events);
            Assert.AreEqual(0, MirrorCount(), "the mirrors went with the session");

            // The singleton is gone, so the drain does not update; the queued despawn is never
            // applied and never reported.
            Tick();
            Assert.AreEqual(2, _events.Count);
        }

        [Test]
        public void Uninstall_WithoutDestroyMirrors_PublishesNothing_AndLeavesTheEntities()
        {
            // 0.27.1's behaviour, kept as the default.
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();
            _events.Clear();

            DotsNetcodeBootstrap.Uninstall(_world);

            CollectionAssert.IsEmpty(_events);
            Assert.AreEqual(1, MirrorCount());
        }

        // -- external destruction --------------------------------------------------------------

        [Test]
        public void ExternalDestruction_IsReportedOnce_OnTheNextCommand_AndTheWireDespawnIsThenSilent()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            // A consumer system — or HealthDeathSystem with writeHealth on — destroys the mirror.
            _entityManager.DestroyEntity(Find("uuid-a"));

            // Nothing is known until a command for the id arrives.
            Tick();
            CollectionAssert.AreEqual(new[] { "S:uuid-a" }, _events);

            view.SetState("uuid-a", 1f, 1f, 100, 100);
            Tick();
            CollectionAssert.AreEqual(new[] { "S:uuid-a", "D:uuid-a:ExternalDestruction" }, _events);

            // The wire eventually despawns it too. Already reported; silent.
            view.Despawn("uuid-a");
            Tick();
            CollectionAssert.AreEqual(new[] { "S:uuid-a", "D:uuid-a:ExternalDestruction" }, _events);
        }

        [Test]
        public void ExternalDestruction_ThenASpawnOfTheSameId_ClosesTheFirstLifeBeforeOpeningTheSecond()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();
            _entityManager.DestroyEntity(Find("uuid-a"));

            // A second view over the same drain — the one shape in which a Spawn for a mapped id can
            // reach ApplySpawn at all, because the first view's own filter still holds the id.
            var replacement = NewView(_view.Lifecycle);
            DotsNetcodeBootstrap.Install(_world, replacement);
            ((IEntityView)replacement).Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            CollectionAssert.AreEqual(new[] { "S:uuid-a", "D:uuid-a:ExternalDestruction", "S:uuid-a" }, _events);
            Assert.AreEqual(1, MirrorCount());
            Assert.AreNotEqual(_spawnedEntities[0], _spawnedEntities[1]);
        }

        [Test]
        public void Teardown_ReportsAnUnnoticedExternalDestruction_AsSuch()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            Tick();
            _entityManager.DestroyEntity(Find("uuid-a"));
            _events.Clear();

            DotsNetcodeBootstrap.Uninstall(_world, destroyMirrors: true);

            CollectionAssert.AreEquivalent(new[] { "D:uuid-a:ExternalDestruction", "D:uuid-b:Teardown" }, _events);
        }

        // -- subscription semantics ------------------------------------------------------------

        [Test]
        public void ThrowingSubscriber_IsIsolated_FromOtherSubscribersAndFromTheDrain()
        {
            LogAssert.Expect(LogType.Exception, new Regex("spawn-boom"));
            LogAssert.Expect(LogType.Exception, new Regex("spawn-boom"));
            LogAssert.Expect(LogType.Exception, new Regex("despawn-boom"));

            // Subscribed FIRST, so the later recording handlers are the ones that must survive it.
            var throwing = _view.Lifecycle.Subscribe((NetworkEntitySpawned e) => throw new InvalidOperationException("spawn-boom"));
            var throwingDespawn = _view.Lifecycle.Subscribe((NetworkEntityDespawned e) => throw new InvalidOperationException("despawn-boom"));
            _events.Clear();
            var late = new List<string>();
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntitySpawned e) => late.Add("S:" + e.EntityId)));
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntityDespawned e) => late.Add("D:" + e.EntityId)));

            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            Tick();
            view.Despawn("uuid-a");
            Tick();

            // Both the handlers subscribed before the throwing one and those after it heard everything.
            CollectionAssert.AreEqual(new[] { "S:uuid-a", "S:uuid-b", "D:uuid-a:Despawned" }, _events);
            CollectionAssert.AreEqual(new[] { "S:uuid-a", "S:uuid-b", "D:uuid-a" }, late);

            // And the drain finished its work: both created, one destroyed.
            Assert.AreEqual(1, MirrorCount());
            Assert.AreEqual(Entity.Null, Find("uuid-a"));

            throwing.Dispose();
            throwingDespawn.Dispose();
        }

        [Test]
        public void LateSubscriber_HearsNothingRetroactive()
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            var late = new List<string>();
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntitySpawned e) => late.Add(e.EntityId)));

            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            Tick();

            CollectionAssert.AreEqual(new[] { "uuid-b" }, late, "present-at-subscribe is a query, not a replay");
            Assert.AreEqual(2, MirrorCount());
        }

        [Test]
        public void DisposedSubscription_StopsDelivery_AndDisposingTwiceIsHarmless()
        {
            var heard = new List<string>();
            var subscription = _view.Lifecycle.Subscribe((NetworkEntitySpawned e) => heard.Add(e.EntityId));

            var view = (IEntityView)_view;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            subscription.Dispose();
            subscription.Dispose();

            view.Spawn("uuid-b", isLocal: false, type: PlayerType);
            Tick();

            CollectionAssert.AreEqual(new[] { "uuid-a" }, heard);
            Assert.AreEqual(2, _view.Lifecycle.SubscriberCount, "only the fixture's two recorders remain");
        }

        [Test]
        public void WithNoObservers_TheDrainStillSpawnsAndDespawns_AndCountsNothing()
        {
            // A view nobody subscribed to: the publish is skipped entirely, so the per-event string
            // allocation is not paid and the counters stay at zero. Behaviour is otherwise 0.27.1's.
            var silent = NewView();
            DotsNetcodeBootstrap.Install(_world, silent);
            Assert.IsFalse(silent.Lifecycle.HasObservers);

            var view = (IEntityView)silent;
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();
            view.Despawn("uuid-a");
            Tick();

            Assert.AreEqual(0, MirrorCount());
            Assert.AreEqual(0, silent.Lifecycle.SpawnedCount);
            Assert.AreEqual(0, silent.Lifecycle.DespawnedCount);
        }
    }
}
