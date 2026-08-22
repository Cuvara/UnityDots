using Unity.Entities;

namespace Cuvara.DOTS.Groups
{
    /// <summary>
    /// Where a replicated entity's rendered position is decided: buffered authoritative states are
    /// evaluated against the render clock and written to <c>LocalTransform</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Presentation, not simulation, and that is the load-bearing choice.</b> Interpolation is a
    /// <i>rendering</i> concern — it answers "where is this entity drawn on this frame", which is a
    /// question asked once per drawn frame and never once per simulation step. Putting it in
    /// <see cref="SimulationSystemGroup"/> would tie it to that group's semantics, and to a
    /// fixed-step group it must never inherit: a rendered position evaluated at 60 Hz fixed steps
    /// while the client draws at 144 Hz is the stutter this exists to remove, reintroduced one layer
    /// up.
    /// </para>
    /// <para>
    /// <b>First inside <see cref="ViewSystemGroup"/>, before <see cref="ViewLifecycleGroup"/>.</b>
    /// The transform must be final before anything reads it, and both of the other view groups read
    /// it: <see cref="ViewLifecycleGroup"/> places a newly spawned view from
    /// <c>LocalToWorld</c>, and <see cref="ViewTransformSyncGroup"/> copies it onto every live
    /// GameObject. Running after either would show this frame's views at last frame's position — a
    /// constant one-frame lag on every remote entity, visible as softness rather than as a bug, and
    /// blamed on the interpolation delay rather than on the ordering.
    /// </para>
    /// <para>
    /// <b>Writing <c>LocalTransform</c> in presentation means this group also composes
    /// <c>LocalToWorld</c> itself</b>, because <c>TransformSystemGroup</c> has already run for this
    /// frame and will not run again before the views are read. That is not a shortcut: the netcode
    /// adapter's spawn path already does exactly this, for exactly this reason, and a system that
    /// wrote only <c>LocalTransform</c> here would move the entity and leave every view behind.
    /// </para>
    /// <para>
    /// <b>Empty without <c>com.cuvara.netcode</c></b>, which costs an empty update call and keeps
    /// the ordering surface identical whether or not the optional assembly is installed — the same
    /// rule <see cref="SnapshotApplyGroup"/> follows.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewSystemGroup))]
    [UpdateBefore(typeof(ViewLifecycleGroup))]
    public partial class ViewInterpolationGroup : ComponentSystemGroup
    {
    }
}
