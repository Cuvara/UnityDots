using System.Collections.Generic;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Named archetype definitions: the list of <see cref="ViewConfig"/> assets a session can spawn,
    /// each under a stable name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is the join between the server and the client. A snapshot says "goblin"; it does not
    /// know what prefab that is, how many to pool or how the art is offset. This asset is where that
    /// mapping is authored, so adding a creature is an asset edit rather than a code change — which
    /// is the whole point of the item, since without it consumers hardcode keys.
    /// </para>
    /// <para>
    /// Names are distinct from view keys on purpose: two archetypes can share a prefab (a "goblin"
    /// and a "goblin-elite" differing only in scale and pool size), and an archetype can be renamed
    /// in the art pipeline without the server's vocabulary changing.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(menuName = "Cuvara/DOTS/View Archetype Library", fileName = "ViewArchetypeLibrary")]
    public sealed class ViewArchetypeLibrary : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            [Tooltip("Name the server/gameplay layer refers to this archetype by.")]
            public string Name;

            [Tooltip("The view configuration to use.")]
            public ViewConfig Config;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries => entries;

        /// <summary>Replaces the entry list — for tests and for generated libraries.</summary>
        /// <remarks>
        /// Public since 0.15.0, alongside <see cref="ViewConfig.Configure"/> and for the same reason:
        /// a library assembled in code is useless if the configs in it cannot be.
        /// </remarks>
        public void Configure(params Entry[] newEntries)
        {
            entries = new List<Entry>(newEntries);
        }

        /// <summary>
        /// Stable name hash. <see cref="string.GetHashCode()"/> is deliberately not used: it is not
        /// guaranteed stable across runtimes or Unity versions, and this hash is stored in a blob
        /// that outlives the process that built it.
        /// </summary>
        public static int HashName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;

            // FNV-1a, 32-bit. Chosen for being short enough to read and specified precisely enough
            // that two builds of this package agree.
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                var hash = offset;
                foreach (var c in name)
                {
                    hash ^= c;
                    hash *= prime;
                }

                return (int)hash;
            }
        }
    }
}
