using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Installs and removes the camera-follow module: the <see cref="CameraFollowConfig"/> singleton
    /// and <see cref="CameraFollowSystem"/> under <see cref="ViewSystemGroup"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CameraFollowSystem"/> is <c>[DisableAutoCreation]</c> like the rest of the package,
    /// and <c>DotsViewBootstrap</c> deliberately does not create it: a headless world, a server
    /// world and a test world all present views without owning <c>Camera.main</c>. So the camera is
    /// its own module with its own owner — session-scoped by default, because which entity the
    /// camera follows is a property of one connection, not of the composition root.
    /// </para>
    /// <para>
    /// <b>Validation happens here, before the system exists.</b> A NaN offset or a negative smooth
    /// time inside <c>OnUpdate</c> would put the camera at NaN and every subsequent frame at NaN;
    /// rejected at install it is a stack trace pointing at the config that was wrong.
    /// </para>
    /// <para>
    /// <see cref="Uninstall"/> destroys the system rather than leaving it idle: it is a managed
    /// <see cref="SystemBase"/> carrying smooth-damp velocity, and a reinstall after a scene change
    /// should start from rest rather than from the previous scene's momentum.
    /// </para>
    /// </remarks>
    public static class CameraFollowBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "CameraFollow";

        /// <summary>
        /// Validates <paramref name="config"/>, publishes it as the world's singleton (replacing any
        /// previous one) and creates the follow system. Idempotent by world.
        /// </summary>
        /// <exception cref="ArgumentException">A config value is NaN, infinite, or out of range.</exception>
        public static Entity Install(World world, CameraFollowConfig config, DotsModuleScope scope = DotsModuleScope.Session)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (config == null) throw new ArgumentNullException(nameof(config));
            Validate(config);

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<CameraFollowConfig>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentObject(entity, config);
#if UNITY_EDITOR
                entityManager.SetName(entity, "CameraFollowConfig");
#endif
            }
            else
            {
                // Swap the instance rather than copy fields into the old one: the consumer keeps
                // their own reference and expects a runtime tweak on it to take effect.
                entity = query.GetSingletonEntity();
                if (!ReferenceEquals(entityManager.GetComponentObject<CameraFollowConfig>(entity), config))
                {
                    entityManager.RemoveComponent<CameraFollowConfig>(entity);
                    entityManager.AddComponentObject(entity, config);
                }
            }

            InstallSystems(world);
            DotsModules.Register(world, ModuleName, scope, Uninstall);
            return entity;
        }

        /// <summary>
        /// Throws unless every field is finite and in range: <c>SmoothTime &gt;= 0</c>,
        /// <c>MaxSpeed &gt; 0</c>, <c>TeleportDistance &gt;= 0</c>, offsets finite. The camera
        /// reference is not checked — null means <c>Camera.main</c> and is legal.
        /// </summary>
        public static void Validate(CameraFollowConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            DotsModules.RequireAtLeast(config.SmoothTime, 0f, ModuleName, nameof(CameraFollowConfig.SmoothTime));
            DotsModules.RequireFinite(config.MaxSpeed, ModuleName, nameof(CameraFollowConfig.MaxSpeed));
            if (config.MaxSpeed <= 0f)
            {
                throw new ArgumentException(
                    $"[Cuvara.DOTS] {ModuleName}: MaxSpeed must be > 0, was {config.MaxSpeed}. A zero speed is a camera that never moves.");
            }

            RequireFinite(config.Offset, nameof(CameraFollowConfig.Offset));
            RequireFinite(config.LookAtOffset, nameof(CameraFollowConfig.LookAtOffset));
            DotsModules.RequireAtLeast(config.TeleportDistance, 0f, ModuleName, nameof(CameraFollowConfig.TeleportDistance));
        }

        /// <summary>
        /// Drops the follow system's spring state so its next update snaps to the target. For a
        /// reconnect or a scene load that lands the player somewhere else. No-op when not installed.
        /// </summary>
        public static void ResetSmoothing(World world)
        {
            if (world == null || !world.IsCreated) return;
            world.GetExistingSystemManaged<CameraFollowSystem>()?.ResetSmoothing();
        }

        /// <summary>
        /// Creates the follow system under <see cref="ViewSystemGroup"/>, creating the group path if
        /// the view bootstrap has not, and sorts so its <c>[UpdateAfter(ViewTransformSyncGroup)]</c>
        /// is applied rather than merely declared.
        /// </summary>
        public static void InstallSystems(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var presentation = world.GetOrCreateSystemManaged<PresentationSystemGroup>();
            var view = world.GetOrCreateSystemManaged<ViewSystemGroup>();
            var sync = world.GetOrCreateSystemManaged<ViewTransformSyncGroup>();
            presentation.AddSystemToUpdateList(view);
            view.AddSystemToUpdateList(sync);
            view.AddSystemToUpdateList(world.GetOrCreateSystemManaged<CameraFollowSystem>());
            presentation.SortSystems();
        }

        /// <summary>Whether the config singleton is published in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world)
        {
            if (world == null || !world.IsCreated) return false;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowConfig>());
            return !query.IsEmpty;
        }

        /// <summary>
        /// Removes the config singleton and destroys the follow system. Safe on a world that never
        /// had either, and safe twice. Leaves <see cref="CameraFollowTarget"/> tags on entities —
        /// they are the consumer's, and harmless with no system reading them.
        /// </summary>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using (var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<CameraFollowConfig>()))
            {
                if (!query.IsEmpty) entityManager.DestroyEntity(query);
            }

            var system = world.GetExistingSystemManaged<CameraFollowSystem>();
            if (system != null)
            {
                var view = world.GetExistingSystemManaged<ViewSystemGroup>();
                view?.RemoveSystemFromUpdateList(system);
                view?.SortSystems();
                world.DestroySystemManaged(system);
            }

            DotsModules.Unregister(world, ModuleName);
        }

        private static void RequireFinite(float3 value, string field)
        {
            DotsModules.RequireFinite(value.x, ModuleName, field + ".x");
            DotsModules.RequireFinite(value.y, ModuleName, field + ".y");
            DotsModules.RequireFinite(value.z, ModuleName, field + ".z");
        }
    }
}
