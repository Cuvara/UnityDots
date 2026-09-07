using Unity.Entities;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Every <see cref="ViewConfig"/> the session knows about, as one immutable blob.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the blob buys over putting the config on each entity.</b> A plain
    /// <see cref="IComponentData"/> carrying key, pool size, scale, both offsets and the sorting
    /// pair is around 100 bytes, and it is copied into <i>every</i> entity that uses that config.
    /// A thousand goblins is a thousand identical copies: chunk capacity falls, so the same entities
    /// span more chunks, and every system iterating them touches more cache lines for data that never
    /// varies. With the blob, the entity carries a <see cref="ViewConfigRef"/> — four bytes — and the
    /// shared data exists once per session.
    /// </para>
    /// <para>
    /// It is also the shape the many-per-archetype case wants: a blob is readable from a Bursted job
    /// without a managed lookup, it is immutable so no job needs to guard it, and its lifetime is
    /// explicit rather than tied to a <c>ScriptableObject</c> the GC might be holding.
    /// </para>
    /// <para>
    /// The cost is indirection: reading a config is a blob dereference plus an index, and the blob
    /// must be disposed. <see cref="ViewConfigCatalog"/> owns that lifetime.
    /// </para>
    /// </remarks>
    public struct ViewConfigTable
    {
        /// <summary>
        /// The <see cref="ViewConfigCatalog.Version"/> this table was built at. A
        /// <see cref="ViewConfigRef"/> stamped with a different version is refused by the spawn
        /// path — see <see cref="ViewConfigRef.Version"/>.
        /// </summary>
        public int Version;

        public BlobArray<ViewConfigRecord> Records;

        /// <summary>Index of the record registered under <paramref name="nameHash"/>, or -1.</summary>
        /// <remarks>
        /// Linear: a catalog holds tens of archetypes, not thousands, and a linear scan over a
        /// contiguous blob beats a hash structure at that size while staying trivially Burst-safe.
        /// Resolve by name once at spawn-request time and carry the index, rather than scanning per
        /// frame.
        /// </remarks>
        public int IndexOf(int nameHash)
        {
            for (var i = 0; i < Records.Length; i++)
            {
                if (Records[i].NameHash == nameHash) return i;
            }

            return -1;
        }
    }
}
