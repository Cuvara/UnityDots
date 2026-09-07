using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// One entry in the per-frame overlay buffer. The host project's UI system reads this
    /// to position world-space UI elements (health bars, name plates).
    /// </summary>
    public struct ViewOverlayData
    {
        /// <summary>
        /// The anchored entity, including its version — the stable identity a consumer keys its UI
        /// element by. <see cref="ViewId"/> changes when a view is recycled and re-acquired for the
        /// same entity (a chunk release, an external destroy); this does not.
        /// </summary>
        public Unity.Entities.Entity Entity;

        /// <summary>View handle — matches <see cref="EntityViewLink.ViewId"/>.</summary>
        public int ViewId;

        /// <summary>World-space anchor position (entity position + offset).</summary>
        public float3 WorldPosition;

        /// <summary>Health fraction 0–1. -1 means "no health data available".</summary>
        public float HealthFraction;
    }
}
