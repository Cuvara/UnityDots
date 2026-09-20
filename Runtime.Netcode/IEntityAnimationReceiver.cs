using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Implemented by a component on a view GameObject that wants to be told what the entity
    /// bound to it is doing. Optional: a view without one is positioned and rotated exactly
    /// as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the seam a content author implements, and it is deliberately tiny.</b> The
    /// package does not own an Animator, a state machine, or a naming convention for
    /// triggers. It owns the one fact a client cannot work out for itself — that the server
    /// says this entity entered an action — and hands it over. Everything past that is
    /// content.
    /// </para>
    /// <para>
    /// <b>Why <paramref name="retriggered"/> is a parameter rather than something the
    /// implementer works out.</b> Deciding "is this a new occurrence" means comparing against
    /// what was last shown, and what was last shown has to be remembered somewhere. A view is
    /// a POOLED GameObject: it outlives the entity bound to it and is handed to the next one,
    /// so state remembered on it leaks across entities — a recycled view would compare a new
    /// entity's first swing against the previous entity's counter and decide it was not new.
    /// The package keeps that memory in ECS, where the entity's lifetime ends it.
    /// </para>
    /// <para>
    /// <b>And the comparison itself is easy to get wrong.</b> The counter wraps at 2^32 and
    /// resets when the server restarts or the entity respawns, so the obvious
    /// <c>seq &gt; lastSeq</c> silently stops retriggering for four billion actions after one
    /// wrap. Putting that rule in one place rather than in every consumer's view script is
    /// the point.
    /// </para>
    /// </remarks>
    public interface IEntityAnimationReceiver
    {
        /// <summary>
        /// The entity's action, once per frame in which it changed OR was re-entered.
        /// </summary>
        /// <param name="action">
        /// What the entity is doing. <see cref="SimAction.Unspecified"/> means the server
        /// sends no action at all — keep showing whatever is on screen rather than falling
        /// back to idle, or a server predating the field freezes every entity mid-animation.
        /// </param>
        /// <param name="retriggered">
        /// True when the entity ENTERED this action again while already in it — a second
        /// swing. An implementation should replay a one-shot animation on true and let a
        /// looping one continue on false.
        /// <para>
        /// False on the first report of a NEW action: that is a transition, and a state
        /// machine driven by <paramref name="action"/> will play it anyway. Passing true
        /// there would make every implementation double-trigger on the transition.
        /// </para>
        /// </param>
        void OnAction(SimAction action, bool retriggered);
    }
}
