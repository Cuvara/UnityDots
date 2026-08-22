using Cuvara.Netcode.Interpolation;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Every number remote interpolation reads, as one blittable singleton: netcode's tuning struct
    /// and the mapping that turns the server's plane into world space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An <see cref="IComponentData"/>, never a <c>ScriptableObject</c>, and this is not a
    /// preference.</b> The consumer is a <c>[BurstCompile]</c> job, and a Bursted job cannot follow
    /// a managed asset reference at all. Authoring this as an asset would compile, would look
    /// idiomatic, and would fail at Burst compilation time with a message about managed types that
    /// names neither the asset nor the decision that put it there. netcode's own
    /// <see cref="InterpolationConfig"/> documents the same constraint from the other side.
    /// </para>
    /// <para>
    /// <b>The same config type as the GameObject path, on purpose.</b> <c>WorldViewBinder</c> takes
    /// an <see cref="InterpolationConfig"/> in its constructor; this holds one in a component. Two
    /// tuning types would be two sets of defaults, and the first deployment that changed the world
    /// rate would change one of them.
    /// </para>
    /// <para>
    /// <b><see cref="Mapping"/> lives here rather than on each entity</b> because it is a property
    /// of the world, not of an entity — the type's own remarks say so — and because the job needs it
    /// to project the evaluated server-space position. Seeded from
    /// <see cref="DotsEntityView.Mapping"/> by <see cref="DotsNetcodeBootstrap"/>, so the value the
    /// samples were produced against is the value they are rendered against; a second, independently
    /// configured mapping is the kind of divergence that draws every remote entity in the wrong
    /// place while every test that checks one of the two passes.
    /// </para>
    /// </remarks>
    public struct InterpolationSettings : IComponentData
    {
        /// <summary>Netcode's tuning, already through <c>Normalized()</c> when seeded here.</summary>
        public InterpolationConfig Config;

        /// <summary>Where the server's 2D plane lands in the client's world.</summary>
        public SnapshotSpaceMapping Mapping;
    }
}
