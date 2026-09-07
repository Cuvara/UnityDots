using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Why a replicated entity's mirror stopped being present in this world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>None of these means "the entity died".</b> The wire does not distinguish an
    /// area-of-interest exit from a server-side removal — <c>WorldViewBinder</c> derives despawn
    /// from absence in the merged world, and this package receives one <c>IEntityView.Despawn</c>
    /// either way. A consumer that needs death semantics reads hp from
    /// <see cref="NetworkEntityState"/> before the despawn, or waits for a gameplay event the
    /// server sends explicitly; it must not infer death from <see cref="NetworkEntityDespawned"/>.
    /// </para>
    /// </remarks>
    public enum NetworkDespawnReason : byte
    {
        /// <summary>
        /// The wire despawned it: <c>IEntityView.Despawn</c> reached the drain, either because the
        /// merged world stopped listing the id (AOI exit or removal) or because
        /// <c>WorldViewBinder.Reset</c> cleared the session.
        /// </summary>
        Despawned = 0,

        /// <summary>
        /// Something other than the adapter destroyed the mirror entity — a consumer's own system,
        /// <c>HealthDeathSystem</c> with <c>writeHealth</c> on, or a manual <c>DestroyEntity</c>.
        /// Detected on the next command that targets the id, so the notification is late by up to
        /// one snapshot interval, and <see cref="NetworkEntityDespawned.Entity"/> no longer exists
        /// when it is published.
        /// </summary>
        ExternalDestruction = 1,

        /// <summary>
        /// <c>DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true)</c> tore the session down
        /// while the entity was still present. Every id present at that moment gets exactly one of
        /// these, in no particular order.
        /// </summary>
        Teardown = 2,
    }

    /// <summary>
    /// A replicated id became present in this world: the drain created its mirror entity and every
    /// component the adapter owns is on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is network presence, not visual presence.</b> The view for this entity may not exist
    /// yet — its key may still be warming — and may never exist if nothing configured a view for its
    /// archetype. <c>Cuvara.DOTS.Messaging.ViewSpawned</c> is the visual event and it is a different
    /// lifecycle: a view can be culled, recycled by a chunk release and re-acquired while the network
    /// entity stays present throughout.
    /// </para>
    /// <para>
    /// Published from <c>NetworkViewCommandSystem</c> on the drain's thread, synchronously, after the
    /// entity is created and before the next command is applied. Exactly once per
    /// spawn → despawn life of an id: a repeated <c>Spawn</c> for an id that is already present —
    /// which is what a repeated keyframe produces if a caller bypasses <c>WorldViewBinder</c> —
    /// publishes nothing. An id that leaves and re-enters the area of interest is a new life and gets
    /// a new event with a new <see cref="Entity"/>.
    /// </para>
    /// <para>
    /// <see cref="Entity"/> carries index <i>and</i> version, so a handler that caches it and later
    /// receives a <see cref="NetworkEntityDespawned"/> can match the two lives of one id apart even
    /// though the index may be reused.
    /// </para>
    /// </remarks>
    public readonly struct NetworkEntitySpawned
    {
        /// <summary>The replicated id, exactly as it arrived on the wire. For players, the Nakama user id.</summary>
        public readonly string EntityId;

        /// <summary>
        /// The server's entity kind (<c>"player"</c>, <c>"mob"</c>, …). Read back from
        /// <see cref="NetworkEntity.Type"/>, so a kind longer than 29 bytes arrives clipped here
        /// exactly as it is clipped there. Empty when the server sent none.
        /// </summary>
        public readonly string EntityType;

        /// <summary>Whether this is the local player's entity.</summary>
        public readonly bool IsLocal;

        /// <summary>
        /// The mirror entity, including its version. Exists for the duration of the handler and
        /// until the matching <see cref="NetworkEntityDespawned"/>.
        /// </summary>
        public readonly Entity Entity;

        public NetworkEntitySpawned(string entityId, string entityType, bool isLocal, Entity entity)
        {
            EntityId = entityId ?? string.Empty;
            EntityType = entityType ?? string.Empty;
            IsLocal = isLocal;
            Entity = entity;
        }

        /// <summary>Kept for source compatibility with 0.27.x; <see cref="Entity"/> is <c>Entity.Null</c>.</summary>
        public NetworkEntitySpawned(string entityId, string entityType, bool isLocal)
            : this(entityId, entityType, isLocal, Entity.Null)
        {
        }
    }

    /// <summary>
    /// A replicated id stopped being present in this world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exactly one per <see cref="NetworkEntitySpawned"/>, never more.</b> The drain's id → entity
    /// map is the source of truth: an id that is not in it publishes nothing, which is what makes a
    /// wire despawn arriving after an <see cref="NetworkDespawnReason.ExternalDestruction"/> report,
    /// or a queued <c>Despawn</c> drained after a teardown, silent rather than a second event.
    /// </para>
    /// <para>
    /// <b>For <see cref="NetworkDespawnReason.Despawned"/> and <see cref="NetworkDespawnReason.Teardown"/>
    /// the entity still exists while handlers run</b> and is destroyed immediately after the last
    /// handler returns — so a handler may read its last <c>LocalTransform</c> for a despawn effect.
    /// For <see cref="NetworkDespawnReason.ExternalDestruction"/> it does not exist; check
    /// <c>EntityManager.Exists</c> before touching components if a handler serves all three. Do not
    /// keep <see cref="Entity"/> past the handler for anything but identity comparison.
    /// </para>
    /// <para>
    /// <see cref="Reason"/> never says whether the entity died. See <see cref="NetworkDespawnReason"/>.
    /// </para>
    /// </remarks>
    public readonly struct NetworkEntityDespawned
    {
        /// <summary>The replicated id, exactly as it arrived on the wire.</summary>
        public readonly string EntityId;

        /// <summary>The server's entity kind, as recorded at spawn. See <see cref="NetworkEntitySpawned.EntityType"/>.</summary>
        public readonly string EntityType;

        /// <summary>Whether this was the local player's entity.</summary>
        public readonly bool IsLocal;

        /// <summary>
        /// The mirror entity this id had, including its version — the same value the matching
        /// <see cref="NetworkEntitySpawned"/> carried.
        /// </summary>
        public readonly Entity Entity;

        /// <summary>Why it left. Never "it died".</summary>
        public readonly NetworkDespawnReason Reason;

        public NetworkEntityDespawned(string entityId, string entityType, bool isLocal, Entity entity, NetworkDespawnReason reason)
        {
            EntityId = entityId ?? string.Empty;
            EntityType = entityType ?? string.Empty;
            IsLocal = isLocal;
            Entity = entity;
            Reason = reason;
        }

        /// <summary>Kept for source compatibility with 0.27.x; <see cref="Entity"/> is <c>Entity.Null</c>.</summary>
        public NetworkEntityDespawned(string entityId, string entityType)
            : this(entityId, entityType, false, Entity.Null, NetworkDespawnReason.Despawned)
        {
        }
    }
}
