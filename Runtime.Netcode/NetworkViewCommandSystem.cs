using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Drains <see cref="DotsEntityView"/>'s queue: creates, updates and destroys the entities that
    /// mirror replicated ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole queue, every update.</b> A cap would be a frame-rate-dependent way of losing
    /// state: the commands are not independent — a spawn its state never reached is an entity at the
    /// origin — and the backlog after a keyframe is bounded by the AOI, not by anything that grows.
    /// If the drain ever becomes a cost, the fix is fewer commands, not a partial drain.
    /// </para>
    /// <para>
    /// <b>The id → <see cref="Entity"/> map lives here, not on the view.</b> The view runs on the
    /// caller's thread and must not name an <see cref="Entity"/> it cannot legally create; the map
    /// is only meaningful next to the <c>EntityManager</c> that produced its values.
    /// <see cref="NetworkEntity"/> carries the id back the other way for anything that needs it.
    /// </para>
    /// <para>
    /// <b>Not Bursted.</b> It reads a managed queue through a managed singleton. Marking it
    /// <c>[BurstCompile]</c> would be a claim the code cannot honour.
    /// </para>
    /// <para>
    /// <b>Deliberately not parallelised, and this was examined rather than skipped.</b> The
    /// simulation systems became <c>IJobEntity</c>/<c>ScheduleParallel</c> in 0.17.0; this one did
    /// not, for three reasons that do not go away with effort:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The drain is ordered by definition.</b> The queue's whole guarantee is that spawn precedes
    /// its first state and despawn follows its last. A parallel drain has no order, so the guarantee
    /// would have to be rebuilt with a sequence number — which is the FIFO again, more expensively.
    /// </description></item>
    /// <item><description>
    /// <b>Two commands in one drain can target the same entity</b> — two <c>SetState</c>s for one id
    /// when snapshots outpace frames — and the correct result is "last wins". Writing the same
    /// component from two workers is a race whose outcome is the scheduler's, so the apply half
    /// cannot be split without first de-duplicating by id, and that de-duplication is a serial pass
    /// over the same data the serial apply already walks.
    /// </description></item>
    /// <item><description>
    /// <b>It creates and destroys entities.</b> That is main-thread work or command-buffer work, and
    /// the command-buffer route buys nothing here because the recording is the cheap part.
    /// </description></item>
    /// </list>
    /// <para>
    /// The work is also bounded by the AOI rather than by anything that grows, so this is tens of
    /// commands per frame, not thousands. Parallelising it would be parallelism for its own sake.
    /// </para>
    /// </remarks>
    // In SnapshotApplyGroup, inside NetcodeSystemGroup, inside InitializationSystemGroup — so
    // entities and transforms written here are seen by this frame's TransformSystemGroup and this
    // frame's ViewSystemGroup. See DotsEntityView for why that makes the queue free rather than a
    // frame late. The sub-group exists so prediction can order itself after this without naming it.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SnapshotApplyGroup))]
    internal partial struct NetworkViewCommandSystem : ISystem
    {
        private NativeHashMap<FixedString64Bytes, Entity> _entities;

        public void OnCreate(ref SystemState state)
        {
            _entities = new NativeHashMap<FixedString64Bytes, Entity>(64, Allocator.Persistent);
            state.RequireForUpdate<NetworkEntityViewReference>();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_entities.IsCreated) _entities.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var view = SystemAPI.ManagedAPI.GetSingleton<NetworkEntityViewReference>().View;
            if (view == null) return;

            var entityManager = state.EntityManager;
            var mapping = view.Mapping;
            var writeHealth = view.WritesHealth;

            while (view.TryDequeue(out var command))
            {
                switch (command.Kind)
                {
                    case NetworkViewCommandKind.Spawn:
                        ApplySpawn(entityManager, mapping, writeHealth, command);
                        break;

                    case NetworkViewCommandKind.State:
                        ApplyState(entityManager, mapping, writeHealth, command);
                        break;

                    case NetworkViewCommandKind.Despawn:
                        ApplyDespawn(entityManager, command);
                        break;
                }
            }
        }

        private void ApplySpawn(
            EntityManager entityManager,
            in SnapshotSpaceMapping mapping,
            bool writeHealth,
            in NetworkViewCommand command)
        {
            // A spawn for an id already mapped is dropped rather than replacing the entity: the view
            // filters duplicates too, so reaching here means the two disagree, and destroying a live
            // entity to build an identical one loses whatever a consumer attached to it.
            if (_entities.ContainsKey(command.Id)) return;

            var entity = entityManager.CreateEntity();
            var position = mapping.Origin;

            entityManager.AddComponentData(entity, LocalTransform.FromPosition(position));

            // LocalToWorld is added and seeded rather than left to TransformSystemGroup, for two
            // reasons: EntityViewSpawnSystem reads it and would otherwise place the first view at
            // the origin, and a test world — or any world without the default transform systems —
            // never computes it at all.
            entityManager.AddComponentData(entity, new LocalToWorld
            {
                Value = float4x4.TRS(position, quaternion.identity, new float3(1f, 1f, 1f)),
            });

            entityManager.AddComponentData(entity, new NetworkEntity
            {
                Id = command.Id,
                Type = command.Type,
                IsLocal = command.IsLocal,
            });

            entityManager.AddComponentData(entity, new NetworkEntityState { Hp = 0, MaxHp = 0 });

            // Added at spawn with the same value LocalTransform got, so the component set is stable
            // from the first frame and a predictor attaching later never reads a default.
            entityManager.AddComponentData(entity, new ReconciliationAnchor
            {
                Position = position,
                // float2.zero, not a mapped value: the server has said nothing yet, and
                // mapping.ToWorld(0, 0) is Origin, so the two fields agree at spawn.
                ServerPosition = float2.zero,
            });

            // Both, not either: EntityViewSpawnSystem matches on EntityViewRequest and prefers the
            // config's key when a ViewConfigRef is present. Writing the resolved key into the
            // request as well means a catalog rebuild that invalidates the index degrades to the
            // right prefab instead of to nothing.
            entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = command.ViewKey });
            if (command.ConfigIndex >= 0)
            {
                entityManager.AddComponentData(entity, new ViewConfigRef { Index = command.ConfigIndex });
            }

