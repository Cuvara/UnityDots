using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Netcode.Prediction;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Cuvara.Netcode.World;
using NUnit.Framework;
using Shared.GameLogic.Components;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Prediction
{
    /// <summary>
    /// Transform ownership and reconciliation inputs across the situations that break them: a
    /// reconnect, an ack that outran the drain, timed states on the predicted entity, prediction
    /// toggled at runtime, and a predictor that has moved away from the server's word.
    /// </summary>
    /// <remarks>
    /// Complements <c>LocalPredictionSystemTests</c>, which pins the steady state. The predictor's
    /// arithmetic is netcode's; what is asserted here is which values reach it, when, and that
    /// exactly one writer touches <c>LocalTransform</c> at every moment.
    /// </remarks>
    public sealed class PredictionOwnershipTests
    {
        private const string LocalArchetype = "player-local";
        private const string PlayerType = "player";

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private WorldState _worldState;
        private double _elapsed;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.PredictionOwnershipTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");
            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = LocalArchetype, Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(LocalArchetype, LocalArchetype),
                SnapshotSpaceMapping.XZPlane);
            DotsNetcodeBootstrap.Install(_world, _view);

            _worldState = new WorldState();
            _elapsed = 0.0;
        }

        [TearDown]
        public void TearDown()
        {
            DotsPredictionBootstrap.Uninstall(_world);
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            Object.DestroyImmediate(_library);
            Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private static LocalMovePredictor Predictor() => new LocalMovePredictor(
            new PredictionSettings(15, 5f, new MapBounds(0f, 0f, 1000f, 1000f)));

        /// <summary>One frame through both groups, with real time so the predictor and the render clock advance.</summary>
        private void Tick(float deltaTime = 0.016f)
        {
            _elapsed += deltaTime;
            _world.SetTime(new TimeData(_elapsed, deltaTime));
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
            _world.GetExistingSystemManaged<ViewSystemGroup>().Update();
        }

        private Entity Local()
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).IsLocal) return entities[i];
            }

            return Entity.Null;
        }

        private int LocalCount()
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            return query.CalculateEntityCount();
        }

        private void SpawnLocal(float x = 0f, float y = 0f)
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.SetState("uuid-me", x, y, 100, 100);
        }

        /// <summary>The wire's half: a merged snapshot carrying an ack tick and a speed.</summary>
        private void ApplyServerSnapshot(float x, float y, long tick, float speed = 5f)
        {
            _worldState.Apply(new ResolvedSnapshot(
                tick,
                ackTick: tick,
                full: true,
                entities: new[] { new ResolvedEntity("uuid-me", PlayerType, x, y, 100, 100, speed) },
                removed: null));
        }

        [Test]
        public void ReconnectReset_ReenablesReconciliation_WhenTheNewServersTicksAreLower()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            // Session 1: acked at tick 100. The first reconcile seeds the predictor and is not counted.
            SpawnLocal(10f, 10f);
            ApplyServerSnapshot(10f, 10f, tick: 100);
            Tick();
            Assert.AreEqual(0, predictor.Reconciles, "guard: netcode counts from the second reconcile");

            // Reconnect: new generation on the view, fresh world state, and a server whose ticks
            // are far below 100. Without the generation reset `ackTick > _lastAckTick` would stay
            // false for the whole session and reconciliation would be silently off.
            _view.BeginGeneration();
            _worldState.Reset();
            SpawnLocal(20f, 20f);
            ApplyServerSnapshot(20f, 20f, tick: 5);
            Tick();

            Assert.AreEqual(1, LocalCount(), "one mirror: the old session's is gone");
            Assert.AreEqual(1, predictor.Reconciles, "the new session's first ack reconciled");
            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(Local());
            Assert.AreEqual(new float2(20f, 20f), anchor.ServerPosition);
            Assert.AreEqual(1u, anchor.Sequence, "a fresh anchor for a fresh mirror");
        }

        [Test]
        public void AnAckThatOutranTheDrain_WaitsForTheAnchor_RatherThanPairingWithAStalePosition()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal(1f, 1f);
            ApplyServerSnapshot(1f, 1f, tick: 1);
            Tick(); // seed
            Assert.AreEqual(0, predictor.Reconciles);

            // The binder merged tick 2 but the view has not been fed its state yet — the ack moved,
            // the anchor did not. Reconciling now would hand the predictor (tick 2, position of
            // tick 1), a pair the server never produced.
            ApplyServerSnapshot(2f, 2f, tick: 2);
            Tick();
            Assert.AreEqual(0, predictor.Reconciles, "no fresh anchor, no reconcile");

            ((IEntityView)_view).SetState("uuid-me", 2f, 2f, 100, 100);
            Tick();
            Assert.AreEqual(1, predictor.Reconciles, "anchor and ack now describe the same snapshot");

            Tick();
            Assert.AreEqual(1, predictor.Reconciles, "an unchanged pair is not reconciled again");
        }

        [Test]
        public void TheSpawnPlaceholderAnchor_IsNeverReconciledTo()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            // Spawn with no state: the anchor is at sequence 0 and ServerPosition is float2.zero
            // because the server has said nothing — not because the player is at the origin.
            ((IEntityView)_view).Spawn("uuid-me", isLocal: true, type: PlayerType);
            ApplyServerSnapshot(40f, 40f, tick: 3);
            Tick();

            Assert.AreEqual(0u, _entityManager.GetComponentData<ReconciliationAnchor>(Local()).Sequence);
            Assert.AreEqual(0, predictor.Reconciles, "not even the seeding reconcile ran against a placeholder");

            // The first real state seeds the predictor from a real position.
            ((IEntityView)_view).SetState("uuid-me", 40f, 40f, 100, 100);
            Tick();
            Assert.AreEqual(40f, predictor.Position.X, 1e-3f);
            Assert.AreEqual(40f, predictor.Position.Y, 1e-3f);
        }

        [Test]
        public void TimedStatesOnThePredictedEntity_AreBuffered_ButNeverRenderedByInterpolation()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal(5f, 5f);
            Tick();
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(Local()), "guard: claimed");

            _view.SetStateAtTick("uuid-me", 100f, 100f, 100, 100, tick: 10, receiveTimeSeconds: 10 / 15.0);
            _view.SetStateAtTick("uuid-me", 200f, 200f, 100, 100, tick: 11, receiveTimeSeconds: 11 / 15.0);
            for (var i = 0; i < 5; i++) Tick();

            var entity = Local();
            Assert.AreEqual(2, _entityManager.GetBuffer<SnapshotSample>(entity).Length, "samples are kept — the anchor path is the same");
            Assert.IsFalse(_entityManager.GetComponentData<InterpolationState>(entity).HasRendered,
                "the interpolation job must skip a predicted entity: one writer for LocalTransform");
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(entity));
            Assert.AreEqual(new float2(200f, 200f), _entityManager.GetComponentData<ReconciliationAnchor>(entity).ServerPosition);
        }

        [Test]
        public void TogglingPrediction_HandsTheTransformBackAndForth_WithoutAFrameOfNoWriter()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal(3f, 3f);
            ApplyServerSnapshot(3f, 3f, tick: 1);
            Tick();
            var entity = Local();
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(entity));

            // Off: the adapter must drive the transform again on the very next state.
            DotsPredictionBootstrap.Uninstall(_world);
            Assert.IsFalse(_entityManager.HasComponent<PredictedTransform>(entity), "uninstall releases the claim");
            ((IEntityView)_view).SetState("uuid-me", 4f, 4f, 100, 100);
            Tick();
            Assert.AreEqual(new float3(4f, 0f, 4f), _entityManager.GetComponentData<LocalTransform>(entity).Position,
                "with no predictor the authoritative position lands directly");

            // On again: claimed before the first predicted write, and the predictor takes over from
            // the known-good position rather than from the origin.
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            ((IEntityView)_view).SetState("uuid-me", 4f, 4f, 100, 100);
            ApplyServerSnapshot(4f, 4f, tick: 2);
            Tick();
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(entity));
            var position = _entityManager.GetComponentData<LocalTransform>(entity).Position;
            Assert.AreEqual(4f, position.x, 1e-3f);
            Assert.AreEqual(4f, position.z, 1e-3f);
        }

        [Test]
        public void TheAnchor_IsNeverFedFromThePredictedOrRenderedPosition()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal(1f, 1f);
            ApplyServerSnapshot(1f, 1f, tick: 1);
            Tick(); // seeds the predictor at (1, 1)

            // Unacknowledged inputs move the prediction away from the server's word.
            for (var tick = 2L; tick <= 9L; tick++) predictor.RecordInput(tick, 1f, 0f);
            for (var i = 0; i < 10; i++) Tick();

            var entity = Local();
            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(entity);

            Assert.AreEqual(new float2(1f, 1f), anchor.ServerPosition, "the anchor is the wire's value and nothing else");
            Assert.AreEqual(new float3(1f, 0f, 1f), anchor.Position);
            Assert.AreEqual(1u, anchor.Sequence, "no state arrived, so nothing rewrote it — not the predictor, not the renderer");
            Assert.AreNotEqual(1f, predictor.SimulatedPosition.X, "guard: the prediction really did move away, so the assertion above means something");
        }
    }
}
