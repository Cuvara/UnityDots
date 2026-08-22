using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// What interpolation last drew for this entity, and at which moment of the server's timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An output, not an input.</b> Nothing in the evaluation reads it — the rendered position is
    /// a pure function of the sample buffer and the render clock, and it must stay that way, because
    /// a renderer that depended on what it drew last frame could not be reasoned about at its edges
    /// and could not be tested at a chosen render tick. This records the answer so that a consumer,
    /// a diagnostic overlay, or a later teleport/hysteresis rule can see it without recomputing it.
    /// </para>
    /// <para>
    /// <b>Added at spawn, alongside <see cref="ReconciliationAnchor"/> and the sample buffer, so the
    /// component set is stable from frame one.</b> That is the rationale the anchor already states
    /// and it applies verbatim here: an entity whose archetype changes on its first state is an
    /// entity every query over mirrors sees in two chunk sets, and a per-entity component added
    /// later is a structural change at snapshot rate.
    /// </para>
    /// <para>
    /// <see cref="HasRendered"/> is false until the buffer holds something evaluable. It is the
    /// honest way to distinguish "drawn at the origin" from "not drawn yet" — a
    /// <see cref="Position"/> of zero cannot, and the origin is a legal place to be.
    /// </para>
    /// </remarks>
    public struct InterpolationState : IComponentData
    {
        /// <summary>Whether interpolation has ever produced a position for this entity.</summary>
        public bool HasRendered;

        /// <summary>
        /// The fractional server tick the last rendered position was evaluated at — the render
        /// clock's own moment, not this entity's newest sample.
        /// </summary>
        public double RenderTick;

        /// <summary>
        /// The last position written to <c>LocalTransform</c>, in world space — so it is already
        /// through <see cref="SnapshotSpaceMapping"/>, unlike the samples it was derived from.
        /// </summary>
        public float3 Position;
    }
}
