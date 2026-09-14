using Unity.Entities;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// The presentation pose the server last reported for a replicated entity: which way it
    /// faces, what it is doing, and the counter that says a repeated action is a new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from the transform, deliberately.</b> Position is written to
    /// <c>LocalTransform</c> because every transform system and the view sync already read
    /// it. Facing is NOT: an entity's facing and its movement direction are different facts —
    /// a character strafing or standing still while turning has one and not the other — and
    /// folding facing into the transform rotation here would make that distinction
    /// unrecoverable for a view that wants to blend one and snap the other.
    /// </para>
    /// <para>
    /// <b>Raw wire forms, not decoded.</b> <see cref="FacingBrad"/> stays in biased binary
    /// radians because zero means "not sent" and only that encoding can say so; a float would
    /// make "facing east" and "no facing" the same value. Whether an absent facing should
    /// hold the last one or be derived from movement is a presentation decision, and this
    /// component is not where presentation decisions belong.
    /// </para>
    /// </remarks>
    public struct EntityPose : IComponentData
    {
        /// <summary>
        /// Facing as biased 16-bit binary radians. 0 means "not sent", never "east".
        /// Decode with <c>Cuvara.Netcode.Protocol.FacingCodec</c>.
        /// </summary>
        public uint FacingBrad;

        /// <summary>
        /// What the entity is doing. <see cref="SimAction.Unspecified"/> (0) means "not
        /// sent", never "idle" — idle is 1.
        /// </summary>
        public SimAction Action;

        /// <summary>
        /// Retrigger counter for <see cref="Action"/>. CHANGES every time the entity enters
        /// an action, including re-entering the one it is already in. 0 means "not sent".
        /// </summary>
        /// <remarks>
        /// <b>Compare by inequality, never by greater-than.</b> The counter wraps at 2^32 and
        /// resets when the server restarts or the entity respawns, so a greater-than test
        /// stops retriggering for four billion actions after a single wrap — and nothing
        /// reports an error, because nothing is wrong except the animation that never plays.
        /// </remarks>
        public uint ActionSeq;

        /// <summary>True when the server is sending a facing for this entity.</summary>
        public bool HasFacing => FacingBrad != 0;

        /// <summary>True when the server is sending a retrigger counter for this entity.</summary>
        public bool HasActionSeq => ActionSeq != 0;
    }

    /// <summary>
    /// What a view has already been shown, so a change can be detected without asking the
    /// view what it is doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the last-shown values live in ECS rather than on the view.</b> A view is a
    /// pooled GameObject: it outlives the entity it is bound to and is handed to the next
    /// one, so state kept on it is state that leaks between entities. Keeping it here ties
    /// it to the entity's lifetime by construction — a despawn takes it with it.
    /// </para>
    /// <para>
    /// Separate component rather than fields on <see cref="EntityPose"/> because the two have
    /// different writers: the drain writes the pose from the wire, and the view system writes
    /// this from what it drew. One component with two writers in two system groups is the
    /// shape that produces a race nobody can see.
    /// </para>
    /// </remarks>
    public struct EntityPoseView : IComponentData
    {
        public SimAction ShownAction;

        public uint ShownActionSeq;

        /// <summary>
        /// False until the view has been shown anything at all, so the first pose is a
        /// change rather than a comparison against a default that happens to match.
        /// </summary>
        public bool Initialised;
    }
}
