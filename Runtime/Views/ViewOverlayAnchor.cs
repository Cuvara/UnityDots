using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Marks an entity as having a world-space UI anchor point (health bar, name plate,
    /// damage number). The <see cref="WorldOffset"/> is applied relative to the entity's
    /// world position — typically <c>(0, 2, 0)</c> for "above the head".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component carries the anchor position, not the UI itself. The host project's UI
    /// system reads <see cref="ViewOverlayBuffer"/> (populated by <see cref="ViewOverlaySystem"/>)
    /// and positions its own elements (UI Toolkit, UGUI, or IMGUI) at the screen-projected
    /// world position. This package owns the "where in the world" half; the host owns the
    /// "what to draw" half.
    /// </para>
    /// </remarks>
    public struct ViewOverlayAnchor : IComponentData
    {
        /// <summary>Local offset from the entity's world position, in world units.</summary>
        public float3 WorldOffset;
    }
}
