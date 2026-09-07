using System;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Modules;
using Unity.Entities;
using Unity.Physics.Systems;
using UnityEngine;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Installs and removes the physics-events module: the <see cref="PhysicsEventBuffer"/>
    /// singleton and <see cref="PhysicsEventCollectorSystem"/> after the physics simulation.
    /// </summary>
    /// <remarks>
    /// Same shape as every other module (<c>Documentation~/MODULE-LIFECYCLE.md</c>): idempotent
    /// install, safe-twice uninstall, owner recorded in <see cref="DotsModules"/>. Session-scoped by
    /// default — collision feedback belongs to a connection, and a reconnect wants a clean tracker.
    /// </remarks>
    public static class PhysicsEventsBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "PhysicsEvents";

        /// <summary>
        /// Publishes the buffer singleton and creates the collector under <see cref="PhysicsSystemGroup"/>.
        /// </summary>
        /// <param name="collisionPublisher">Optional; every buffered collision is also published here.</param>
        /// <param name="triggerPublisher">Optional; every buffered trigger event is also published here.</param>
        /// <param name="requirePhysicsPipeline">
        /// Throw, rather than warn, when the world has no <see cref="PhysicsSimulationGroup"/> — without
        /// one there is never a <c>SimulationSingleton</c> and the collector idles forever.
        /// </param>
        public static Entity Install(
            World world,
            DotsModuleScope scope = DotsModuleScope.Session,
            IDotsPublisher<EntityCollision> collisionPublisher = null,
            IDotsPublisher<EntityTriggerEvent> triggerPublisher = null,
            bool requirePhysicsPipeline = false)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (world.GetExistingSystemManaged<PhysicsSimulationGroup>() == null)
            {
                var message =
                    $"[Cuvara.DOTS] {ModuleName}: world '{world.Name}' has no PhysicsSimulationGroup, so no physics step " +
                    "will ever produce events for the collector to read. Install into the default world, or create " +
                    "Unity.Physics' systems in this one first.";
                if (requirePhysicsPipeline) throw new InvalidOperationException(message);
                Debug.LogWarning(message);
            }

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<PhysicsEventBuffer>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentObject(entity, new PhysicsEventBuffer());
#if UNITY_EDITOR
                entityManager.SetName(entity, "PhysicsEventBuffer");
#endif
            }
            else
            {
                entity = query.GetSingletonEntity();
            }

            var physics = world.GetOrCreateSystemManaged<PhysicsSystemGroup>();
            var collector = world.GetOrCreateSystemManaged<PhysicsEventCollectorSystem>();
            collector.CollisionPublisher = collisionPublisher ?? NullDotsPublisher<EntityCollision>.Instance;
            collector.TriggerPublisher = triggerPublisher ?? NullDotsPublisher<EntityTriggerEvent>.Instance;
            physics.AddSystemToUpdateList(collector);
            physics.SortSystems();

            DotsModules.Register(world, ModuleName, scope, Uninstall);
            return entity;
        }

        /// <summary>Whether the buffer singleton is published in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world)
        {
            if (world == null || !world.IsCreated) return false;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PhysicsEventBuffer>());
            return !query.IsEmpty;
        }

        /// <summary>The installed buffer, or null.</summary>
        public static PhysicsEventBuffer Buffer(World world)
        {
            if (world == null || !world.IsCreated) return null;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PhysicsEventBuffer>());
            return query.IsEmpty ? null : world.EntityManager.GetComponentObject<PhysicsEventBuffer>(query.GetSingletonEntity());
        }

        /// <summary>
        /// Destroys the singleton and the collector (whose <c>OnDestroy</c> completes its jobs and
        /// frees its lists). Safe on a world that never had it, and safe twice.
        /// </summary>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using (var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<PhysicsEventBuffer>()))
            {
                if (!query.IsEmpty) entityManager.DestroyEntity(query);
            }

            var collector = world.GetExistingSystemManaged<PhysicsEventCollectorSystem>();
            if (collector != null)
            {
                var physics = world.GetExistingSystemManaged<PhysicsSystemGroup>();
                physics?.RemoveSystemFromUpdateList(collector);
                physics?.SortSystems();
                world.DestroySystemManaged(collector);
            }

            DotsModules.Unregister(world, ModuleName);
        }
    }
}
