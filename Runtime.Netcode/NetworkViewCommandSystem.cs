using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.Interpolation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
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
    /// If the drain ever becomes a cost, the fix is fewer commands, not a partial drain. Whether it
    /// is a cost is measured, not assumed: every drain records its command count, wall time and the
    /// age of the oldest command into <see cref="NetworkIngestionMetrics"/>, under the
    /// <c>Cuvara.DOTS.NetworkViewCommandSystem.Drain</c> profiler marker.
    /// </para>
    /// <para>
    /// <b>Generations.</b> Every command carries the <see cref="DotsEntityView.Generation"/> it was
    /// enqueued under. A command older than the view's current generation is dropped unapplied; a
    /// <see cref="NetworkViewCommandKind.Reset"/> tears down every mirror of the previous generation
    /// (reason <see cref="NetworkDespawnReason.SessionReset"/>) before the first command of the new
    /// one is applied. Thread rule: this system is the only consumer of the queue, and it runs on
    /// the world's main thread; the view's producer thread is latched on its side.
    /// </para>
    /// <para>
    /// <b>The id → <see cref="Entity"/> map lives here, not on the view.</b> The view runs on the
    /// caller's thread and must not name an <see cref="Entity"/> it cannot legally create; the map
    /// is only meaningful next to the <c>EntityManager</c> that produced its values.
    /// <see cref="NetworkEntity"/> carries the id back the other way for anything that needs it.
    /// </para>
    /// <para>
    /// <b>It applies a position or it buffers one, never both.</b> A state carrying a server tick
    /// is appended to the entity's <see cref="SnapshotSample"/> buffer and the transform is left to
    /// <see cref="RemoteInterpolationSystem"/>; a state without one is written straight to the
    /// transform as it always was. Which of the two a consumer gets is decided by which method it
    /// called on <see cref="DotsEntityView"/>, and the exclusivity is enforced here rather than
    /// documented, because two writers to <c>LocalTransform</c> is the failure shape this file
    /// already carries two paragraphs about. <see cref="ReconciliationAnchor"/> is written on both
    /// paths, verbatim and unchanged — it is the prediction contract and it is not part of this
    /// decision.
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
    /// <para>
    /// <b>It is the publisher of <see cref="NetworkEntitySpawned"/> and
    /// <see cref="NetworkEntityDespawned"/>, and the only one.</b> The id → entity map here is the
    /// single source of truth for "present in this world", so this is the one place that can promise
    /// exactly one spawn and one despawn per life of an id: the view's own <c>_live</c> set filters
    /// duplicates a frame earlier and on another thread, but it counts enqueues, not applied
    /// entities, and it cannot see an entity a consumer destroyed. Events go to
    /// <see cref="DotsEntityView.Lifecycle"/> synchronously, after the structural change for a spawn
    /// and before it for a despawn. See <see cref="NetworkEntityLifecycle"/> for the contract.
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
        /// <summary>
        /// What the map remembers per present id. Type and locality are copied here at spawn so a
        /// despawn event can be built after the entity is gone — an externally destroyed mirror has
        /// no <see cref="NetworkEntity"/> left to read them from.
        /// </summary>
        internal struct Mirror
        {
            public Entity Entity;
            public FixedString32Bytes Type;
            public bool IsLocal;
        }

        private static readonly ProfilerMarker DrainMarker = new ProfilerMarker("Cuvara.DOTS.NetworkViewCommandSystem.Drain");

        private NativeHashMap<FixedString64Bytes, Mirror> _entities;

        /// <summary>Generation the mirrors in <see cref="_entities"/> belong to; 0 until the first command.</summary>
        private int _generation;

        public void OnCreate(ref SystemState state)
        {
            _entities = new NativeHashMap<FixedString64Bytes, Mirror>(64, Allocator.Persistent);
            state.RequireForUpdate<NetworkEntityViewReference>();
        }

        /// <summary>Ids the drain currently holds a mirror for. Tests and teardown.</summary>
        internal int PresentCount => _entities.IsCreated ? _entities.Count : 0;

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
            var lifecycle = view.Lifecycle;
            var metrics = view.Metrics;

            // Read once per drain. A BeginGeneration that lands mid-drain stamps its commands with
            // the newer number; they are applied after this drain's reset check, which is correct
            // because they were enqueued after everything already dequeued.
            var currentGeneration = view.Generation;

            // The render clock is read once, mutated in place across the whole drain and written
            // back once. Reading and writing the singleton per command would be a chunk lookup per
            // state at snapshot rate for a value that is one struct; the arrivals are ordered, so
            // the accumulated result is identical.
            var timed = SystemAPI.HasSingleton<InterpolationSettings>()
                        && SystemAPI.HasSingleton<InterpolationTimeline>();

            InterpolationSettings settings = default;
            InterpolationTimeline timeline = default;
            if (timed)
            {
                settings = SystemAPI.GetSingleton<InterpolationSettings>();
                timeline = SystemAPI.GetSingleton<InterpolationTimeline>();
            }

            using (DrainMarker.Auto())
            {
                var started = NetworkIngestionMetrics.Now;
                var count = 0;
                var oldestAge = 0.0;

                while (view.TryDequeue(out var command))
                {
                    // The first command out is the oldest in; its wait is the queue's worst latency.
                    if (count == 0) oldestAge = started - command.EnqueueTime;
                    count++;

                    // Older than the view's current generation: the session it belonged to has been
                    // reset. Applying it would resurrect an entity of that session for a frame.
                    if (command.Generation < currentGeneration)
                    {
                        metrics.NoteStaleDropped();
                        continue;
                    }

                    switch (command.Kind)
                    {
                        case NetworkViewCommandKind.Reset:
                            // Every mirror still present belongs to the previous generation.
                            Teardown(entityManager, lifecycle, NetworkDespawnReason.SessionReset);
                            _generation = command.Generation;
                            metrics.NoteGenerationReset();
                            break;

                        case NetworkViewCommandKind.Spawn:
                            _generation = command.Generation;
                            ApplySpawn(entityManager, mapping, lifecycle, command);
                            break;

                        case NetworkViewCommandKind.State:
                            ApplyState(entityManager, mapping, writeHealth, lifecycle, metrics, command,
                                       settings.Config, ref timeline.Clock, timed);
                            break;

                        case NetworkViewCommandKind.Despawn:
                            ApplyDespawn(entityManager, lifecycle, command);
                            break;
                    }
                }

                if (count > 0) metrics.NoteDrain(count, NetworkIngestionMetrics.Now - started, oldestAge);
            }

            if (timed) SystemAPI.SetSingleton(timeline);
        }

        private void ApplySpawn(
            EntityManager entityManager,
            in SnapshotSpaceMapping mapping,
            NetworkEntityLifecycle lifecycle,
            in NetworkViewCommand command)
        {
            if (_entities.TryGetValue(command.Id, out var existing))
            {
                // A spawn for an id whose mirror is alive is dropped rather than replacing the
                // entity: the view filters duplicates too, so reaching here means the two disagree,
                // and destroying a live entity to build an identical one loses whatever a consumer
                // attached to it. No event either — the id never stopped being present.
                if (IsLiveMirror(entityManager, existing.Entity)) return;

                // The mirror was destroyed behind the adapter's back and the wire is now spawning
                // the id again (the view forgot it across an AOI exit/re-entry, or a session reset).
                // Close the first life before opening the second, so the events pair up.
                PublishDespawned(lifecycle, command.Id, existing, NetworkDespawnReason.ExternalDestruction);
                _entities.Remove(command.Id);
            }

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

            // Added at spawn for the same reason ReconciliationAnchor is: the component set must be
            // stable from frame one. An entity whose sample buffer appeared on its first timed
            // state would change archetype at snapshot rate — a structural change per entity per
            // area-of-interest entry — and every query over mirrors would iterate two chunk sets to
            // save 192 bytes on entities that are about to need them anyway. Empty is a legal and
            // meaningful state: RemoteInterpolationJob passes over an entity with nothing to
            // evaluate, which is exactly what an untimed consumer wants for every entity.
            entityManager.AddBuffer<SnapshotSample>(entity);

            entityManager.AddComponentData(entity, new InterpolationState
            {
                // Not rendered yet, and Position carries the spawn placement rather than zero so a
                // reader sees where the entity actually is. HasRendered is what distinguishes the
                // two, because the origin is a legal place to be.
                HasRendered = false,
                RenderTick = 0.0,
                Position = position,
            });

            // Both, not either: EntityViewSpawnSystem matches on EntityViewRequest and prefers the
            // config's key when a ViewConfigRef is present. Writing the resolved key into the
            // request as well means a catalog rebuild that invalidates the index degrades to the
            // right prefab instead of to nothing.
            entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = command.ViewKey });
            if (command.ConfigIndex >= 0)
            {
                entityManager.AddComponentData(entity, new ViewConfigRef { Index = command.ConfigIndex, Version = command.ConfigVersion });
            }

            // On the map only if the consumer's resolver said so, and only because this is a mirror:
            // the server listed the id, so showing it reveals nothing the area of interest withheld.
            if (command.MinimapCategory >= 0)
            {
                entityManager.AddComponentData(entity, new MinimapMarker
                {
                    Category = command.MinimapCategory,
                    IsLocal = command.IsLocal,
                });
            }

