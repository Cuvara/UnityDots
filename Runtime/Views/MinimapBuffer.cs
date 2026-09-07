using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Singleton holding per-frame minimap data, read by the host project's minimap renderer.
    /// </summary>
    /// <remarks>
    /// <b>Data contract only, as of 0.27.1.</b> No system in this package populates it: the
    /// <c>MinimapDataSystem</c> earlier documentation named does not exist in the source, and
    /// nothing allocates or disposes <see cref="Entries"/>. A host that wants a minimap owns the
    /// producer, the buffer's lifetime and the projection until a package producer ships — tracked
    /// as D08 in the improvement plan and listed as <i>planned</i> in
    /// <c>Documentation~/SUPPORT-MATRIX.md</c>.
    /// </remarks>
    public sealed class MinimapBuffer : IComponentData
    {
        /// <summary>Per-frame minimap entries. Rebuilt every frame.</summary>
        public NativeList<MinimapEntry> Entries;

        /// <summary>Number of entries this frame.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;
    }
}
