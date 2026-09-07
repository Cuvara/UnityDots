using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Unity.Entities;
using Unity.Physics.Systems;
using UnityEngine;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Installs and removes the physics-movement module: <see cref="PhysicsMovementBridge"/> under
    /// <see cref="MovementSystemGroup"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in the optional physics assembly for the reason every optional bootstrap does: the
    /// core must keep compiling with <c>com.unity.physics</c> absent, so it cannot name the bridge.
    /// The core's <see cref="DotsModules"/> still tears it down through the uninstaller recorded
    /// here, which is what lets a consumer's scene teardown be one call.
    /// </para>
    /// <para>
    /// <b>The one precondition worth checking is the physics pipeline itself.</b> The bridge writes
    /// <c>PhysicsVelocity</c>; only <see cref="PhysicsSystemGroup"/> integrates it. In the default
    /// world Unity creates that group automatically; in a hand-built world it does not exist, and a
    /// bridge installed there writes velocities nobody reads — an entity that never moves, with no
    /// error anywhere. Install warns by default and throws when asked to.
    /// </para>
    /// </remarks>
    public static class PhysicsMovementBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "PhysicsMovement";

        /// <summary>
        /// Creates the bridge under <see cref="MovementSystemGroup"/>, creating the group path if no
        /// other bootstrap has, and sorts the simulation group. Idempotent.
        /// </summary>
        /// <param name="requirePhysicsPipeline">
        /// Throw, rather than warn, when the world has no <see cref="PhysicsSystemGroup"/>. Leave off
        /// for test worlds that only inspect the group layout.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="requirePhysicsPipeline"/> is set and the world has no physics pipeline.
        /// </exception>
        public static void Install(World world, DotsModuleScope scope = DotsModuleScope.Session, bool requirePhysicsPipeline = false)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (world.GetExistingSystemManaged<PhysicsSystemGroup>() == null)
            {
                var message =
                    $"[Cuvara.DOTS] {ModuleName}: world '{world.Name}' has no PhysicsSystemGroup, so the velocities " +
                    "PhysicsMovementBridge writes will never be integrated. Install into the default world, or " +
                    "create Unity.Physics' systems in this one before installing the bridge.";
                if (requirePhysicsPipeline) throw new InvalidOperationException(message);
                Debug.LogWarning(message);
            }

            var simulation = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var gameplay = world.GetOrCreateSystemManaged<GameplaySystemGroup>();
            var movement = world.GetOrCreateSystemManaged<MovementSystemGroup>();
            simulation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<Unity.Transforms.TransformSystemGroup>());
            simulation.AddSystemToUpdateList(gameplay);
            gameplay.AddSystemToUpdateList(movement);
            movement.AddSystemToUpdateList(world.GetOrCreateSystem<PhysicsMovementBridge>());
            simulation.SortSystems();

            DotsModules.Register(world, ModuleName, scope, Uninstall);
        }

        /// <summary>Whether the bridge exists in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world) =>
            world != null && world.IsCreated && world.GetExistingSystem<PhysicsMovementBridge>() != SystemHandle.Null;

        /// <summary>
        /// Removes the bridge from its group and destroys it. Safe on a world that never had it, and
        /// safe twice. Physics components on entities are untouched — they are the consumer's.
        /// </summary>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var handle = world.GetExistingSystem<PhysicsMovementBridge>();
            if (handle != SystemHandle.Null)
            {
                var movement = world.GetExistingSystemManaged<MovementSystemGroup>();
                movement?.RemoveSystemFromUpdateList(handle);
                movement?.SortSystems();
                world.DestroySystem(handle);
            }

            DotsModules.Unregister(world, ModuleName);
        }
    }
}
