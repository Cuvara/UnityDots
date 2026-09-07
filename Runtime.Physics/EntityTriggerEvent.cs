using Unity.Entities;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// One trigger pair for one physics step, with its phase. Same identity, ordering and lifetime
    /// rules as <see cref="EntityCollision"/>; a trigger has no contact geometry to aggregate, so
    /// several Unity.Physics trigger events for one entity pair fold into one with no further data.
    /// </summary>
    public readonly struct EntityTriggerEvent
    {
        public readonly Entity EntityA;
        public readonly Entity EntityB;
        public readonly PhysicsContactPhase Phase;

        /// <summary>True on an exit caused by one entity no longer existing.</summary>
        public readonly bool AnyEntityDestroyed;

        public int EntityIndexA => EntityA.Index;

        public int EntityIndexB => EntityB.Index;

        /// <summary>Kept for readers written against the 0.27 contract: enter is <see cref="PhysicsContactPhase.Enter"/>.</summary>
        public bool Entered => Phase == PhysicsContactPhase.Enter;

        public EntityTriggerEvent(Entity a, Entity b, PhysicsContactPhase phase, bool anyEntityDestroyed)
        {
            EntityA = a;
            EntityB = b;
            Phase = phase;
            AnyEntityDestroyed = anyEntityDestroyed;
        }

        public override string ToString() => $"{Phase} trigger {EntityA}-{EntityB}";
    }
}
