using System;
using Cuvara.DOTS.Groups;
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
    /// </remarks>
    public static class DotsViewBootstrap
    {
        /// <summary>
        /// Creates (or overwrites) the registry singleton entity in <paramref name="world"/>.
        /// Idempotent: calling twice replaces the reference rather than creating a second singleton,
        /// because two singletons would make every <c>GetSingleton</c> call throw.
        /// </summary>
        public static Entity Install(World world, EntityViewRegistry registry)
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
                entityManager.GetComponentObject<EntityViewRegistryReference>(entity).Registry = registry;
            }

            InstallSystems(world);
            return entity;
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

            // Sorting is not automatic after a manual add: without this the UpdateAfter chain inside
            // each group is declared but not applied.
            initialization.SortSystems();
            simulation.SortSystems();
            presentation.SortSystems();
        }

        /// <summary>
        /// Recycles every live view and removes the singleton. Safe on a world that never had one.
        /// </summary>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<EntityViewRegistryReference>());
            if (query.IsEmpty) return;

            var entity = query.GetSingletonEntity();
            entityManager.GetComponentObject<EntityViewRegistryReference>(entity).Registry?.Clear();
            entityManager.DestroyEntity(entity);
        }
    }
}
