using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// One entry in <see cref="MinimapBuffer.Entries"/>: where a marked entity is this frame and how
    /// to tell it apart from the others. Flat struct, no managed references, so the collect job can
    /// write it from Burst.
    /// </summary>
    public struct MinimapEntry
    {
        /// <summary>
        /// The entity, including its version — the stable identity a renderer keys its icon by. Two
        /// lives of one replicated id are two different values here, exactly as they are in
        /// <c>NetworkEntitySpawned.Entity</c>.
        /// </summary>
        public Entity Entity;

        /// <summary>View handle (matches <see cref="EntityViewLink.ViewId"/>), or 0 when the entity has no view this frame.</summary>
        public int ViewId;

        /// <summary><see cref="MinimapMarker.Category"/>, verbatim.</summary>
        public int Category;

        /// <summary>World position projected onto <see cref="MinimapBuffer.Plane"/>.</summary>
        public float2 Position;

        /// <summary>True for the local player's entity.</summary>
        public bool IsLocal;

        /// <summary>
        /// <c>Health.Current / Health.Max</c> in 0–1 when the entity carries the simulation
        /// <c>Health</c> component with a positive maximum; -1 otherwise. Replicated hp lives on
        /// <c>NetworkEntityState</c>, which this core assembly cannot name — a networked host reads
        /// that component by <see cref="Entity"/> if it wants hp on the map.
        /// </summary>
        public float HealthFraction;
    }
}
