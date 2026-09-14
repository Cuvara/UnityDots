using Unity.Collections;
using EventType = Cuvara.Netcode.Protocol.Messages.GameEventType;
using EventFlags = Cuvara.Netcode.Protocol.Messages.GameEventFlags;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// A game event as queued by <see cref="DotsEntityView"/>, before its participants have been
    /// resolved to ECS entities.
    /// </summary>
    /// <remarks>
    /// Resolution is deliberately NOT done at enqueue time. Enqueueing happens on the network
    /// thread, where the entity map is not safe to read, and an entity that has no mirror yet at
    /// that moment may well have one by the time the drain runs — the spawn command for it is
    /// sitting in the other queue. Resolving early would turn the ordinary "event about a
    /// just-spawned entity" case into a permanently unresolved participant.
    /// </remarks>
    public struct PendingNetworkGameEvent
    {
        public EventType Type;

        public FixedString64Bytes SourceId;

        public FixedString64Bytes TargetId;

        public int Amount;

        public uint AbilityId;

        public EventFlags Flags;
    }
}
