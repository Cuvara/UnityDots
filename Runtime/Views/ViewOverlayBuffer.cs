using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Singleton holding the per-frame overlay data. Published by <see cref="ViewOverlaySystem"/>,
    /// read by the host project's UI system to position world-space health bars and name plates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer is rebuilt every frame the view module is installed — it is a snapshot, not an
    /// accumulator, and it is rebuilt (to empty) on the frame the last anchored entity disappears,
    /// so a consumer never sees a stale entry. The host reads it after
    /// <see cref="Groups.ViewTransformSyncGroup"/> (<c>LateUpdate</c>) and before the next frame's
    /// <c>InitializationSystemGroup</c>. The consumer contract — who projects to screen, what happens
    /// behind the camera, distance filtering, cadence, UI recycling — is
    /// <c>Documentation~/MINIMAP-OVERLAY.md</c>, with <see cref="ViewOverlayProjection"/> and
    /// <see cref="ViewOverlayReconciler{TElement}"/> as the helpers that implement it.
    /// </para>
    /// <para>
    /// <b>Managed class, not a struct.</b> A blittable singleton would be ideal, but
    /// <c>NativeList</c> cannot be a component field (it is a container, not a value). This
    /// is the same pattern <see cref="EntityViewRegistryReference"/> uses.
    /// </para>
    /// </remarks>
    public sealed class ViewOverlayBuffer : IComponentData
    {
        /// <summary>
        /// Per-frame overlay entries. Valid from the end of <see cref="ViewOverlaySystem"/>
        /// until the next frame's collect phase clears it.
        /// </summary>
        public NativeList<ViewOverlayData> Entries;

        /// <summary>
        /// Incremented on every rebuild, including one that produced zero entries. Lets a consumer
        /// tell "refreshed and empty" from "not refreshed".
        /// </summary>
        public uint Version;

        /// <summary>Number of entries this frame.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;
    }
}
