using Unity.Collections;
using Unity.Entities;
using EventType = Cuvara.Netcode.Protocol.Messages.GameEventType;
using EventFlags = Cuvara.Netcode.Protocol.Messages.GameEventFlags;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// One edge-triggered occurrence the server reported, with its participants resolved to
    /// ECS entities where this client has a mirror for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This buffer holds one frame's events and no more.</b> It is cleared at the top of
    /// the drain, so a consumer reads it during the frame it was filled and never sees an
    /// event twice. Events are not state: a keyframe restates the world, not its history, and
    /// an event replayed on a later frame would show a player a hit that happened twice.
    /// </para>
    /// <para>
    /// <b>Entity may be <c>Entity.Null</c> while the id is populated.</b> Three separate
    /// reasons, and a consumer must handle all of them rather than treating the null as an
    /// error: the participant is outside this client's AOI, it has no mirror yet because the
    /// spawn is still in the command queue, or the server sent no participant at all. A damage
    /// number whose attacker is unknown is still a damage number worth drawing — the
    /// alternative is a health bar that drops with no explanation.
    /// </para>
    /// <para>
    /// Ids are <c>FixedString64Bytes</c> so the buffer stays blittable and readable from a
    /// Bursted job; the netcode layer's <c>ResolvedGameEvent</c> carries the managed strings.
    /// </para>
    /// </remarks>
    [InternalBufferCapacity(8)]
    public struct NetworkGameEvent : IBufferElementData
    {
        public EventType Type;

        /// <summary>Mirror of the causer, or <see cref="Entity.Null"/> when there is none.</summary>
        public Entity Source;

        /// <summary>Mirror of the subject, or <see cref="Entity.Null"/> when there is none.</summary>
        public Entity Target;

        /// <summary>Id of the causer. Empty when the server reported none.</summary>
        public FixedString64Bytes SourceId;

        /// <summary>Id of the subject. Empty when the server reported none.</summary>
        public FixedString64Bytes TargetId;

        /// <summary>
        /// Magnitude: damage dealt, health restored, experience gained, level reached.
        /// Meaning is per <see cref="Type"/>.
        /// </summary>
        public int Amount;

        /// <summary>Ability involved, 0 for none.</summary>
        public uint AbilityId;

        public EventFlags Flags;

        public bool HasSource => Source != Entity.Null;

        public bool HasTarget => Target != Entity.Null;

        public bool IsCritical => (Flags & EventFlags.Critical) != 0;
    }
}
