using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// One entry in the minimap data buffer. Flat struct for NativeList — no strings,
    /// no managed references.
    /// </summary>
    public struct MinimapEntry
    {
        /// <summary>View handle (matches EntityViewLink.ViewId). 0 = no view.</summary>
        public int ViewId;

        /// <summary>Entity type index (matches EntityType enum: 1=Player, 2=Mob, etc).</summary>
        public int EntityTypeIndex;

        /// <summary>World position (XZ plane).</summary>
        public float2 Position;

        /// <summary>True for the local player.</summary>
        public bool IsLocal;

        /// <summary>Health fraction 0–1. -1 = no health data.</summary>
        public float HealthFraction;
    }
}
