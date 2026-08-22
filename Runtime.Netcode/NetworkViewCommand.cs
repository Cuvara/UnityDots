using Unity.Collections;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>What kind of change a <see cref="NetworkViewCommand"/> describes.</summary>
    internal enum NetworkViewCommandKind : byte
    {
        Spawn = 0,
        State = 1,
        Despawn = 2,
    }

    /// <summary>
    /// One queued <c>IEntityView</c> call, in a form the drain system can apply without touching a
    /// managed object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One struct for all three kinds rather than three types plus a discriminator: the queue is a
    /// FIFO and the order between kinds is the whole guarantee (spawn before its first state,
    /// despawn after the last), so they have to share a lane. Three queues would need a sequence
    /// number to merge them back, which is this field by another name.
    /// </para>
    /// <para>
    /// Unused fields per kind are the cost — a despawn carries four dead floats. At 15 Hz over an
    /// AOI that is a few kilobytes a second of queue traffic, and the alternative costs an
    /// allocation per command.
    /// </para>
    /// </remarks>
    internal struct NetworkViewCommand
    {
        public NetworkViewCommandKind Kind;

        public FixedString64Bytes Id;

        /// <summary>Server entity kind, for <c>NetworkEntity.Type</c>. Spawn only.</summary>
        public FixedString32Bytes Type;

        public bool IsLocal;

        /// <summary>Config table index resolved at enqueue time, or -1 when the entity has no config.</summary>
        public int ConfigIndex;

        /// <summary>View key resolved at enqueue time. Empty when unconfigured.</summary>
        public FixedString64Bytes ViewKey;

        public float X;

        public float Y;

        public int Hp;

        public int MaxHp;

        /// <summary>
        /// Server tick this state was true on, or <c>0</c> for a state whose tick the caller could
        /// not state. State only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero is not a sentinel chosen for convenience; it is the honest value.</b>
        /// <c>IEntityView.SetState</c> carries no tick and cannot be made to, so every state that
        /// arrives through the interface has genuinely unknown timing — the same reason
        /// <see cref="ReconciliationAnchor"/> carries no tick and says so. A state that reaches the
        /// drain with a zero tick is applied exactly as it was before interpolation existed: written
        /// straight to the transform. A state with a real tick is buffered and rendered by
        /// <see cref="RemoteInterpolationSystem"/> instead.
        /// </para>
        /// <para>
        /// Inventing a tick here — from arrival order, or from a counter — would produce a number
        /// that looks authoritative, is not, and would place samples on a timeline the server never
        /// used.
        /// </para>
        /// </remarks>
        public long Tick;

        /// <summary>
        /// Seconds on the caller's monotonic clock when this state was received. State only, and
        /// meaningful only alongside a non-zero <see cref="Tick"/>.
        /// </summary>
        /// <remarks>
        /// Used by the render clock to measure how many real seconds a server tick takes, and for
        /// nothing else — never to place a sample, which is what <see cref="Tick"/> is for. See
        /// <c>InterpolationSample.ReceiveTime</c>, which this becomes verbatim.
        /// </remarks>
        public double ReceiveTime;
    }
}
