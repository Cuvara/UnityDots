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
    /// The buffer is rebuilt every frame — it is a snapshot, not an accumulator. The host reads
    /// it after <see cref="Groups.ViewTransformSyncGroup"/> and before the next frame's
    /// <c>SimulationSystemGroup</c>.
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

        /// <summary>Number of entries this frame.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;
    }
}
