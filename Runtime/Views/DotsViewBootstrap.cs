using System;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Installs the package's system tree and its <see cref="EntityViewRegistry"/> into one named
    /// world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plain static helper taking a <see cref="World"/> — no DI types, so the core assembly stays
    /// installable with only the four pinned Unity dependencies. The VContainer extension in
    /// <c>Cuvara.DOTS.DI</c> is a thin wrapper around this.
    /// </para>
    /// <para>
    /// <b>Why the systems are created here rather than by Unity.</b> The default bootstrap creates
    /// every system that is not <see cref="DisableAutoCreationAttribute"/>-marked in <i>every</i>
    /// world. In a multi-world setup — a thin client world beside a server world, or a test world
    /// beside the default one — that would give two view groups driving the same
    /// <see cref="EntityViewRegistry"/>, and every entity would get two GameObjects. Marking the
    /// package's systems and groups <c>[DisableAutoCreation]</c> and creating them explicitly here
    /// makes "which world presents" an argument rather than an accident. The
    /// <c>RequireForUpdate&lt;EntityViewRegistryReference&gt;()</c> in each system is the second
    /// layer: a group that somehow does get created in a registry-less world does nothing.
    /// </para>
    /// <para>
    /// <b>Lifecycle contract</b> (see <c>Documentation~/MODULE-LIFECYCLE.md</c>). <see cref="Install"/>
    /// twice is one installation. <see cref="Uninstall"/> is a <i>temporary disable</i>: every live
    /// view is recycled to the pool, every linked entity is handed its <see cref="EntityViewRequest"/>
    /// back so it respawns under the next registry, and the singleton goes — but the systems stay
    /// created and idle. Installing a <i>different</i> registry over a live one does the same
    /// hand-back first, so no entity keeps a handle into a registry the world no longer presents
    /// through and no view exists twice. Disposing the world is the permanent teardown; call
    /// <see cref="DotsModules.UninstallAll"/> first so the managed views are recycled while a
    /// registry still exists to recycle them.
    /// </para>
    /// </remarks>
    public static class DotsViewBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "Views";

        /// <summary>
        /// Creates (or overwrites) the registry singleton entity in <paramref name="world"/>.
        /// Idempotent: calling twice replaces the reference rather than creating a second singleton,
        /// because two singletons would make every <c>GetSingleton</c> call throw.
        /// </summary>
        /// <remarks>
        /// Root-scoped by default, matching the client wiring: the registry and its pools outlive a
        /// scene. Pass <see cref="DotsModuleScope.Session"/> for a world whose views belong to one
        /// connection, so <see cref="DotsModules.UninstallScope"/> takes them down with it.
        /// </remarks>
        public static Entity Install(World world, EntityViewRegistry registry, DotsModuleScope scope = DotsModuleScope.Root)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<EntityViewRegistryReference>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentObject(entity, new EntityViewRegistryReference { Registry = registry });
#if UNITY_EDITOR
                entityManager.SetName(entity, "EntityViewRegistry");
