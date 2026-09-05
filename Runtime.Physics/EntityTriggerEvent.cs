namespace Cuvara.DOTS.Physics
{
    /// <summary>Published when an entity enters or exits a trigger volume.</summary>
    public readonly struct EntityTriggerEvent
    {
        public readonly int EntityIndexA;
        public readonly int EntityIndexB;
        public readonly bool Entered;

        public EntityTriggerEvent(int a, int b, bool entered)
        { EntityIndexA = a; EntityIndexB = b; Entered = entered; }
    }
}
