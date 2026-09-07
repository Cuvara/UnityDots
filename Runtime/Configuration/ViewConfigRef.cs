using Unity.Entities;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// "Use the config at this index in the session's <see cref="ViewConfigTable"/>."
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate optional component rather than a field on <c>EntityViewRequest</c>, and that is a
    /// deliberate choice about default values. An <c>int</c> field would default to 0, which is a
    /// perfectly valid config index — so an entity that never set it would silently spawn whatever
    /// config happens to be first in the table. Encoding "unset" as -1 or index+1 works but relies on
    /// everyone remembering the encoding. Component presence has no such failure mode: either the
    /// entity has a config or it does not.
    /// </para>
    /// <para>
    /// It also keeps the bare-key path exactly as it was — an entity with only
    /// <c>EntityViewRequest.ViewKey</c> behaves identically to before this component existed.
    /// </para>
    /// <para>
    /// <b>Versioned since 0.28.0.</b> An index alone cannot tell "the goblin" from "whatever is
    /// at slot 3 after the catalog was rebuilt", and the spawn path could not detect the swap — an
    /// in-range wrong index reads as a valid record. <see cref="Version"/> is the catalog's
    /// <see cref="ViewConfigCatalog.Version"/> at the moment the ref was issued; the spawn system
    /// refuses a ref whose version differs from the installed table's and falls back to the
    /// request's own key. Obtain refs from <see cref="ViewConfigCatalog.CreateRef(int)"/> — a
    /// <c>new ViewConfigRef { Index = i }</c> carries version 0, which no built table ever has,
    /// and is therefore always refused. That is deliberate: an unstamped ref is the silent-swap bug
    /// waiting to happen.
    /// </para>
    /// </remarks>
    public struct ViewConfigRef : IComponentData
    {
        public int Index;

        /// <summary>Catalog version the index is valid for. 0 = unstamped, never accepted.</summary>
        public int Version;
    }
}
