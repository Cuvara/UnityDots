using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Opts an entity into the minimap. <see cref="MinimapDataSystem"/> lists exactly the entities
    /// carrying this and a <c>LocalToWorld</c>, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An explicit marker, not "every entity with a view", and the reason is the area of
    /// interest.</b> A minimap that listed every entity in the world would reveal whatever the
    /// server chose not to replicate the moment a consumer created a local entity for it. Listing
    /// only what a producer deliberately marked keeps the decision with the code that knows where the
    /// entity came from: the netcode adapter marks its mirror entities — which exist only while the
    /// server lists them — and a host marks its own local entities only if it means to.
    /// </para>
    /// <para>
    /// <b>Presence, not visibility.</b> An entity whose view is still warming, or that has no view at
    /// all, is on the minimap the moment it carries this component; <see cref="MinimapEntry.ViewId"/>
    /// is 0 for it. A marker on a mirror stops being reported the frame the mirror is destroyed —
    /// which for a replicated entity is the frame it leaves the area of interest.
    /// </para>
    /// </remarks>
    public struct MinimapMarker : IComponentData
    {
        /// <summary>
        /// Presentation category the host maps to an icon or colour — a resolver's answer for
        /// <c>"player"</c>, <c>"mob"</c>, a quest marker. The package assigns no meaning to the value.
        /// </summary>
        public int Category;

        /// <summary>True for the local player's entity, so a renderer can centre or highlight it.</summary>
        public bool IsLocal;
    }
}