#if UNITY_EDITOR
            entityManager.SetName(entity, (command.IsLocal ? "net:local:" : "net:") + command.Id);
#endif

            _entities.Add(command.Id, entity);
        }

        private void ApplyState(
            EntityManager entityManager,
            in SnapshotSpaceMapping mapping,
            bool writeHealth,
            in NetworkViewCommand command)
        {
            if (!_entities.TryGetValue(command.Id, out var entity)) return;
            if (!entityManager.Exists(entity))
            {
                // Destroyed by something other than a despawn command — a consumer's own system, or
                // the death system when writeHealth is on. Drop the stale mapping so a later spawn
                // of the same id is not refused by ApplySpawn's duplicate check.
                _entities.Remove(command.Id);
                return;
            }

            var position = mapping.ToWorld(command.X, command.Y);

            // Always. This is what the server said, and it is the value a predictor rewinds to —
            // separate from what the client is currently showing, exactly as NetworkEntityState is
            // separate from Health.
            entityManager.SetComponentData(entity, new ReconciliationAnchor
            {
                Position = position,
                // Verbatim from the command, which took it verbatim from SetState. Deliberately not
                // derived from `position` above — a round trip through the mapping is not bit-exact,
                // and a predictor replaying from an off-by-one-ULP anchor drifts.
                ServerPosition = new float2(command.X, command.Y),
            });

            // The transform is written only while nothing else claims it. With a predictor owning
            // LocalTransform, both writing it would work on every frame the predictor runs and snap
            // the entity back to the server position on the frames it does not — intermittent, felt
            // rather than seen, and blamed on the predictor. One writer per component instead.
            if (!entityManager.HasComponent<PredictedTransform>(entity))
            {
                // Scale and rotation are read back rather than reset: the wire carries neither, so
                // overwriting them would silently undo anything a consumer's system did.
                var transform = entityManager.GetComponentData<LocalTransform>(entity);
                transform.Position = position;
                entityManager.SetComponentData(entity, transform);

                entityManager.SetComponentData(entity, new LocalToWorld
                {
                    Value = float4x4.TRS(position, transform.Rotation, new float3(transform.Scale, transform.Scale, transform.Scale)),
                });
            }

            entityManager.SetComponentData(entity, new NetworkEntityState
            {
                Hp = command.Hp,
                MaxHp = command.MaxHp,
            });

            if (writeHealth)
            {
                // Added on the first state rather than at spawn, and that is not a detail: Health
                // means "destroy at zero", so an entity carrying Health{0,0} between its spawn and
                // its first state would be destroyed by HealthDeathSystem if a simulation tick fell
                // in that gap. Adding it only once a real hp value exists closes the window.
                var health = new Health { Current = command.Hp, Max = command.MaxHp };
                if (entityManager.HasComponent<Health>(entity)) entityManager.SetComponentData(entity, health);
                else entityManager.AddComponentData(entity, health);
            }
        }

        private void ApplyDespawn(EntityManager entityManager, in NetworkViewCommand command)
        {
            if (!_entities.TryGetValue(command.Id, out var entity)) return;

            _entities.Remove(command.Id);

            // Destroying the entity is the whole despawn: EntityViewLinkCleanup survives the
            // destruction and EntityViewDespawnSystem recycles the view from it next presentation.
            // Reaching into the registry from here would double-free it.
            if (entityManager.Exists(entity)) entityManager.DestroyEntity(entity);
        }
    }
}
