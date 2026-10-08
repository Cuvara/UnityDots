using System.Collections.Generic;
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
using UnityEngine;

namespace Cuvara.DOTS.Tests.Prediction
{
    /// <summary>
    /// <c>LocalPredictionSystem</c> reconciles with the snapshot's own tick and
    /// <c>ack_applied_tick</c>, and steers the predictor's clock, the way netcode's
    /// <c>WorldViewBinder</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong.</b> The system called the two-argument <c>Reconcile(position, ack)</c>: no
    /// snapshot tick, so the predictor's history was never consulted, and nothing steered the
    /// predictor's clock, so it counted base ticks off the frame clock for the whole session. In a
    /// deterministic harness against a server 0.7% slow, the client's tick line ran from 0 to
    /// 22-23 ticks ahead of the server's over 55 seconds -- on the path the game actually runs.
    /// </para>
    /// <para>
    /// The algorithm is netcode's and is measured there (<c>AckAppliedTickReconcileTests</c>); what
    /// is asserted here is the wiring: that the snapshot's tick and <c>ack_applied_tick</c> reach the
    /// predictor, that the clock is steered, and that the steering is reachable for input reports.
    /// </para>
    /// </remarks>
    public sealed class LocalPredictionClockAndOffsetTests
    {
        private const string LocalArchetype = "player-local";
        private const string PlayerType = "player";
        private const int TickRate = 15;

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
            _world = new World("Cuvara.DOTS.PredictionClockTests");
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
            new PredictionSettings(TickRate, 5f, new MapBounds(0f, 0f, 1000f, 1000f)));

        private void Tick(float deltaTime)
        {
            _elapsed += deltaTime;
            _world.SetTime(new TimeData(_elapsed, deltaTime));
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
        }

        private void SpawnLocal(float x, float y)
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.SetState("uuid-me", x, y, 100, 100);
        }

        private void ApplySnapshot(long tick, long ackTick, long ackAppliedTick, float x, float y)
        {
            _worldState.Apply(new ResolvedSnapshot(
                tick,
                ackTick,
                full: true,
                entities: new[] { new ResolvedEntity("uuid-me", PlayerType, x, y, 100, 100, 5f) },
                removed: null,
                events: null,
                ackAppliedTick: ackAppliedTick));
        }

        [Test]
        public void InstallCreatesTheClockSteering_AndItIsReachable()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            Assert.IsTrue(DotsPredictionBootstrap.TryGetClockSteering(_world, out var steering));
            Assert.IsNotNull(steering);

            // Reporting an input is safe whether or not anything has been acknowledged yet.
            Assert.DoesNotThrow(() => DotsPredictionBootstrap.NoteInputSent(_world, 1));
        }

        [Test]
        public void NoteInputSent_WithoutPredictionInstalled_DoesNothing()
        {
            Assert.IsFalse(DotsPredictionBootstrap.TryGetClockSteering(_world, out _));
            Assert.DoesNotThrow(() => DotsPredictionBootstrap.NoteInputSent(_world, 1));
        }

        [Test]
        public void TheSnapshotTick_ReachesThePredictor_SoItsHistoryIsUsed()
        {
            // The two-argument reconcile can never hit the history: it has no tick to look up.
            // With the snapshot's tick, a client standing still at the snapshot's position compares
            // like for like.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            SpawnLocal(10f, 10f);

            ApplySnapshot(tick: 100, ackTick: 0, ackAppliedTick: 0, x: 10f, y: 10f);
            Tick(1f / TickRate); // seed

            for (long t = 101; t <= 110; t++)
            {
                ApplySnapshot(tick: t, ackTick: 0, ackAppliedTick: 0, x: 10f, y: 10f);
                Tick(1f / TickRate);
            }

            Assert.Greater(predictor.Reconciles, 0, "precondition: snapshots were reconciled");
            Assert.Greater(predictor.HistoryHits, 0,
                "with the snapshot tick passed, the predictor answers from its history instead of replaying");
        }

        [Test]
        public void AckAppliedTick_ReachesThePredictor()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            SpawnLocal(10f, 10f);

            ApplySnapshot(tick: 100, ackTick: 0, ackAppliedTick: 0, x: 10f, y: 10f);
            Tick(1f / TickRate); // seed: the predictor's base tick is now the server's

            long recordedOn = predictor.BaseTick;
            predictor.RecordInput(1, 1f, 0f);

            // The server applied input 1 three ticks "earlier" on its own tick line than the client
            // recorded it -- the client leads by three.
            ApplySnapshot(tick: 101, ackTick: 1, ackAppliedTick: recordedOn - 3, x: 10f, y: 10f);
            Tick(0.001f);

            Assert.AreEqual(1, predictor.AckOffsetSamples, "the pair (input 1, its applied tick) was measured");
            Assert.AreEqual(3, predictor.AckTickOffset, "client base tick of the input minus the server tick that applied it");
        }

        [Test]
        public void TheClockIsSteeredOntoTheServers()
        {
            // A client frame clock 5% fast against the server's tick stream. Unsteered, the
            // predictor's base tick would gain 0.05 ticks per frame -- 15 ticks over these 300 frames
            // -- with nothing to bound it. Steered, it stays within a few ticks of the server's tick
            // plus the lead the steering measured.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            Assert.IsTrue(DotsPredictionBootstrap.TryGetClockSteering(_world, out var steering));
            SpawnLocal(10f, 10f);

            long serverTick = 100;
            ApplySnapshot(serverTick, 0, 0, 10f, 10f);
            Tick(1.05f / TickRate);

            var errors = new List<long>();
            for (int frame = 0; frame < 300; frame++)
            {
                serverTick++;
                ApplySnapshot(serverTick, 0, 0, 10f, 10f);
                Tick(1.05f / TickRate);
                if (frame >= 200) errors.Add(predictor.BaseTick - (serverTick + steering.TargetLeadTicks()));
            }

            long worst = 0;
            foreach (long e in errors) worst = System.Math.Max(worst, System.Math.Abs(e));

            Assert.AreEqual(0, predictor.HardResyncs, "steered, not reset");
            Assert.LessOrEqual(worst, 3,
                "the predictor's clock must be steered onto the server's tick line; unsteered it drifts " +
                "15 ticks over this run");
        }
    }
}
