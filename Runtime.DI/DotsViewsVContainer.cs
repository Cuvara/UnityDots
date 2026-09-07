#if CUVARA_DOTS_VCONTAINER
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using Unity.Entities;
using UnityEngine;
using VContainer;

namespace Cuvara.DOTS.DI
{
    /// <summary>
    /// VContainer registration for the DOTS view layer, mirroring
    /// <c>GameFoundationVContainer.RegisterGameFoundation</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole DI surface of the package, and it is deliberately in its own optional
    /// assembly. The core types take plain constructors and know nothing about VContainer, so
    /// <c>com.cuvara.dots</c> installs and compiles against its four pinned Unity dependencies
    /// alone.
    /// </para>
    /// <para>
    /// The caller supplies <see cref="IViewAssetProvider"/> — either
    /// <c>RegisterGameFoundationViewProvisioning</c> from the GameFoundation assembly, or their
    /// own. This assembly must not know which, or it would need a UniT reference and stop being
    /// independently optional.
    /// </para>
    /// </remarks>
    public static class DotsViewsVContainer
    {
        /// <summary>
        /// Registers <see cref="EntityViewRegistry"/> and installs it into
        /// <paramref name="world"/> (defaulting to <c>World.DefaultGameObjectInjectionWorld</c>)
        /// once the container is built.
        /// </summary>
        /// <param name="viewRoot">Optional parent for spawned views. Null parents to the scene root.</param>
        public static IContainerBuilder RegisterDotsViews(this IContainerBuilder builder, Transform viewRoot = null, World world = null)
        {
            // Publishers first: the registry and provisioner take them as constructor arguments, and
            // with no messaging library installed these resolve to no-op publishers.
            builder.RegisterDotsMessaging();

            builder.Register(container => new EntityViewRegistry(
                    container.Resolve<IViewAssetProvider>(),
                    viewRoot,
                    container.Resolve<IDotsPublisher<ViewSpawned>>(),
                    container.Resolve<IDotsPublisher<ViewDespawned>>()),
                Lifetime.Singleton).AsSelf();

            // Resolved lazily, after the build callback has installed the systems: the cascade needs
            // the world, and the world's ECB system has to exist before a release can record into it.
            builder.Register<IViewCascadeSink>(
                container => new EntityViewCascade(
                    world ?? World.DefaultGameObjectInjectionWorld,
                    container.Resolve<EntityViewRegistry>()),
                Lifetime.Singleton);

            // Without the cascade sink the provisioner would release assets that live views are
            // still standing on — the dangling-link bug. This is the only call site that can supply
            // it, which is why RegisterGameFoundationViewProvisioning does not register a provisioner.
            builder.Register(container => new ChunkViewProvisioner(
                    container.Resolve<IViewAssetProvider>(),
                    container.Resolve<IViewCascadeSink>(),
                    container.Resolve<IDotsPublisher<ChunkWarmed>>(),
                    container.Resolve<IDotsPublisher<ChunkReleased>>(),
                    container.Resolve<IDotsPublisher<ChunkCascadeReleased>>()),
                Lifetime.Singleton).AsSelf();

            // The container owns the module's lifetime: disposing the scope uninstalls the views
            // (recycling every live GameObject, handing requests back) exactly as
            // DotsViewBootstrap.Uninstall documents. VContainer disposes IDisposable singletons it
            // created, so this is the one place teardown can be guaranteed to run for a DI consumer.
            builder.Register<DotsViewsLifetime>(Lifetime.Singleton).AsSelf();

            // Deferred to build time: the DOTS world and the container are created independently,
            // and resolving the registry during registration would invert that.
            builder.RegisterBuildCallback(container =>
            {
                var target = world ?? World.DefaultGameObjectInjectionWorld;
                if (target == null)
                {
                    Debug.LogWarning("[Cuvara.DOTS] No DOTS world available — entity views are disabled.");
                    return;
                }

                DotsViewBootstrap.Install(target, container.Resolve<EntityViewRegistry>());
                container.Resolve<DotsViewsLifetime>().Attach(target);
            });

            return builder;
        }
    }

    /// <summary>
    /// Ties the view module's teardown to the container that installed it. Created by
    /// <see cref="DotsViewsVContainer.RegisterDotsViews"/>; disposed by VContainer with the scope.
    /// </summary>
    /// <remarks>
    /// Holds the world only from <see cref="Attach"/> to <see cref="Dispose"/>, and
    /// <c>DotsViewBootstrap.Uninstall</c> is a no-op on a world that was disposed first — so
    /// whichever of the container and the world goes first, nothing dangles and nothing throws.
    /// </remarks>
    public sealed class DotsViewsLifetime : System.IDisposable
    {
        private World _world;

        /// <summary>The world this scope installed views into, or null before build / after dispose.</summary>
        public World World => _world;

        internal void Attach(World world) => _world = world;

        public void Dispose()
        {
            var world = _world;
            _world = null;
            if (world != null) DotsViewBootstrap.Uninstall(world);
        }
    }
}
#endif
