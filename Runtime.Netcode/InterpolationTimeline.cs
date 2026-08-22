using Cuvara.Netcode.Interpolation;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// The render timeline for this world: the fractional server tick every remote entity is drawn
    /// at on this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One per world, not one per entity, and the reason is that entities must agree with each
    /// other.</b> Every entity's ticks come off the same server clock, so drawing them against
    /// different moments would render two avatars passing each other at two different instants of
    /// the same world — a disagreement no single entity's motion would ever look wrong for.
    /// netcode's <see cref="InterpolationClock"/> states this as its first invariant; a singleton is
    /// how ECS spells it.
    /// </para>
    /// <para>
    /// <b>Two writers, at two declared points in the frame, and they do different things.</b> The
    /// snapshot drain calls <c>NoteSnapshot</c> in <c>InitializationSystemGroup</c> when a state
    /// arrives — that moves the clock's <i>target</i> and refines its seconds-per-tick estimate.
    /// <c>InterpolationClockSystem</c> calls <c>Advance</c> in <c>ViewInterpolationGroup</c> once
    /// per frame — that moves the clock itself. The package's one-writer-per-component rule is about
    /// two systems racing to own the same value; this is one value with one owner per phase, and
    /// collapsing them into a single writer is impossible because an arrival is not a frame.
    /// </para>
    /// <para>
    /// <b>Blittable, so a Bursted job can read it.</b> <see cref="InterpolationClock"/> is
    /// <c>bool</c>, <c>long</c> and <c>double</c> only, exactly so it can be carried in a component
    /// and copied into a job field. A managed clock object would have forced the evaluation onto the
    /// main thread, which is the whole cost this design avoids.
    /// </para>
    /// </remarks>
    public struct InterpolationTimeline : IComponentData
    {
        /// <summary>Netcode's render clock: never snapped, only dilated, strictly increasing.</summary>
        public InterpolationClock Clock;
    }
}
