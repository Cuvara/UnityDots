namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Published when a server entity appears in the client's world (snapshot introduces
    /// a new id). Game code reacts to this for spawn VFX, sound, minimap updates.
    /// </summary>
    public readonly struct NetworkEntitySpawned
    {
        /// <summary>Server entity id.</summary>
        public readonly string EntityId;
        /// <summary>Server entity type (e.g. "player", "mob").</summary>
        public readonly string EntityType;
        /// <summary>Whether this is the local player.</summary>
        public readonly bool IsLocal;

        public NetworkEntitySpawned(string entityId, string entityType, bool isLocal)
        {
            EntityId = entityId;
            EntityType = entityType;
            IsLocal = isLocal;
        }
    }

    /// <summary>
    /// Published when a server entity disappears from the client's world (removed from
    /// snapshot or left AOI). Game code reacts for despawn VFX, cleanup.
    /// </summary>
    public readonly struct NetworkEntityDespawned
    {
        /// <summary>Server entity id.</summary>
        public readonly string EntityId;
        /// <summary>Server entity type.</summary>
        public readonly string EntityType;

        public NetworkEntityDespawned(string entityId, string entityType)
        {
            EntityId = entityId;
            EntityType = entityType;
        }
    }
}
