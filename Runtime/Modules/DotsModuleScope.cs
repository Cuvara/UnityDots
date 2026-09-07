namespace Cuvara.DOTS.Modules
{
    /// <summary>
    /// Who owns an installed module's lifetime, recorded on the module's registration entity so the
    /// answer lives in the <see cref="Unity.Entities.World"/> and dies with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is the one the client's DI layout already draws: the root scope owns what
    /// outlives a scene (the view registry, pools, the view bootstrap), a scene or session scope owns
    /// what belongs to one connection in one scene (the catalog, the netcode adapter, camera follow).
    /// Recording it lets <see cref="DotsModules.UninstallScope"/> take down exactly the
    /// session-scoped half on a scene reload without touching what other scenes stand on.
    /// </para>
    /// <para>
    /// Recording it <i>in the world</i> rather than in a static table is what guarantees no reference
    /// to an old world survives into the next session: when the world is disposed, the records go
    /// with it, and there is nothing left to hold a stale <c>World</c>.
    /// </para>
    /// </remarks>
    public enum DotsModuleScope
    {
        /// <summary>Owned by the composition root; survives scene loads. Torn down with the world.</summary>
        Root = 0,

        /// <summary>Owned by one scene or one network session. Torn down on scene unload or disconnect.</summary>
        Session = 1,
    }
}
