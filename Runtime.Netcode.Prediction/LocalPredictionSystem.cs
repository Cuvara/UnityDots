using Cuvara.DOTS.Groups;
using Shared.GameLogic.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Netcode.Prediction
{
    /// <summary>
    /// Drives <c>LocalMovePredictor</c> from ECS: reconciles it against the local entity's
    /// <see cref="ReconciliationAnchor"/>, advances it, and writes the result to
    /// <c>LocalTransform</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The DOTS half of the seam, and only that half.</b> The predictor owns the algorithm — the
    /// input buffer, the replay through <c>TryMove</c>, the smoothing. This system owns reading the
    /// anchor, supplying the tick, claiming and releasing <see cref="PredictedTransform"/>, and
    /// writing the transform. It lives here rather than in netcode because the alternative is
    /// netcode taking a dependency on Entities, and the arrow between these packages is one-way.
    /// </para>
    /// <para>
    /// <b>It reconciles from <c>ServerPosition</c>, never from <c>Position</c>.</b> The predictor
    /// replays the shared simulation, which clamps against map bounds expressed in server
    /// coordinates, so it must be handed the coordinates the server actually sent. <c>Position</c>
    /// is the view's field; recovering server space from it would mean inverting
    /// <c>SnapshotSpaceMapping</c>, and a float round trip through a projection is not bit-exact —
    /// replay would integrate from a position the server never held. That is why the anchor carries
    /// both and why the mapping still has no inverse.
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

        public void OnCreate(ref SystemState state)
        {
            _localEntities = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkEntity, ReconciliationAnchor, LocalTransform>()
                .Build(ref state);

            state.RequireForUpdate<LocalPredictionReference>();
            state.RequireForUpdate(_localEntities);
        }

        public void OnUpdate(ref SystemState state)
        {
            var reference = SystemAPI.ManagedAPI.GetSingleton<LocalPredictionReference>();
            var predictor = reference.Predictor;
            if (predictor == null) return;

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

                var anchor = entityManager.GetComponentData<ReconciliationAnchor>(entity);

                // Only on a new acknowledgement. Reconciling every frame against an unchanged tick
                // would replay the same unacknowledged inputs repeatedly and count corrections that
                // never happened, which is a diagnostics lie as well as wasted work.
                var ackTick = reference.World?.AckTick ?? 0L;
                if (ackTick > _lastAckTick)
                {
                    _lastAckTick = ackTick;

                    // Speed first, then position — the same order WorldViewBinder uses on the
                    // GameObject path, and the order matters: Reconcile replays every
                    // unacknowledged input, so replaying at a stale speed integrates the whole
                    // backlog at the wrong rate.
                    //
                    // It is read here rather than carried on ReconciliationAnchor because the
                    // anchor is written from IEntityView.SetState, which carries no speed — the
                    // wire value only exists on WorldState. And it is read here rather than left
                    // to the consumer because netcode's binder feeds it ONLY in its predictor
                    // overload, which its own docs tell the DOTS path not to use: "hand the
                    // predictor to that system instead". That leaves this system as the only thing
                    // that can.
                    //
                    // Non-positive is ignored inside SetServerSpeed — on the wire that means "not
                    // sent" — so a server that never populates it leaves the constructed fallback
                    // standing rather than collapsing the speed to zero.
                    if (reference.World != null)
                    {
                        // ToString allocates, once per reconcile rather than once per frame: at a
                        // 15 Hz tick that is a handful of short-lived strings a second. Interning
                        // it is a change to make when a profiler says so, not before.
                        var wireId = entityManager.GetComponentData<NetworkEntity>(entity).Id.ToString();
                        if (reference.World.TryGet(wireId, out var snapshot))
                        {
                            predictor.SetServerSpeed(snapshot.Speed);
                        }
                    }

                    predictor.Reconcile(ToVec2(anchor.ServerPosition), ackTick);
                }

                predictor.Advance(SystemAPI.Time.DeltaTime);

                // Mapped here, on the way out, using the same SnapshotSpaceMapping the adapter uses —
                // read from the view singleton rather than duplicated, so the predicted and the
                // authoritative paths cannot drift apart in how they place the world.
                var mapping = SystemAPI.ManagedAPI.GetSingleton<NetworkEntityViewReference>().View.Mapping;
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
