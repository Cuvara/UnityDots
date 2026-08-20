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
    /// The DOTS half of prediction: anchor in, <c>LocalTransform</c> out, and the transform having
    /// exactly one writer at every moment.
    /// </summary>
    /// <remarks>
    /// Driven through the public groups, as the adapter's tests are. The predictor itself is netcode's
    /// and has its own tests; what is asserted here is the wiring — which coordinates it is handed,
    /// when the marker is claimed and released, and that nothing is ever left unwritten.
    /// </remarks>
    public sealed class LocalPredictionSystemTests
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

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.PredictionTests");
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

        private static LocalMovePredictor Predictor(bool enabled = true) => new LocalMovePredictor(
            enabled
                ? new PredictionSettings(15, 5f, new MapBounds(0f, 0f, 1000f, 1000f))
                : default);

        private double _elapsed;

        /// <summary>
        /// One frame. <paramref name="deltaTime"/> is pushed into the world's time because a bare
        /// <c>World</c> has no player loop driving it — <c>SystemAPI.Time.DeltaTime</c> would
        /// otherwise be zero, and <c>LocalMovePredictor.Advance(0)</c> early-returns, so every
        /// frame-rate test would silently assert nothing.
        /// </summary>
        private void Tick(float deltaTime = 0f)
        {
            _elapsed += deltaTime;
            _world.SetTime(new TimeData(_elapsed, deltaTime));
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
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

        private void SpawnLocal(float x = 0f, float y = 0f)
        {
            var view = (IEntityView)_view;
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.SetState("uuid-me", x, y, 100, 100);
        }

        [Test]
        public void EnabledPredictor_ClaimsTheTransform_SoItHasExactlyOneWriter()
        {
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal();
            Tick(0.016f);

            Assert.IsTrue(predictor.IsEnabled, "guard: the settings used here must produce a usable predictor");
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(Local()),
                "the driver must claim the transform before it writes one");
        }

        [Test]
        public void DisabledPredictor_DoesNotClaimTheTransform()
        {
            // The failure this prevents is a frozen avatar: with the marker on and a predictor that
            // writes nothing, LocalTransform has NO writer at all. It surfaces in a build, because a
            // disabled predictor is a runtime configuration rather than a compile-time one.
            var predictor = Predictor(enabled: false);
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal(3f, 7f);
            Tick(0.016f);

            Assert.IsFalse(predictor.IsEnabled, "guard: these settings must produce a disabled predictor");
            Assert.IsFalse(_entityManager.HasComponent<PredictedTransform>(Local()));
        }

        [Test]
        public void DisabledPredictor_LeavesTheAdapterDrivingTheTransform()
        {
            // The other half of the same guarantee: not claiming is only correct if the adapter is
            // still writing. Asserting the marker's absence alone would pass on a frozen avatar.
            DotsPredictionBootstrap.Install(_world, Predictor(enabled: false), _worldState);

            SpawnLocal(3f, 7f);
            Tick(0.016f);

            Assert.AreEqual(new float3(3f, 0f, 7f),
                _entityManager.GetComponentData<LocalTransform>(Local()).Position);
        }

        [Test]
        public void ADisabledPredictorReleasesATransformItHadClaimed()
        {
            // The remove path, which nobody exercises by hand: a predictor that stops predicting
            // mid-session must hand the transform back, or it is frozen from that moment on.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal();
            Tick(0.016f);
            var entity = Local();
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(entity), "guard: claimed first");

            // Swap in a disabled predictor, as a session that turned prediction off would.
            DotsPredictionBootstrap.Install(_world, Predictor(enabled: false), _worldState);
            Tick(0.016f);

            Assert.IsFalse(_entityManager.HasComponent<PredictedTransform>(entity), "released");

            // And the adapter is writing again, which is what "released" has to mean.
            var view = (IEntityView)_view;
            view.SetState("uuid-me", 4f, 5f, 100, 100);
            Tick(0.016f);
            Assert.AreEqual(new float3(4f, 0f, 5f),
                _entityManager.GetComponentData<LocalTransform>(entity).Position);
        }

        [Test]
        public void Uninstall_HandsTheTransformBack_RatherThanLeavingItUnwritten()
        {
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);
            SpawnLocal();
            Tick(0.016f);
            var entity = Local();
            Assert.IsTrue(_entityManager.HasComponent<PredictedTransform>(entity), "guard: claimed");

            DotsPredictionBootstrap.Uninstall(_world);

            Assert.IsFalse(_entityManager.HasComponent<PredictedTransform>(entity),
                "tearing down the driver while the marker is still on would leave no writer at all");
        }

        [Test]
        public void RemoteEntities_AreNeverClaimed()
        {
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);

            var view = (IEntityView)_view;
            view.Spawn("uuid-other", isLocal: false, type: PlayerType);
            view.SetState("uuid-other", 9f, 9f, 100, 100);
            Tick(0.016f);

            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<PredictedTransform>());
            Assert.AreEqual(0, query.CalculateEntityCount(), "nobody predicts other players");
        }

        [Test]
        public void PredictedTransform_IsWrittenThroughTheSameMappingTheAdapterUses()
        {
            // One mapping, read from the view singleton rather than duplicated. If the predicted and
            // authoritative paths ever placed the world differently, the local player would sit at a
            // different offset from everyone else — visible, and hard to attribute.
            var mapping = new SnapshotSpaceMapping(
                new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float3(0f, 0f, 5f));

            var shifted = new DotsEntityView(
                _catalog, new TypeArchetypeResolver(LocalArchetype, LocalArchetype), mapping);
            DotsNetcodeBootstrap.Install(_world, shifted);
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);

            var view = (IEntityView)shifted;
            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.SetState("uuid-me", 0f, 0f, 100, 100);
            Tick(0.016f);

            var position = _entityManager.GetComponentData<LocalTransform>(Local()).Position;
            Assert.AreEqual(mapping.ToWorld(0f, 0f).z, position.z, 1e-4f,
                "the predicted write must go through the session's mapping, not an identity one");
        }

        [Test]
        public void TheAnchorsRawServerCoordinatesAreWhatReachThePredictor()
        {
            // The contract this whole component pair exists for. Asserted indirectly but exactly:
            // with no unacknowledged input, reconciling to the anchor leaves the predictor sitting
            // on the server's own coordinates, so the transform is the mapping of ServerPosition —
            // not of anything recovered from Position.
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);

            SpawnLocal(12.5f, -3.25f);
            Tick(0.016f);

            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(Local());
            Assert.AreEqual(new float2(12.5f, -3.25f), anchor.ServerPosition,
                "guard: the adapter stored the wire value verbatim");
        }

        [Test]
        public void TheTwoAnchorFields_Correspond_UnderAnIdentityMapping()
        {
            // Position is the view's field, ServerPosition is the predictor's, and they must never
            // silently diverge. Asserted under XZPlane specifically, where the mapping is a pure
            // swizzle with no offset, so correspondence is exact and any future change that made
            // one field stop tracking the other fails here rather than showing up as prediction
            // drift. It is NOT an invitation to derive one from the other — see
            // ServerPosition_SurvivesAMappingThatWouldNotRoundTrip for why that is forbidden.
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);

            SpawnLocal(8.25f, -1.5f);
            Tick(0.016f);

            var anchor = _entityManager.GetComponentData<ReconciliationAnchor>(Local());
            Assert.AreEqual(new float3(8.25f, 0f, -1.5f), anchor.Position);
            Assert.AreEqual(new float2(8.25f, -1.5f), anchor.ServerPosition);
            Assert.AreEqual(SnapshotSpaceMapping.XZPlane.ToWorld(anchor.ServerPosition.x, anchor.ServerPosition.y),
                anchor.Position, "the two fields describe the same point in different spaces");
        }

        /// <summary>Feeds the world one snapshot carrying a speed, as the wire would.</summary>
        private void ApplyServerSnapshot(string id, float x, float y, float speed, long tick)
        {
            _worldState.Apply(new ResolvedSnapshot(
                tick,
                ackTick: tick,
                full: true,
                entities: new[] { new ResolvedEntity(id, PlayerType, x, y, 100, 100, speed) },
                removed: null));
        }

        [Test]
        public void TheServersSpeed_ReachesThePredictor_RatherThanTheConstructedFallback()
        {
            // PredictionSettings.Speed is fixed at construction, and a client integrating at a
            // different rate from the server desyncs every tick with NO error on either side. By
            // eye that is indistinguishable from a badly tuned predictor, which is why it is worth
            // a test rather than a comment: nothing else fails when it is wrong.
            var predictor = Predictor();               // constructed at 5
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal();
            ApplyServerSnapshot("uuid-me", 0f, 0f, speed: 7.5f, tick: 1);
            Tick(0.016f);

            Assert.AreEqual(7.5f, predictor.EffectiveSpeed, 1e-4f,
                "the wire value must win over the value the predictor was constructed with");
        }

        [Test]
        public void AServerThatSendsNoSpeed_LeavesTheFallbackStanding()
        {
            // Zero on the wire means "not sent", not "speed is zero". Collapsing to zero would
            // freeze prediction while leaving every counter looking healthy.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);

            SpawnLocal();
            ApplyServerSnapshot("uuid-me", 0f, 0f, speed: 0f, tick: 1);
            Tick(0.016f);

            Assert.AreEqual(5f, predictor.EffectiveSpeed, 1e-4f,
                "a non-positive wire speed must leave the constructed fallback in place");
        }

        [Test]
        public void PredictedTransform_IsRewrittenEveryFrame_EvenWithNoSnapshot()
        {
            // The defect this guards is one layer along from "did we advance": a predictor that
            // advances every frame is still invisible if the transform is only written when a
            // snapshot arrives. Proven by clobbering the transform and running a frame in which NO
            // snapshot is delivered — if the write is snapshot-driven, the sentinel survives.
            DotsPredictionBootstrap.Install(_world, Predictor(), _worldState);
            SpawnLocal();
            Tick(0.016f);

            var entity = Local();
            var sentinel = new float3(-999f, -999f, -999f);
            var clobbered = _entityManager.GetComponentData<LocalTransform>(entity);
            clobbered.Position = sentinel;
            _entityManager.SetComponentData(entity, clobbered);

            Tick(0.016f); // no snapshot, no SetState, no ack — just a frame

            Assert.AreNotEqual(sentinel, _entityManager.GetComponentData<LocalTransform>(entity).Position,
                "the driver must rewrite LocalTransform every frame, not only when a snapshot lands");
        }

        [Test]
        public void PredictedPosition_AdvancesBetweenSnapshots()
        {
            // The 15 Hz-render defect itself. Held input, many frames, and deliberately NOT one
            // snapshot: if Advance were driven by the snapshot drain, the position would be frozen
            // for the whole interval and only jump when a snapshot arrived. Every existing test in
            // this file drives the system by delivering state, so none of them could ever have
            // caught this — the frame loop was not modelled at all until Tick took a deltaTime.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            SpawnLocal();

            // One snapshot first, and this is a correction to the first version of this test rather
            // than a weakening of it. Advancing from a cold start moves nothing: the predictor has
            // no baseline to extrapolate from until a reconcile has happened, so the original
            // version asserted behaviour the predictor does not have and failed for that reason
            // rather than for the defect it targets. Runtime always has a snapshot first — the
            // entity only exists because one arrived — so a baseline is the honest precondition.
            ApplyServerSnapshot("uuid-me", 0f, 0f, speed: 5f, tick: 1);
            Tick(0.016f);

            var entity = Local();
            predictor.RecordInput(2, 1f, 0f);

            // From here: frames only. No snapshot, no ack, nothing for the drain to apply.
            var start = _entityManager.GetComponentData<LocalTransform>(entity).Position;
            for (var frame = 0; frame < 30; frame++) Tick(0.016f);
            var end = _entityManager.GetComponentData<LocalTransform>(entity).Position;

            Assert.AreNotEqual(start, end,
                "with input held and no snapshot delivered, the predicted position must still move — " +
                "otherwise the avatar renders at the snapshot rate however fast the client draws");
        }

        [Test]
        public void ZeroDeltaTime_DoesNotAdvance_SoTheTestsAboveMeasureSomething()
        {
            // Guards the harness rather than the driver. Advance(0) early-returns, so if a future
            // change dropped the SetTime call the two tests above would pass vacuously on a frozen
            // clock. This fails if deltaTime ever stops reaching the predictor.
            var predictor = Predictor();
            DotsPredictionBootstrap.Install(_world, predictor, _worldState);
            SpawnLocal();
            Tick(0.016f);

            ApplyServerSnapshot("uuid-me", 0f, 0f, speed: 5f, tick: 1);
            Tick(0.016f);

            predictor.RecordInput(2, 1f, 0f);
            var before = predictor.Position;
            for (var frame = 0; frame < 30; frame++) Tick(0f);

            Assert.AreEqual(before.X, predictor.Position.X, 1e-6f, "a zero-length frame must not advance");
            Assert.AreEqual(before.Y, predictor.Position.Y, 1e-6f);
        }

        [Test]
        public void NoPredictionInstalled_LeavesEverythingToTheAdapter()
        {
            // The package must behave exactly as it did before this assembly existed when nothing
            // installs a predictor — which is every project that has not opted in.
            SpawnLocal(2f, 6f);
            Tick(0.016f);

            Assert.AreEqual(new float3(2f, 0f, 6f),
                _entityManager.GetComponentData<LocalTransform>(Local()).Position);

            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<PredictedTransform>());
            Assert.AreEqual(0, query.CalculateEntityCount());
        }
    }
}
