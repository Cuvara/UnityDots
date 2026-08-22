using Cuvara.Netcode.Interpolation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Draws every remote mirror at the moment the render clock names: evaluate the buffered
    /// samples, project into world space, write the transform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no interpolation arithmetic in this file, and there must never be.</b> The whole
    /// body of the maths is <c>SnapshotInterpolation.Evaluate</c> in <c>com.cuvara.netcode</c> —
    /// bracketing, the tick fraction, the extrapolation cap, the single-sample and empty-buffer
    /// cases. The GameObject path calls that same method over a pooled array; this calls it over
    /// chunk memory. If a lerp ever appears here, the client and the renderer have started keeping
    /// two opinions about where an entity is, and the day they disagree the symptom will be a
    /// remote avatar in the wrong place with both implementations passing their own tests.
    /// </para>
    /// <para>
    /// <b><c>WithNone&lt;PredictedTransform&gt;</c> is the one-writer rule, not an optimisation.</b>
    /// A predictor takes ownership of <c>LocalTransform</c> by adding that tag, and this job must
    /// respect the claim exactly as the snapshot drain already does. Both writing it would work on
    /// every frame the predictor ran and lose on the frames it did not — an intermittent snap on the
    /// one entity the player is holding a key to move, felt rather than seen, and blamed on
    /// prediction.
    /// </para>
    /// <para>
    /// <b><c>LocalToWorld</c> is composed here rather than left to <c>TransformSystemGroup</c>.</b>
    /// That group runs at the end of <see cref="SimulationSystemGroup"/> and has already finished
    /// for this frame by the time presentation starts, so a job that wrote only
    /// <c>LocalTransform</c> would move the entity and leave the view a frame behind it — every
    /// frame, for every remote entity. The netcode drain composes it for the same reason, and this
    /// mirrors that code deliberately, including reading rotation and scale back rather than
    /// resetting them: the wire carries neither, so overwriting them would undo whatever a
    /// consumer's system did.
    /// </para>
    /// <para>
    /// <b>An empty buffer is a legal state, not an error.</b> An entity spawned this frame whose
    /// first state has not arrived, or one whose only states came without a tick, has nothing to
    /// evaluate; <c>Evaluate</c> reports false and this leaves the transform exactly as the drain
    /// left it. That is what keeps the untimed path working unchanged rather than freezing every
    /// entity at the origin.
    /// </para>
    /// </remarks>
    [BurstCompile]
    [WithNone(typeof(PredictedTransform))]
    internal partial struct RemoteInterpolationJob : IJobEntity
    {
        /// <summary>This frame's render moment, copied from the singleton once per schedule.</summary>
        public InterpolationClock Clock;

        /// <summary>Tuning, copied from the singleton once per schedule.</summary>
        public InterpolationConfig Config;

        /// <summary>Server plane to world space. Applied after evaluation, never before.</summary>
        public SnapshotSpaceMapping Mapping;

        private void Execute(
            in DynamicBuffer<SnapshotSample> samples,
            ref LocalTransform transform,
            ref LocalToWorld localToWorld,
            ref InterpolationState state)
        {
            // The shared core, over chunk memory. A generic struct argument, so this is a
            // constrained call Burst specialises rather than an interface dispatch that boxes.
            if (!SnapshotInterpolation.Evaluate(
                    new SnapshotSampleBuffer(samples), Clock, Config, out var x, out var y))
            {
                return;
            }

            var position = Mapping.ToWorld(x, y);

            transform.Position = position;
            localToWorld.Value = float4x4.TRS(
                position,
                transform.Rotation,
                new float3(transform.Scale, transform.Scale, transform.Scale));

            state.HasRendered = true;
            state.RenderTick = Clock.RenderTick;
            state.Position = position;
        }
    }
}
