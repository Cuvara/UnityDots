using System.Collections.Generic;
using Unity.Collections;
using Cuvara.DOTS.Groups;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Recycles views whose entity was destroyed or whose GameObject was destroyed externally,
    /// then lets the entity finish dying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources of despawn, both handled here:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Entity destroyed</b>: matched by <c>EntityViewLinkCleanup</c> without
    /// <c>EntityViewLink</c> — a shape an entity can only reach by being destroyed.
    /// </description></item>
    /// <item><description>
    /// <b>GameObject destroyed externally</b>: scene unload, manual <c>Object.Destroy()</c>,
    /// or editor reset. The registry's <see cref="EntityViewRegistry.SweepDestroyed"/> detects
    /// null transforms and despawns them. Entities whose view was swept have their
    /// <see cref="EntityViewLink"/> removed so they can re-enter the spawn queue.
    /// </description></item>
    /// </list>
    /// </remarks>
    // First in ViewLifecycleGroup: recycling a dead entity's view before this frame's spawns lets
    // the pool hand the freed instance straight back instead of instantiating another.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewLifecycleGroup))]
    internal partial struct EntityViewDespawnSystem : ISystem
    {
        private EntityQuery _destroyed;
        private EntityQuery _linked;

        public void OnCreate(ref SystemState state)
        {
            _destroyed = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityViewLinkCleanup>()
                .WithNone<EntityViewLink>()
                .Build(ref state);

            _linked = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityViewLink>()
                .Build(ref state);

            state.RequireForUpdate<EntityViewRegistryReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var registry = SystemAPI.ManagedAPI.GetSingleton<EntityViewRegistryReference>().Registry;
            if (registry == null) return;

            // Phase 1: sweep externally-destroyed GameObjects and unlink their entities
            // so they can respawn. This runs before the entity-destroyed query because a
            // swept entity may also be dying, and the cleanup below must not double-despawn.
            int swept = registry.SweepDestroyed();
            if (swept > 0)
            {
                // Find entities whose EntityViewLink points at a view the sweep just removed.
                // Those links are now stale — remove them so the entity is either garbage-
                // collected (if destroyed) or re-enters the spawn queue (if alive).
                var linkedEntities = _linked.ToEntityArray(Allocator.Temp);
                var linkedLinks = _linked.ToComponentDataArray<EntityViewLink>(Allocator.Temp);
                for (int i = 0; i < linkedEntities.Length; i++)
                {
                    if (registry.Get(linkedLinks[i].ViewId) == null)
                    {
                        state.EntityManager.RemoveComponent<EntityViewLink>(linkedEntities[i]);
                        state.EntityManager.RemoveComponent<ViewTransformOffset>(linkedEntities[i]);
                    }
                }
                linkedEntities.Dispose();
                linkedLinks.Dispose();
            }

            // Phase 2: standard entity-destroyed despawn
            if (_destroyed.IsEmpty) return;

            var entities = _destroyed.ToEntityArray(Allocator.Temp);
            var cleanups = _destroyed.ToComponentDataArray<EntityViewLinkCleanup>(Allocator.Temp);

            for (var i = 0; i < cleanups.Length; i++) registry.Despawn(cleanups[i].ViewId);

            state.EntityManager.RemoveComponent<EntityViewLinkCleanup>(entities);

            cleanups.Dispose();
            entities.Dispose();
        }
    }
}