#if UNITY_EDITOR
            entityManager.SetName(entity, (command.IsLocal ? "net:local:" : "net:") + command.Id);
#endif

            var mirror = new Mirror { Entity = entity, Type = command.Type, IsLocal = command.IsLocal };
            _entities.Add(command.Id, mirror);

            // After every component is on the entity and after the map records it, so a handler that
            // queries NetworkEntity or reads the anchor sees a complete mirror, and a handler that
            // despawns it from inside the callback is refused nothing.
            if (lifecycle.HasObservers)
            {
                lifecycle.PublishSpawned(new NetworkEntitySpawned(
                    command.Id.ToString(), command.Type.ToString(), command.IsLocal, entity));
            }
        }

        private void ApplyState(
            EntityManager entityManager,
            in SnapshotSpaceMapping mapping,
            bool writeHealth,
            NetworkEntityLifecycle lifecycle,
            NetworkIngestionMetrics metrics,
            in NetworkViewCommand command,
            in InterpolationConfig interpolation,
            ref InterpolationClock clock,
            bool interpolationInstalled)
        {
            if (!_entities.TryGetValue(command.Id, out var mirror)) return;

            var entity = mirror.Entity;
            if (!IsLiveMirror(entityManager, entity))
            {
                // Destroyed by something other than a despawn command — a consumer's own system, or
                // the death system when writeHealth is on. Drop the stale mapping so a later spawn
                // of the same id is not refused by ApplySpawn's duplicate check, and report the end
                // of this life now: the wire will keep sending state for the id, and a later wire
                // despawn finds nothing in the map and stays silent, so this is the one report.
                _entities.Remove(command.Id);
                PublishDespawned(lifecycle, command.Id, mirror, NetworkDespawnReason.ExternalDestruction);
                return;
            }

            var position = mapping.ToWorld(command.X, command.Y);

            // Always. This is what the server said, and it is the value a predictor rewinds to —
            // separate from what the client is currently showing, exactly as NetworkEntityState is
            // separate from Health. Nothing rendered or predicted is ever written here: the only
            // source is the command, and the command's only source is the wire.
            var previousAnchor = entityManager.GetComponentData<ReconciliationAnchor>(entity);
            entityManager.SetComponentData(entity, new ReconciliationAnchor
            {
                Position = position,
                // Verbatim from the command, which took it verbatim from SetState. Deliberately not
                // derived from `position` above — a round trip through the mapping is not bit-exact,
                // and a predictor replaying from an off-by-one-ULP anchor drifts.
                ServerPosition = new float2(command.X, command.Y),
                Sequence = previousAnchor.Sequence + 1u,
                Tick = command.Tick,
            });

            // A state that carries a tick is a sample, not a placement. It is buffered and the
            // transform is left to RemoteInterpolationSystem, which renders it against the world's
            // render clock at frame rate rather than at snapshot rate.
            //
            // This is where the two paths are kept mutually exclusive, and it is enforced here
            // rather than documented and hoped for: writing the transform as well would fight the
            // interpolation job for the same component every time a snapshot landed, which is the
            // one-writer rule broken by the very system that exists to honour it. A sample the ring
            // refuses — a duplicate or a reordered tick — changes nothing and must NOT fall back to
            // a direct write, because the entity is still owned by interpolation; its superseded
            // state is simply not worth rendering.
            var buffered = interpolationInstalled
                           && command.Tick > 0L
                           && entityManager.HasBuffer<SnapshotSample>(entity);

            if (buffered)
            {
                if (!TryAppendSample(entityManager, entity, command, interpolation))
                {
                    metrics.NoteRejectedSample();
                }
                else
                {
                    // The clock is told about the arrival regardless of which entity carried it:
                    // there is one render timeline per world, and every entity's ticks come off the
                    // same server clock. A tick gap of zero is passed because IEntityView-shaped
                    // adapters have no TickRateEstimator to consult — it only seeds the very first
                    // seconds-per-tick estimate, and the first real measurement replaces the seed
                    // outright rather than being smoothed into it.
                    clock.NoteSnapshot(command.Tick, command.ReceiveTime, 0, interpolation);
                }
            }

            // An untimed state for an entity that already holds samples: interpolation owns this
            // transform. Writing it here would be the second writer the paragraph above forbids,
            // just arriving through the other method — a consumer feeding one id through both
            // SetState and SetStateAtTick. Counted, anchor and hp still written, transform left alone.
            else if (interpolationInstalled
                     && entityManager.HasBuffer<SnapshotSample>(entity)
                     && entityManager.GetBuffer<SnapshotSample>(entity).Length > 0)
            {
                metrics.NoteMixedPath();
            }

            // The transform is written only while nothing else claims it. With a predictor owning
            // LocalTransform, both writing it would work on every frame the predictor runs and snap
            // the entity back to the server position on the frames it does not — intermittent, felt
            // rather than seen, and blamed on the predictor. One writer per component instead.
            else if (!entityManager.HasComponent<PredictedTransform>(entity))
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

        /// <summary>
        /// Appends one received state to an entity's sample buffer, evicting the oldest when full.
        /// False when the ring refuses the tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The admission rule is netcode's <c>InterpolationRing.Accepts</c>, not a local
        /// comparison</b>, and that matters more than its two lines suggest: the evaluator's
        /// bracketing assumes strictly increasing ticks, and a duplicate or reordered sample slipped
        /// into the buffer makes it pick a pair that spans no time and render the wrong endpoint,
        /// silently. The GameObject path's pooled ring asks the same function the same question, so
        /// the two storages cannot disagree about which samples are kept.
        /// </para>
        /// <para>
        /// <b>A shift rather than a start index, and this is the one place the ECS storage differs
        /// from the pooled one.</b> A <c>DynamicBuffer</c> is a list, and <c>ISampleBuffer</c>
        /// requires index 0 to be the oldest sample; keeping a per-entity start index would mean a
        /// second component, an indexer that consults it, and index arithmetic in the hot job.
        /// Removing the front element instead is a memmove of at most seven 24-byte samples — 168
        /// bytes, inside the chunk, on snapshot arrivals only and never on the per-frame path.
        /// <c>InterpolationRing.Claim</c> is therefore deliberately unused here while
        /// <c>Accepts</c> is not: the admission rule is a correctness invariant shared between the
        /// paths, the slot arithmetic is a property of one path's storage.
        /// </para>
        /// </remarks>
        private static bool TryAppendSample(
            EntityManager entityManager,
            Entity entity,
            in NetworkViewCommand command,
            in InterpolationConfig interpolation)
        {
            var samples = entityManager.GetBuffer<SnapshotSample>(entity);
            var newestTick = samples.Length > 0 ? samples[samples.Length - 1].Value.Tick : 0L;

            if (!InterpolationRing.Accepts(samples.Length, newestTick, command.Tick)) return false;

            // Clamped the way netcode clamps it, so a config that was never normalized cannot make
            // the buffer hold nothing or hold one sample and never interpolate.
            var capacity = interpolation.RingCapacity < 2 ? 2 : interpolation.RingCapacity;
            while (samples.Length >= capacity) samples.RemoveAt(0);

            samples.Add(new SnapshotSample
            {
                Value = new InterpolationSample
                {
                    Tick = command.Tick,
                    ReceiveTime = command.ReceiveTime,
                    // Server space, verbatim, exactly as ReconciliationAnchor.ServerPosition takes
                    // it: SnapshotSpaceMapping is applied once, after evaluation, in the job.
                    X = command.X,
                    Y = command.Y,
                },
            });

            return true;
        }

        private void ApplyDespawn(EntityManager entityManager, NetworkEntityLifecycle lifecycle, in NetworkViewCommand command)
        {
            if (!_entities.TryGetValue(command.Id, out var mirror)) return;

            _entities.Remove(command.Id);

            // Before the destroy, so a handler can read the entity's last state for a despawn effect.
            // The reason is Despawned even if the entity turns out to be gone already: the wire said
            // it left, and that is the fact being reported.
            PublishDespawned(lifecycle, command.Id, mirror, NetworkDespawnReason.Despawned);

            // Destroying the entity is the whole despawn: EntityViewLinkCleanup survives the
            // destruction and EntityViewDespawnSystem recycles the view from it next presentation.
            // Reaching into the registry from here would double-free it.
            if (entityManager.Exists(mirror.Entity)) entityManager.DestroyEntity(mirror.Entity);
        }

        /// <summary>
        /// Ends every present life at once: one <see cref="NetworkEntityDespawned"/> per mapped id,
        /// then the mirror entities are destroyed and the map is emptied. Called by
        /// <c>DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reason is <see cref="NetworkDespawnReason.Teardown"/> for a mirror that still exists
        /// and <see cref="NetworkDespawnReason.ExternalDestruction"/> for one that does not — the
        /// latter is a life that ended earlier and was never reported because no command for the id
        /// arrived in between. Either way each id is reported once, and a <c>Despawn</c> command still
        /// sitting in the view's queue afterwards finds an empty map and publishes nothing.
        /// </para>
        /// <para>
        /// Order is the map's iteration order, which is not the spawn order. A consumer that needs
        /// ordered teardown drains <c>WorldViewBinder.Reset</c> through a tick first, which reaches
        /// <see cref="ApplyDespawn"/> in the binder's order.
        /// </para>
        /// </remarks>
        internal void Teardown(
            EntityManager entityManager,
            NetworkEntityLifecycle lifecycle,
            NetworkDespawnReason reason = NetworkDespawnReason.Teardown)
        {
            if (!_entities.IsCreated || _entities.Count == 0) return;

            // Snapshot the map before publishing: a handler is allowed to call anything on the
            // EntityManager, and the map must already be in its final state if one of them asks
            // the drain a question through the view.
            var ids = _entities.GetKeyArray(Allocator.Temp);
            var mirrors = _entities.GetValueArray(Allocator.Temp);
            _entities.Clear();

            for (var i = 0; i < ids.Length; i++)
            {
                var alive = IsLiveMirror(entityManager, mirrors[i].Entity);
                PublishDespawned(lifecycle, ids[i], mirrors[i],
                    alive ? reason : NetworkDespawnReason.ExternalDestruction);
                // Exists rather than alive: a mirror already stripped to its cleanup components is
                // destroyed again harmlessly, and the view path finishes it next presentation.
                if (entityManager.Exists(mirrors[i].Entity)) entityManager.DestroyEntity(mirrors[i].Entity);
            }

            ids.Dispose();
            mirrors.Dispose();
        }

        /// <summary>
        /// Whether a mapped entity is still a mirror, as opposed to destroyed or in cleanup limbo.
        /// </summary>
        /// <remarks>
        /// <b><c>Exists</c> alone is the wrong test, and the reason is a cleanup component.</b> Every
        /// mirror with a view carries <c>EntityViewLinkCleanup</c>, so <c>DestroyEntity</c> on it does
        /// not remove the entity — Entities strips every non-cleanup component and keeps the shell
        /// alive until <c>EntityViewDespawnSystem</c> removes the cleanup in presentation. The drain
        /// runs in initialization, before that, so on the frame after an external destroy the
        /// entity still <c>Exists</c> and has no <see cref="NetworkEntity"/>. That shell is not a
        /// mirror: a state written to it would be lost with the entity, and a spawn refused because
        /// of it would leave the id invisible. Asking for the component the drain itself added is
        /// the honest question.
        /// </remarks>
        private static bool IsLiveMirror(EntityManager entityManager, Entity entity) =>
            entityManager.Exists(entity) && entityManager.HasComponent<NetworkEntity>(entity);

        private static void PublishDespawned(
            NetworkEntityLifecycle lifecycle,
            in FixedString64Bytes id,
            in Mirror mirror,
            NetworkDespawnReason reason)
        {
            // Gated on observers, as the spawn publish is: the event carries managed strings, and an
            // unobserved session should not allocate two of them per AOI transition. The counters on
            // the hub therefore describe delivered events, not lives.
            if (!lifecycle.HasObservers) return;

            lifecycle.PublishDespawned(new NetworkEntityDespawned(
                id.ToString(), mirror.Type.ToString(), mirror.IsLocal, mirror.Entity, reason));
        }
    }
}
