using Cuvara.DOTS.Groups;
using Shared.GameLogic.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Netcode.Prediction
{
    /// <summary>
    /// Drives <c>LocalMovePredictor</c> from ECS: steers its clock and reconciles it once per merged
    /// snapshot, advances it every frame, and writes the result to <c>LocalTransform</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The DOTS half of the seam, and only that half.</b> The predictor owns the algorithm — the
    /// input buffer, the history, the replay through <c>TryMove</c>, the smoothing; netcode's
    /// <c>PredictionClockSteering</c> owns keeping its clock on the server's. This system owns
    /// feeding both from the merged snapshot, claiming and releasing
    /// <see cref="PredictedTransform"/>, and writing the transform. It lives here rather than in netcode because the alternative is
    /// netcode taking a dependency on Entities, and the arrow between these packages is one-way.
    /// </para>
    /// <para>
    /// <b>It reconciles from the server's own coordinates, never from <c>Position</c>.</b> The
    /// predictor replays the shared simulation, which clamps against map bounds expressed in server
    /// coordinates, so it must be handed the coordinates the server actually sent. <c>Position</c>
    /// is the view's field; recovering server space from it would mean inverting
    /// <c>SnapshotSpaceMapping</c>, and a float round trip through a projection is not bit-exact —
    /// replay would integrate from a position the server never held. Those coordinates are taken
    /// from the <c>WorldState</c> entry of the snapshot being reconciled, which also supplies its
    /// tick, ack and <c>ack_applied_tick</c>, so all four describe one snapshot;
    /// <see cref="ReconciliationAnchor.ServerPosition"/> holds the same wire floats but no tick,
    /// and is used only when the local entity is not in <c>WorldState</c>.
    /// </para>
    /// <para>
    /// <b>It compares against the predictor's history and steers its clock</b>, exactly as
    /// <c>WorldViewBinder</c> does for a GameObject view: the snapshot's base tick and
    /// <c>ack_applied_tick</c> go to the <c>Reconcile</c> overloads that take them, and
    /// <c>PredictionClockSteering.OnSnapshot</c> keeps the client's tick line on the server's.
    /// Before this the DOTS path did neither, and its clock ran away from the server's by
    /// 22-23 ticks over 55 seconds against a server 0.7% slow.
    /// </para>
    /// <para>
    /// <b>It also feeds the server's speed.</b> <c>PredictionSettings.Speed</c> is fixed at
    /// construction, and a client integrating at a different rate from the server desyncs every
    /// tick with no error on either side — the failure looks exactly like a badly tuned predictor.
    /// The wire has carried per-entity speed since netcode 0.8.0, so this system reads it from
    /// <c>WorldState</c> and calls <c>SetServerSpeed</c> before each reconcile.
    /// </para>
    /// <para>
    /// <b>Input is not sampled here.</b> <c>RecordInput</c> belongs to whatever reads the device and
    /// sends the move to the server, because the tick it records must be the tick that was sent. A
    /// system that invented its own input would produce a buffer the server never saw, and replay
    /// against it would diverge by construction.
    /// </para>
    /// <para>
    /// Not Bursted: it reaches a managed predictor through a managed singleton.
    /// </para>
    /// <para>
    /// <b>Never parallelise this, and the reason is not performance.</b> One predictor instance owns
    /// an input ring buffer, and <c>RecordInput</c>, <c>Reconcile</c> and <c>Advance</c> are
    /// order-dependent against it — <c>Reconcile</c> replays the entire unacknowledged backlog in
    /// sequence. Running those from a worker thread, or for several entities concurrently, would not
    /// crash: it would produce a plausible wrong position. That failure shape — a wrong answer rather
    /// than an exception — is the one this project has paid for most often, and there is nothing to
    /// gain in exchange, because exactly one entity is ever predicted.
    /// </para>
    /// </remarks>
    // In PredictionSystemGroup, which runs after SnapshotApplyGroup — so the anchor read here was
    // written by this frame's snapshot, not the previous one. Reconciling against a stale anchor is
    // a one-frame-old correction, which looks like mistuned prediction rather than like a bug.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictionSystemGroup))]
    internal partial struct LocalPredictionSystem : ISystem
    {
        private EntityQuery _localEntities;
        private long _lastAckTick;

        /// <summary>Anchor sequence at the last reconcile, so an ack without a fresh anchor waits a frame.</summary>
        private uint _lastAnchorSequence;

        /// <summary>
        /// <c>WorldState.Tick</c> at the last snapshot reconcile: each merged snapshot is folded in
        /// exactly once.
        /// </summary>
        private long _lastWorldTick;

        /// <summary><see cref="DotsEntityView.Generation"/> the ack/anchor memory belongs to.</summary>
        private int _lastGeneration;

        public void OnCreate(ref SystemState state)
        {
            _localEntities = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkEntity, ReconciliationAnchor, LocalTransform>()
                .Build(ref state);

            state.RequireForUpdate<LocalPredictionReference>();
            state.RequireForUpdate<NetworkEntityViewReference>();
            state.RequireForUpdate(_localEntities);
        }

        public void OnUpdate(ref SystemState state)
        {
            var reference = SystemAPI.ManagedAPI.GetSingleton<LocalPredictionReference>();
            var predictor = reference.Predictor;
            if (predictor == null) return;

            var view = SystemAPI.ManagedAPI.GetSingleton<NetworkEntityViewReference>().View;
            if (view == null) return;

            // A new generation is a new server session: its ack ticks start wherever that server
            // is, possibly below the last one seen, and the anchor sequence restarts with the new
            // mirror. Carrying the old memory across would make `ackTick > _lastAckTick` false for
            // the rest of the session and silently disable reconciliation after every reconnect.
            // The world tick and the clock measurements belong to the old route for the same
            // reason.
            //
            // A world tick that went BACKWARDS is the same event seen from WorldState (it was
            // Reset for a new join or a map transfer) and is treated the same way.
            var worldState = reference.World;
            if (view.Generation != _lastGeneration
                || (worldState != null && worldState.Tick < _lastWorldTick))
            {
                _lastGeneration = view.Generation;
                _lastAckTick = 0L;
                _lastAnchorSequence = 0u;
                _lastWorldTick = 0L;
                reference.Steering?.Reset();
            }

            var entityManager = state.EntityManager;
            var entities = _localEntities.ToEntityArray(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (!entityManager.GetComponentData<NetworkEntity>(entity).IsLocal) continue;

                if (!predictor.IsEnabled)
                {
                    // Release the transform rather than leaving the marker on. A disabled predictor
                    // writes nothing, so a marker left behind would leave LocalTransform with NO
                    // writer at all and freeze the avatar in place — the marker's failure mode
                    // reached from the other side, and one that surfaces in a build rather than in a
                    // test, because a disabled predictor is a runtime configuration.
                    if (entityManager.HasComponent<PredictedTransform>(entity))
                    {
                        entityManager.RemoveComponent<PredictedTransform>(entity);
                    }

                    continue;
                }

                // Claimed before the first write, so the adapter stops writing LocalTransform in the
                // same frame this system starts. Claiming after would give both a writer for one
                // frame, which is the two-writer race this whole component pair exists to prevent.
                if (!entityManager.HasComponent<PredictedTransform>(entity))
                {
                    entityManager.AddComponent<PredictedTransform>(entity);
                }

                // THE SNAPSHOT PATH: once per merged snapshot, everything from that one snapshot.
                //
                // This used to reconcile through the two-argument Reconcile(position, ack): no
                // snapshot tick, so the predictor's history was never consulted and every
                // reconcile replayed instead; and nothing steered the predictor's clock, so it
                // counted base ticks off the client's frame clock for the whole session. Measured
                // against a server running 0.7% slow, the client's tick line ran 22-23 ticks ahead
                // of the server's over 55 seconds -- that much latency, and before
                // ack_applied_tick that much error at every start and stop, on the path the game
                // actually runs. WorldViewBinder had both all along; this is the same sequence,
                // through the same code (PredictionClockSteering), for a consumer that binds views
                // itself.
                //
                // Position, tick, ack and ack_applied_tick are all read from WorldState here, not
                // the position from the anchor. They must describe ONE snapshot, and the anchor
                // carries no tick (IEntityView.SetState has none to give it): it is refreshed by
                // the binder on its own schedule, so pairing it with WorldState's tick can pair a
                // position from one snapshot with the tick of the next -- a whole snapshot interval
                // of motion reported as a correction. WorldState's entry holds the same wire
                // floats the anchor's ServerPosition does, bit for bit, so nothing is lost.
                bool reconciled = false;
                if (worldState != null && worldState.Tick > 0L && worldState.Tick > _lastWorldTick)
                {
                    // ToString allocates, once per snapshot rather than once per frame: at a 15 Hz
                    // world rate that is a handful of short-lived strings a second. Interning it is
                    // a change to make when a profiler says so, not before.
                    var wireId = entityManager.GetComponentData<NetworkEntity>(entity).Id.ToString();
                    if (worldState.TryGet(wireId, out var snapshot))
                    {
                        long worldTick = worldState.Tick;
                        long ackTick = worldState.AckTick;
                        _lastWorldTick = worldTick;
                        _lastAckTick = ackTick;
                        _lastAnchorSequence = entityManager.GetComponentData<ReconciliationAnchor>(entity).Sequence;

                        // Speed first, then the clock, then position — the order WorldViewBinder
                        // uses. Reconcile replays unacknowledged inputs, so replaying at a stale
                        // speed integrates the whole backlog at the wrong rate. Non-positive is
                        // ignored inside SetServerSpeed: on the wire that means "not sent".
                        predictor.SetServerSpeed(snapshot.Speed);

                        // Seeds the predictor's base tick once (idempotent), measures the snapshot
                        // age and acknowledgement floor, feeds a corroborated rate forward and
                        // steers the phase -- see PredictionClockSteering. Time is the DOTS world's
                        // own clock, the one Advance is fed from below; NoteInputSent must use the
                        // same one (DotsPredictionBootstrap.NoteInputSent does).
                        double now = SystemAPI.Time.ElapsedTime;
                        var steering = reference.Steering;
                        if (steering != null)
                        {
                            steering.SampleTickRate(worldTick, now);
                            steering.OnSnapshot(predictor, worldTick, ackTick, now);
                        }
                        else
                        {
                            predictor.SeedBaseTick(worldTick);
                        }

                        // The snapshot's own tick and ack_applied_tick (0 from a protocol 2 server,
                        // which the predictor treats as "compare at the snapshot tick", as before).
                        long ackApplied = worldState.AckAppliedTick;

                        // Protocol 3 (ADR-28): a predictor running the 3D motor must be corrected in
                        // height too, or a missed landing or a server-side step-up is never repaired
                        // and the local player floats or sinks for the rest of the session.
                        if (predictor.UsesCharacterMotor)
                        {
                            predictor.Reconcile(
                                new Vec3(snapshot.X, snapshot.Y, snapshot.Z), snapshot.VelZ,
                                ackTick, worldTick, ackApplied);
                        }
                        else
                        {
                            predictor.Reconcile(new Vec2(snapshot.X, snapshot.Y), ackTick, worldTick, ackApplied);
                        }

                        reconciled = true;
                    }
                }

                // THE FALLBACK: the local entity is not in WorldState (a view fed by something other
                // than the binder) -- the previous behaviour, unchanged. Only on a new
                // acknowledgement AND a fresh anchor: reconciling every frame against an unchanged
                // tick would replay the same unacknowledged inputs repeatedly and count corrections
                // that never happened, and an ack that advanced while the anchor did not would pair
                // the new tick with the previous snapshot's position. An anchor at sequence zero has
                // never received a state and is the spawn placeholder, never something to rewind to.
                if (!reconciled)
                {
                    var anchor = entityManager.GetComponentData<ReconciliationAnchor>(entity);
                    var ackTick = worldState?.AckTick ?? 0L;
                    if (ackTick > _lastAckTick && anchor.Sequence != 0u && anchor.Sequence != _lastAnchorSequence)
                    {
                        _lastAckTick = ackTick;
                        _lastAnchorSequence = anchor.Sequence;
                        if (worldState != null) predictor.SeedBaseTick(worldState.Tick);
                        predictor.Reconcile(ToVec2(anchor.ServerPosition), ackTick);
                    }
                }

                predictor.Advance(SystemAPI.Time.DeltaTime);

                // Mapped here, on the way out, using the same SnapshotSpaceMapping the adapter uses —
                // read from the view singleton rather than duplicated, so the predicted and the
                // authoritative paths cannot drift apart in how they place the world.
                var mapping = view.Mapping;
                var predicted = predictor.Position;

                var transform = entityManager.GetComponentData<LocalTransform>(entity);
                transform.Position = mapping.ToWorld(predicted.X, predicted.Y);
                entityManager.SetComponentData(entity, transform);

                entityManager.SetComponentData(entity, new LocalToWorld
                {
                    Value = float4x4.TRS(
                        transform.Position,
                        transform.Rotation,
                        new float3(transform.Scale, transform.Scale, transform.Scale)),
                });
            }

            entities.Dispose();
        }


        /// <summary>
        /// The one conversion site between ECS maths and the shared simulation's vector type.
        /// </summary>
        /// <remarks>
        /// <c>SimConversions</c> in <c>Cuvara.DOTS.GameLogic</c> already has this extension method,
        /// and it is <c>internal</c> — reaching it would mean either widening that assembly's public
        /// API or an <c>InternalsVisibleTo</c> grant that couples two independently gated assemblies,
        /// for one line. Kept as a single private site here instead, which is what "convert at the
        /// boundary, not once per call" is actually asking for.
        /// </remarks>
        private static Vec2 ToVec2(in float2 value) => new Vec2(value.x, value.y);
    }
}
