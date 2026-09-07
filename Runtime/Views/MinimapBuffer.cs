using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Singleton holding this frame's minimap entries. Populated by <see cref="MinimapDataSystem"/>;
    /// read by the host project's minimap renderer, which owns projection to map space and drawing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A snapshot, rebuilt every frame the system runs.</b> <see cref="Entries"/> is cleared and
    /// refilled from the current set of <see cref="MinimapMarker"/> entities on every update — the
    /// system runs even when that set is empty, so the frame the last marked entity disappears is
    /// the frame the buffer reads zero. A consumer can therefore never see a stale marker: an entry
    /// present in the buffer describes an entity that existed when <c>ViewTransformSyncGroup</c> ran
    /// this frame. Read it after <c>PresentationSystemGroup</c> (<c>LateUpdate</c>) and before the
    /// next frame's <c>InitializationSystemGroup</c>.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> The native list is allocated by <c>MinimapBootstrap.Install</c>, released by
    /// <c>MinimapBootstrap.Uninstall</c>, and — if the world is disposed without an uninstall — by
    /// <see cref="MinimapDataSystem"/>'s <c>OnDestroy</c>. Both paths check <c>IsCreated</c> and reset
    /// the field, so disposal is idempotent whichever runs first. Nothing else may dispose or resize
    /// it; a consumer holds the reference only to read.
    /// </para>
    /// <para>
    /// Managed class rather than a blittable struct for the reason <see cref="ViewOverlayBuffer"/>
    /// gives: a <see cref="NativeList{T}"/> cannot be a component field.
    /// </para>
    /// </remarks>
    public sealed class MinimapBuffer : IComponentData
    {
        /// <summary>This frame's entries, in no particular order. Valid until the next rebuild.</summary>
        public NativeList<MinimapEntry> Entries;

        /// <summary>Which world axes <see cref="MinimapEntry.Position"/> was taken from.</summary>
        public MinimapPlane Plane;

        /// <summary>
        /// Incremented on every rebuild, including one that produced zero entries. A renderer that
        /// caches icons compares this to know the buffer was refreshed rather than merely unchanged.
        /// </summary>
        public uint Version;

        /// <summary>Number of entries this frame; 0 when the buffer has been released.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;
    }
}
