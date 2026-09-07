using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Unity.Entities;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>
    /// Creates the simulation systems and puts them in the package's movement and lifecycle groups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the view bootstrap on purpose. The simulation components are usable without the
    /// view layer — an entity that moves, spins and expires needs no GameObject — so installing them
    /// must not require an <c>EntityViewRegistry</c> or an <c>IViewAssetProvider</c>. A consumer that
    /// wants both calls both.
    /// </para>
    /// <para>
    /// Every system carries <c>[DisableAutoCreation]</c>, matching the rest of the package: which
    /// systems exist in a world is the consumer's decision, not a side effect of the assembly being
    /// referenced.
    /// </para>
    /// <para>
    /// Idempotent. <c>GetOrCreateSystem</c> returns an existing instance and
    /// <c>AddSystemToUpdateList</c> ignores a system already in the list, so calling this twice —
    /// or after the view bootstrap, which creates the same groups — changes nothing.
    /// </para>
    /// </remarks>
    public static class DotsSimulationBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "Simulation";

        /// <summary>
        /// Installs the simulation systems into <paramref name="world"/>, creating the package's
        /// group tree if the view bootstrap has not already done so.
        /// </summary>
        /// <param name="scope">
        /// Who owns the module. Root by default — the client installs it once per world and leaves
        /// it; a test or a per-connection world passes Session.
        /// </param>
        public static void InstallSimulationSystems(World world, DotsModuleScope scope = DotsModuleScope.Root)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var simulation = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var gameplay = world.GetOrCreateSystemManaged<GameplaySystemGroup>();
            var movement = world.GetOrCreateSystemManaged<MovementSystemGroup>();
            var lifecycle = world.GetOrCreateSystemManaged<LifecycleSystemGroup>();
            var commandBuffer = world.GetOrCreateSystemManaged<DotsEndSimulationCommandBufferSystem>();

            // Same reason as in DotsViewBootstrap.InstallSystems: GameplaySystemGroup's
            // [UpdateBefore(TransformSystemGroup)] needs the target to exist to be applied.
            simulation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<Unity.Transforms.TransformSystemGroup>());
            simulation.AddSystemToUpdateList(gameplay);
            gameplay.AddSystemToUpdateList(movement);
            gameplay.AddSystemToUpdateList(lifecycle);
            gameplay.AddSystemToUpdateList(commandBuffer);

            movement.AddSystemToUpdateList(world.GetOrCreateSystem<MoveTowardSystem>());
            movement.AddSystemToUpdateList(world.GetOrCreateSystem<MoveBounceSystem>());
            movement.AddSystemToUpdateList(world.GetOrCreateSystem<SpinSystem>());

            lifecycle.AddSystemToUpdateList(world.GetOrCreateSystem<HealthDeathSystem>());
            lifecycle.AddSystemToUpdateList(world.GetOrCreateSystem<TimeToLiveSystem>());

            // Adding a system manually does not sort the group: without this the UpdateAfter chains
            // above are declared and not applied, and the systems run in insertion order by luck.
            simulation.SortSystems();

            DotsModules.Register(world, ModuleName, scope, Uninstall);
        }

        /// <summary>Whether the simulation systems exist in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world) =>
            world != null && world.IsCreated && world.GetExistingSystem<MoveTowardSystem>() != SystemHandle.Null;

        /// <summary>
        /// Removes the five simulation systems from their groups and destroys them. The groups
        /// themselves stay: they are shared with the view bootstrap and are part of the ordering
        /// surface consumers order against. Safe on a world that never had them, and safe twice.
        /// </summary>
        /// <remarks>
        /// Destroys rather than disables, because these systems own no native state worth keeping
        /// and an <c>ISystem</c> has no per-instance state to resume. Must be called between frames.
        /// </remarks>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var movement = world.GetExistingSystemManaged<MovementSystemGroup>();
            var lifecycle = world.GetExistingSystemManaged<LifecycleSystemGroup>();

            Remove<MoveTowardSystem>(world, movement);
            Remove<MoveBounceSystem>(world, movement);
            Remove<SpinSystem>(world, movement);
            Remove<HealthDeathSystem>(world, lifecycle);
            Remove<TimeToLiveSystem>(world, lifecycle);

            // Removal is applied at the next sort, and the handles are destroyed right after, so the
            // sort has to happen now — a group holding a destroyed handle is the failure this avoids.
            movement?.SortSystems();
            lifecycle?.SortSystems();

            DotsModules.Unregister(world, ModuleName);
        }

        private static void Remove<T>(World world, ComponentSystemGroup group) where T : unmanaged, ISystem
        {
            var handle = world.GetExistingSystem<T>();
            if (handle == SystemHandle.Null) return;

            group?.RemoveSystemFromUpdateList(handle);
            group?.SortSystems();
            world.DestroySystem(handle);
        }
    }
}
