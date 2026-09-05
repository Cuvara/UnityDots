using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Singleton holding per-frame minimap data. Populated by <see cref="MinimapDataSystem"/>,
    /// read by the host project's minimap renderer.
    /// </summary>
    public sealed class MinimapBuffer : IComponentData
    {
        /// <summary>Per-frame minimap entries. Rebuilt every frame.</summary>
        public NativeList<MinimapEntry> Entries;

        /// <summary>Number of entries this frame.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;
    }
}
