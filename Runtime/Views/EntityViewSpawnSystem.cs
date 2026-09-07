using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Unity.Transforms;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Turns <see cref="EntityViewRequest"/> into a live view plus an <see cref="EntityViewLink"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not Bursted, and not schedulable.</b> Spawning goes through the managed pool, so
    /// <see cref="OnUpdate"/> runs on the main thread by definition. There is nothing to gain by
    /// pretending otherwise, and no measurement here claims a win.
    /// </para>
    /// <para>
    /// A request whose key is not warm is <b>left in place and retried next frame</b> rather than
    /// force-loading synchronously. The pool would happily load on demand and hitch; deferring
    /// keeps the hitch in <see cref="Provisioning.ChunkViewProvisioner"/>'s prewarm, where it is
    /// asynchronous and expected. The visible cost is that an entity spawned before its chunk
    /// finished warming stays invisible for a few frames.
    /// </para>
    /// </remarks>
    // After despawn — see EntityViewDespawnSystem for why that order. Expressed as an explicit
    // UpdateAfter rather than OrderFirst/OrderLast: Entities batches those separately and then drops
    // ordering relations between the batch and normal members, so this is the relation that holds.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewLifecycleGroup))]
    [UpdateAfter(typeof(EntityViewDespawnSystem))]
    internal partial struct EntityViewSpawnSystem : ISystem
    {
        private EntityQuery _pending;
        private EntityQuery _configTable;

        public void OnCreate(ref SystemState state)
        {
            _pending = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityViewRequest>()
                .WithNone<EntityViewLink>()
                .Build(ref state);

            // Not RequireForUpdate: a config table is optional, and requiring it would stop the
            // bare-key path from working in a project that has none.
            _configTable = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ViewConfigTableReference>()
                .Build(ref state);

            state.RequireForUpdate(_pending);
            state.RequireForUpdate<EntityViewRegistryReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var registry = SystemAPI.ManagedAPI.GetSingleton<EntityViewRegistryReference>().Registry;
            if (registry == null) return;

            var entities = _pending.ToEntityArray(Allocator.Temp);
            var entityManager = state.EntityManager;

            // Fetched once per update rather than per entity, and through a plain query rather than
            // SystemAPI so the resolve helper can stay a static method.
            var tableRef = _configTable.IsEmpty
                ? default
                : _configTable.GetSingleton<ViewConfigTableReference>();

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];

                // Two routes to a key, and the bare-key one is unchanged: an entity with only
                // EntityViewRequest behaves exactly as it did before configs existed. A ViewConfigRef
                // overrides it, because an entity carrying one asked for a configured view.
                var hasConfig = TryResolveConfig(entityManager, entity, tableRef, out var record);
                var key = hasConfig
                    ? record.ViewKey.ToString()
                    : entityManager.GetComponentData<EntityViewRequest>(entity).ViewKey.ToString();
                if (!registry.IsWarm(key))
                {
                    // Retried next frame — but counted, so a key that will never arrive (a typo, a
                    // prefab missing from the chunk manifest) eventually says so instead of
                    // deferring in silence forever. One warning per key, not one per frame.
                    registry.NoteDeferredSpawn(key);
                    continue;
                }

                // LocalToWorld, not LocalTransform: LocalTransform is relative to the parent, so a
                // parented entity would place its view at local coordinates. LocalToWorld is what
                // TransformSystemGroup just computed and is correct either way.
                var hasTransform = entityManager.HasComponent<LocalToWorld>(entity);
                var localToWorld = hasTransform ? entityManager.GetComponentData<LocalToWorld>(entity) : default;
                var position = hasTransform ? localToWorld.Position : float3.zero;

                // Spawn already rotated: spawning at identity and letting the first sync correct it
                // shows one frame of wrong facing on everything that spawns already oriented.
                var rotation = hasTransform ? localToWorld.Rotation : quaternion.identity;

                var offset = hasConfig
                    ? new ViewTransformOffset
                    {
                        Position = record.PositionOffset,
                        Rotation = record.RotationOffset,
                        Scale = record.Scale,
                    }
                    : ViewTransformOffset.Identity;

                // Spawn already offset, for the same reason it spawns already rotated: letting the
                // first sync correct it shows one frame in the wrong place.
                var spawnRotation = math.mul(rotation, offset.Rotation);
                var spawnPosition = position + math.mul(rotation, offset.Position);

                var viewId = registry.Spawn(key, spawnPosition, spawnRotation);
                if (viewId == 0) continue;

                entityManager.AddComponentData(entity, new EntityViewLink { ViewId = viewId });
                entityManager.AddComponentData(entity, new EntityViewLinkCleanup { ViewId = viewId });

                // Added to every linked entity, identity when unconfigured, so the sync keeps one
                // query instead of one per has-offset combination.
                entityManager.AddComponentData(entity, offset);

                if (hasConfig)
                {
                    entityManager.AddComponentData(entity, new ViewSortingKey
                    {
                        LayerId = record.SortingLayerId,
                        Order = record.SortingOrder,
                    });
                }

                entityManager.RemoveComponent<EntityViewRequest>(entity);
            }

            entities.Dispose();
        }

        /// <summary>
        /// Resolves an entity's <see cref="ViewConfigRef"/> against the session table.
        /// </summary>
        /// <remarks>
        /// Returns false — falling back to the bare key — when there is no config, no table, or the
        /// index is out of range. An out-of-range index warns, because it means the catalog was
        /// rebuilt without the entities that referenced it being updated, and silently rendering the
        /// wrong archetype is worse than rendering the request's own key.
        /// </remarks>
        private static bool TryResolveConfig(EntityManager entityManager, Entity entity, ViewConfigTableReference tableRef, out ViewConfigRecord record)
        {
            record = default;
            if (!entityManager.HasComponent<ViewConfigRef>(entity)) return false;
            if (!tableRef.Table.IsCreated) return false;

            var configRef = entityManager.GetComponentData<ViewConfigRef>(entity);
            var index = configRef.Index;
            ref var table = ref tableRef.Table.Value;

            // Version first, range second: a stale ref is a stale ref whether or not its index still
            // happens to be in range, and the in-range case is the one that used to spawn the wrong
            // archetype silently.
            if (configRef.Version != table.Version)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Cuvara.DOTS] ViewConfigRef (index {index}, version {configRef.Version}) does not match the installed " +
                    $"config table (version {table.Version}); falling back to the request's own view key. " +
                    (configRef.Version == 0
                        ? "A ref built with `new` is unstamped — issue refs with ViewConfigCatalog.CreateRef."
                        : "The catalog was rebuilt; re-resolve refs with ViewConfigCatalog.CreateRef after a rebuild."));
                return false;
            }

            if (index < 0 || index >= table.Records.Length)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Cuvara.DOTS] ViewConfigRef index {index} is outside the {table.Records.Length}-entry " +
                    "config table; falling back to the request's own view key.");
                return false;
            }

            record = table.Records[index];
            return true;
        }
    }
}
