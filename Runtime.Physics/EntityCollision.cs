using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// One collision pair for one physics step, with its phase. Produced by
    /// <see cref="PhysicsEventCollectorSystem"/> into <see cref="PhysicsEventBuffer"/> and, when a
    /// publisher was supplied, through <c>IDotsPublisher&lt;EntityCollision&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Identity is the full <see cref="Entity"/>, index and version.</b> Entities recycle indices;
    /// a pair keyed on index alone would report a "Stay" between a dead goblin and whatever now
    /// occupies its slot. The tracker keys on <c>(Index, Version)</c>, so a reused index is a new
    /// entity, a new pair, and an <see cref="PhysicsContactPhase.Enter"/>. The legacy
    /// <see cref="EntityIndexA"/>/<see cref="EntityIndexB"/> remain as conveniences.
    /// </para>
    /// <para>
    /// <b>Pair ordering.</b> <see cref="EntityA"/> is always the entity with the lower index (then
    /// lower version). Unity.Physics reports a pair in whichever body order the broadphase produced;
    /// normalising here means a consumer sees one stable pair whichever way round it was found, and
    /// <see cref="Normal"/> always points from A toward B.
    /// </para>
    /// <para>
    /// <b>Aggregation.</b> Unity.Physics raises one event per collider pair per step, and a compound
    /// collider can raise several for one entity pair. They are folded into one event:
    /// <see cref="ContactCount"/> is how many, <see cref="Impulse"/> is their sum,
    /// <see cref="Normal"/> the normalised mean, <see cref="Position"/> the mean contact position.
    /// </para>
    /// </remarks>
    public readonly struct EntityCollision
    {
        public readonly Entity EntityA;
        public readonly Entity EntityB;
        public readonly PhysicsContactPhase Phase;

        /// <summary>Mean contact normal, from A toward B. Zero on <see cref="PhysicsContactPhase.Exit"/>.</summary>
        public readonly float3 Normal;

        /// <summary>Mean contact position this step. Last known on <see cref="PhysicsContactPhase.Exit"/>.</summary>
        public readonly float3 Position;

        /// <summary>Summed estimated impulse of every contact this step. Zero on exit.</summary>
        public readonly float Impulse;

        /// <summary>How many Unity.Physics events were folded into this one. Zero on exit.</summary>
        public readonly int ContactCount;

        /// <summary>
        /// True on an exit caused by one entity no longer existing. The consumer must not touch that
        /// entity's components; <c>EntityManager.Exists</c> says which.
        /// </summary>
        public readonly bool AnyEntityDestroyed;

        public int EntityIndexA => EntityA.Index;

        public int EntityIndexB => EntityB.Index;

        public EntityCollision(
            Entity a, Entity b, PhysicsContactPhase phase, float3 normal, float3 position, float impulse, int contactCount, bool anyEntityDestroyed)
        {
            EntityA = a;
            EntityB = b;
            Phase = phase;
            Normal = normal;
            Position = position;
            Impulse = impulse;
            ContactCount = contactCount;
            AnyEntityDestroyed = anyEntityDestroyed;
        }

        public override string ToString() => $"{Phase} {EntityA}-{EntityB} x{ContactCount} impulse {Impulse:0.###}";
    }
}