#endif
            }
            else
            {
                // Mutate the existing managed instance rather than re-adding the component: the
                // managed add/set overloads differ per Entities version, the field write does not.
                entity = query.GetSingletonEntity();
                var reference = entityManager.GetComponentObject<EntityViewRegistryReference>(entity);

                // Replacing the registry: the views the old one holds are recycled and their entities
                // re-request, so they reappear under the new registry on the next lifecycle tick
                // instead of standing on handles the new registry has never heard of. Same instance
                // twice is a plain re-install and touches nothing.
                if (!ReferenceEquals(reference.Registry, registry))
                {
                    ReleaseLinks(world, reference.Registry);
                    reference.Registry?.Clear();
                }

                reference.Registry = registry;
            }

            InstallSystems(world);
            DotsModules.Register(world, ModuleName, scope, Uninstall);
            return entity;
        }

        /// <summary>Whether a registry singleton is currently published in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world)
        {
            if (world == null || !world.IsCreated) return false;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EntityViewRegistryReference>());
            return !query.IsEmpty;
        }

        /// <summary>The registry the world presents through, or null when none is installed.</summary>
        public static EntityViewRegistry InstalledRegistry(World world)
        {
            if (world == null || !world.IsCreated) return null;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EntityViewRegistryReference>());
            if (query.IsEmpty) return null;

            return world.EntityManager.GetComponentObject<EntityViewRegistryReference>(query.GetSingletonEntity()).Registry;
        }

        /// <summary>
        /// Creates the package's group tree in <paramref name="world"/> and hangs it off the Unity
        /// groups. Idempotent — <c>GetOrCreateSystemManaged</c> returns the existing instance, and
        /// <c>AddSystemToUpdateList</c> ignores a system already in the list.
        /// </summary>
        /// <remarks>
        /// The empty groups are created too. Their positions are part of the package's published
        /// ordering surface from this version on, so a consumer's <c>[UpdateAfter]</c> resolves
        /// today and does not change meaning when the systems that fill them land.
        /// </remarks>
        public static void InstallSystems(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var initialization = world.GetOrCreateSystemManaged<InitializationSystemGroup>();
            var simulation = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var presentation = world.GetOrCreateSystemManaged<PresentationSystemGroup>();

            // GameplaySystemGroup declares [UpdateBefore(TransformSystemGroup)]. In the default world
            // that group already exists; in a hand-built one (tests, a headless world) it does not,
            // and Entities then drops the relation with a warning at sort time. Creating the empty
            // group makes the declared order real in every world the bootstrap installs into.
            simulation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<Unity.Transforms.TransformSystemGroup>());

            var netcode = world.GetOrCreateSystemManaged<NetcodeSystemGroup>();
            var provisioning = world.GetOrCreateSystemManaged<ProvisioningSystemGroup>();
            initialization.AddSystemToUpdateList(netcode);
            initialization.AddSystemToUpdateList(provisioning);

            // Snapshot application then prediction. Created here, empty, for the same reason the
            // other empty groups are: a consumer's [UpdateAfter] must resolve today and mean the
            // same thing once the optional assemblies that fill them are installed.
            var snapshotApply = world.GetOrCreateSystemManaged<SnapshotApplyGroup>();
            var prediction = world.GetOrCreateSystemManaged<PredictionSystemGroup>();
            netcode.AddSystemToUpdateList(snapshotApply);
            netcode.AddSystemToUpdateList(prediction);

            var gameplay = world.GetOrCreateSystemManaged<GameplaySystemGroup>();
            var movement = world.GetOrCreateSystemManaged<MovementSystemGroup>();
            var lifecycle = world.GetOrCreateSystemManaged<LifecycleSystemGroup>();
            var commandBuffer = world.GetOrCreateSystemManaged<DotsEndSimulationCommandBufferSystem>();
            simulation.AddSystemToUpdateList(gameplay);
            gameplay.AddSystemToUpdateList(movement);
            gameplay.AddSystemToUpdateList(lifecycle);
            gameplay.AddSystemToUpdateList(commandBuffer);

            var view = world.GetOrCreateSystemManaged<ViewSystemGroup>();
            var viewInterpolation = world.GetOrCreateSystemManaged<ViewInterpolationGroup>();
            var viewLifecycle = world.GetOrCreateSystemManaged<ViewLifecycleGroup>();
            var viewSync = world.GetOrCreateSystemManaged<ViewTransformSyncGroup>();
            presentation.AddSystemToUpdateList(view);
            // Empty without the netcode adapter, and created anyway — same rule as the netcode and
            // prediction groups above. Its position is what a consumer's [UpdateAfter] resolves
            // against, and a group that appeared later would shift the phase silently.
            view.AddSystemToUpdateList(viewInterpolation);
            view.AddSystemToUpdateList(viewLifecycle);
            view.AddSystemToUpdateList(viewSync);
            viewLifecycle.AddSystemToUpdateList(world.GetOrCreateSystem<EntityViewDespawnSystem>());
            viewLifecycle.AddSystemToUpdateList(world.GetOrCreateSystem<EntityViewSpawnSystem>());
            viewSync.AddSystemToUpdateList(world.GetOrCreateSystem<EntityViewTransformSyncSystem>());
            viewSync.AddSystemToUpdateList(world.GetOrCreateSystem<ViewOverlaySystem>());

            // Sorting is not automatic after a manual add: without this the UpdateAfter chain inside
            // each group is declared but not applied.
            initialization.SortSystems();
            simulation.SortSystems();
            presentation.SortSystems();
        }

        /// <summary>
        /// Recycles every live view, hands every linked entity its request back, and removes the
        /// singleton. Safe on a world that never had one, and safe to call twice.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Temporary, not permanent.</b> The systems stay created; each <c>RequireForUpdate</c>s
        /// the singleton this removes, so they idle until the next <see cref="Install"/>. Nothing
        /// native is disposed here because nothing native belongs to this module — the overlay
        /// buffer belongs to <see cref="ViewOverlaySystem"/> and is released in its <c>OnDestroy</c>
        /// when the world is disposed, after that system has completed its own job.
        /// </para>
        /// <para>
        /// Makes structural changes through <see cref="EntityManager"/> directly, so it must be
        /// called between frames — from a scene teardown, a DI scope disposal, a test — never from
        /// inside a system update.
        /// </para>
        /// </remarks>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using (var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<EntityViewRegistryReference>()))
            {
                if (!query.IsEmpty)
                {
                    var entity = query.GetSingletonEntity();
                    var registry = entityManager.GetComponentObject<EntityViewRegistryReference>(entity).Registry;
                    ReleaseLinks(world, registry);
                    registry?.Clear();
                    entityManager.DestroyEntity(entity);
                }
            }

            DotsModules.Unregister(world, ModuleName);
        }

        /// <summary>
        /// Strips every view link in the world and re-adds the <see cref="EntityViewRequest"/> the
        /// link was spawned from, so the entity respawns under whatever registry is installed next.
        /// </summary>
        /// <remarks>
        /// The key comes from the registry's own handle table, which is the only place it still
        /// exists once the request was consumed. A link the registry does not know — already
        /// despawned, or from a registry that was replaced without this running — is removed with an
        /// empty request when the entity carries a <see cref="ViewConfigRef"/> (the config supplies
        /// the key) and with no request otherwise, since there is nothing to respawn it as.
        /// </remarks>
        private static void ReleaseLinks(World world, EntityViewRegistry registry)
        {
            var entityManager = world.EntityManager;

            using (var linked = entityManager.CreateEntityQuery(ComponentType.ReadOnly<EntityViewLink>()))
            {
                if (!linked.IsEmpty)
                {
                    using var entities = linked.ToEntityArray(Allocator.Temp);
                    using var links = linked.ToComponentDataArray<EntityViewLink>(Allocator.Temp);

                    for (var i = 0; i < entities.Length; i++)
                    {
                        var entity = entities[i];
                        // Not `registry != null && TryGetKey(..., out var key)`: the
                        // short-circuit leaves `key` unassigned on the false branch and
                        // the compiler rejects the later read (CS0165).
                        var known = false;
                        string key = null;
                        if (registry != null)
                        {
                            known = registry.TryGetKey(links[i].ViewId, out key);
                        }

                        if (!entityManager.HasComponent<EntityViewRequest>(entity))
                        {
                            if (known)
                            {
                                entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = key });
                            }
                            else if (entityManager.HasComponent<ViewConfigRef>(entity))
                            {
                                entityManager.AddComponentData(entity, new EntityViewRequest());
                            }
                        }

                        entityManager.RemoveComponent<EntityViewLink>(entity);
                        if (entityManager.HasComponent<ViewTransformOffset>(entity)) entityManager.RemoveComponent<ViewTransformOffset>(entity);
                        if (entityManager.HasComponent<ViewSortingKey>(entity)) entityManager.RemoveComponent<ViewSortingKey>(entity);
                    }
                }
            }

            // Cleanup components keep a destroyed entity alive until the despawn system runs; with
            // the registry going away nothing would ever run it for these, so free them here.
            using (var cleanup = entityManager.CreateEntityQuery(ComponentType.ReadOnly<EntityViewLinkCleanup>()))
            {
                if (!cleanup.IsEmpty) entityManager.RemoveComponent<EntityViewLinkCleanup>(cleanup);
            }
        }
    }
}
