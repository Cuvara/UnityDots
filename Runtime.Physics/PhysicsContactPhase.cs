namespace Cuvara.DOTS.Physics
{
    /// <summary>Where a collision or trigger pair is in its life, as reported once per physics step.</summary>
    public enum PhysicsContactPhase : byte
    {
        /// <summary>The pair was not in contact last step and is now.</summary>
        Enter = 0,

        /// <summary>The pair was in contact last step and still is.</summary>
        Stay = 1,

        /// <summary>
        /// The pair was in contact last step and is not now — because the bodies separated, because a
        /// collider was removed, or because one entity was destroyed (see <c>AnyEntityDestroyed</c> on
        /// the event).
        /// </summary>
        Exit = 2,
    }
}
