using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// The last authoritative position the server reported for this entity, in world space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named for what a predictor does with it, not for where it came from.</b> "Network
    /// position" or "server position" would describe the source and invite the obvious wrong move —
    /// someone deciding the local entity looks stale and writing this straight into
    /// <c>LocalTransform</c>. This is the value a prediction layer <i>rewinds to and replays from</i>.
    /// The name is meant to make that misuse read as wrong before anyone runs it.
    /// </para>
    /// <para>
    /// <b>It exists because two writers to one component is the failure shape this workspace keeps
    /// paying for.</b> Once prediction owns the local player's per-frame position, prediction and
    /// this adapter would both write <c>LocalTransform</c> in the same frame — the adapter first, in
    /// initialization, prediction second, in simulation. That *works*, on every frame prediction
    /// runs, and fails only on the frames it does not: the avatar snaps back to the server position
    /// for one frame. Intermittent, visible only as feel, local player only, and it presents as a
    /// prediction bug. Splitting the value out gives each component exactly one writer.
    /// </para>
    /// <para>
    /// <b>Written for every replicated entity, not only predicted ones — and the reason is chunk
    /// layout, not tidiness.</b> Adding this only to predicted entities would give local and remote
    /// mirrors <i>different archetypes</i>. Two archetypes are two sets of chunks, and every query
    /// over mirror entities then iterates both, in a package whose entire justification is chunk
    /// iteration. That is a structural cost paid at query time by every system, to save one
    /// <see cref="float3"/> per entity. Uniform keeps one archetype and pays 12 bytes.
    /// </para>
    /// <para>
    /// So: nothing consumes this for remotes today and nothing has to. If you are reading this
    /// because the redundancy looked wasteful — that instinct is right about the bytes and wrong
    /// about the cost, and splitting the archetype is the expensive half.
    /// </para>
    /// <para>
    /// <b>Two positions, and the raw one is not redundant.</b> <see cref="Position"/> is world
    /// space and is what <c>LocalTransform</c> wants. <see cref="ServerPosition"/> is the
    /// <c>(x, y)</c> the wire actually carried, stored verbatim before
    /// <see cref="SnapshotSpaceMapping"/> touches it, and it is what a predictor wants — a predictor
    /// rewinds in the space it simulates in, and the shared simulation clamps against map bounds
    /// expressed in server coordinates. <c>LocalMovePredictor.Reconcile</c> takes a server-space
    /// <c>Vec2</c> for exactly that reason.
    /// </para>
    /// <para>
    /// <b>Why not invert the mapping instead.</b> <c>dot(p - Origin, Right)</c> is one line, and it
    /// was rejected: a float round trip through a projection is <b>not bit-exact</b>. The recovered
    /// value would differ in the last place, replay would integrate from a position the server never
    /// held, and the result is sub-ULP drift in the one system whose entire justification is
    /// bit-exactness — most likely diagnosed as FMA contraction, in a different package, by someone
    /// who never saw the inverse. Eight bytes per mirror entity removes the possibility rather than
    /// making it unlikely. It is also why <see cref="SnapshotSpaceMapping"/> still has no inverse:
    /// adding one would put the trap back within reach.
    /// </para>
    /// <para>
    /// <b>No guessed tick.</b> <see cref="Tick"/> is non-zero only when the caller stated one. A reconciliation anchor is a
    /// position <i>at a tick</i>, and this adapter genuinely does not know the tick:
    /// <c>IEntityView.SetState</c> carries <c>(id, x, y, hp, maxHp)</c> and nothing else. The tick a
    /// predictor needs is <c>WorldState.AckTick</c> — "the newest input tick the server accepted for
    /// this player", which netcode already surfaces and documents as exactly this, and which a
    /// predictor reads from netcode directly. Inventing a tick here, or guessing one from arrival
    /// order, would produce a number that looks authoritative and is not.
    /// </para>
    /// </remarks>
    public struct ReconciliationAnchor : IComponentData
    {
        /// <summary>
        /// The server's position, already through <see cref="SnapshotSpaceMapping"/> — so it is in
        /// the same space as <c>LocalTransform.Position</c> and needs no further conversion.
        /// </summary>
        public float3 Position;

        /// <summary>
        /// The server's own <c>(x, y)</c>, exactly as <c>IEntityView.SetState</c> delivered it —
        /// no mapping, no round trip, no arithmetic of any kind.
        /// </summary>
        /// <remarks>
        /// This is the field a predictor reconciles against, because the shared simulation it
        /// replays runs in these coordinates. Storing it rather than recovering it from
        /// <see cref="Position"/> is a bit-exactness decision, not a convenience one — see the
        /// type's remarks.
        /// </remarks>
        public float2 ServerPosition;

        /// <summary>
        /// Incremented by the drain on every authoritative state written to this anchor; zero until
        /// the first state lands. A predictor reconciles only when this has moved since its last
        /// reconcile, so a newer <c>WorldState.AckTick</c> paired with an anchor the drain has not
        /// refreshed yet — the binder ticked between the drain and the predictor — waits one frame
        /// instead of replaying against the previous snapshot's position.
        /// </summary>
        public uint Sequence;

        /// <summary>
        /// The server tick the state was true on, when the caller stated one through
        /// <c>DotsEntityView.SetStateAtTick</c>; <c>0</c> for a state that arrived without a tick.
        /// Diagnostic and honest, never invented: see the type remarks for why this component
        /// carries no guessed tick, and <c>NetworkViewCommand.Tick</c> for the same rule at the
        /// queue.
        /// </summary>
        public long Tick;
    }
}
