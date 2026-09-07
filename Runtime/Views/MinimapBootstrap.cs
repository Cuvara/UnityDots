using System;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Installs and removes the minimap module: the <see cref="MinimapBuffer"/> singleton and
    /// <see cref="MinimapDataSystem"/> under <see cref="ViewTransformSyncGroup"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own module rather than part of <c>DotsViewBootstrap</c>, because a minimap is a property
    /// of one presentation — a headless or server world has views to recycle and no map to draw — and
    /// because the module owns a native container whose lifetime must be explicit. Session-scoped by
    /// default: the map belongs to a connection, and <see cref="DotsModules.UninstallScope"/> takes
    /// it down with the rest of the session.
    /// </para>
    /// <para>
    /// <b>What the host owns.</b> Projection from <see cref="MinimapEntry.Position"/> to map pixels,
    /// icon choice per <see cref="MinimapEntry.Category"/>, drawing, and deciding which entities carry
    /// <see cref="MinimapMarker"/>. The package owns collecting the positions of what was marked and
    /// guaranteeing the list is never stale.
    /// </para>
    /// <para>
    /// <b>Native buffer lifetime.</b> Allocated here (<c>Allocator.Persistent</c>), released by
    /// <see cref="Uninstall"/>, or by <see cref="MinimapDataSystem"/>'s <c>OnDestroy</c> when the
    /// world is disposed first. <see cref="ReleaseEntries"/> is the single release path and is
    /// idempotent, so both may run.
    /// </para>
    /// </remarks>
    public static class MinimapBootstrap
    {
        /// <summary>Name this module is recorded under in <see cref="DotsModules"/>.</summary>
        public const string ModuleName = "Minimap";

        /// <summary>Initial entry capacity when none is given. The list grows if more entities are marked.</summary>
        public const int DefaultCapacity = 64;

        /// <summary>
        /// Publishes the buffer singleton (replacing any previous one) and creates the producer
        /// system. Idempotent by world: a second call keeps the existing buffer, updates its plane,
        /// and bumps the module's install count.
        /// </summary>
        /// <param name="plane">Which world axes entries are projected onto. Match the world's <c>SnapshotSpaceMapping</c>.</param>
        /// <param name="initialCapacity">Entries pre-allocated; must be positive.</param>
        /// <returns>The singleton entity.</returns>
        public static Entity Install(
            World world,
            MinimapPlane plane = MinimapPlane.XZ,
            int initialCapacity = DefaultCapacity,
            DotsModuleScope scope = DotsModuleScope.Session)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (initialCapacity <= 0)
            {
                throw new ArgumentException(
                    $"[Cuvara.DOTS] {ModuleName}: initialCapacity must be > 0, was {initialCapacity}.", nameof(initialCapacity));
            }

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<MinimapBuffer>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentObject(entity, new MinimapBuffer
                {
                    Entries = new NativeList<MinimapEntry>(initialCapacity, Allocator.Persistent),
                    Plane = plane,
                });
#if UNITY_EDITOR
                entityManager.SetName(entity, "MinimapBuffer");
#endif
            }
            else
            {
                // Keep the allocation: a consumer may hold the buffer reference, and a fresh list
                // behind it would be one the consumer never sees. The plane is a setting and follows
                // the newest install; a released list (Uninstall raced a re-Install) is re-created.
                entity = query.GetSingletonEntity();
                var buffer = entityManager.GetComponentObject<MinimapBuffer>(entity);
                buffer.Plane = plane;
                if (!buffer.Entries.IsCreated)
                {
                    buffer.Entries = new NativeList<MinimapEntry>(initialCapacity, Allocator.Persistent);
                }
            }

            InstallSystems(world);
            DotsModules.Register(world, ModuleName, scope, Uninstall);
            return entity;
        }

        /// <summary>
        /// Creates the producer under <see cref="ViewTransformSyncGroup"/>, creating the group path if
        /// the view bootstrap has not, and sorts so the <c>[UpdateAfter]</c> is applied.
        /// </summary>
        public static void InstallSystems(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var presentation = world.GetOrCreateSystemManaged<PresentationSystemGroup>();
            var view = world.GetOrCreateSystemManaged<ViewSystemGroup>();
            var sync = world.GetOrCreateSystemManaged<ViewTransformSyncGroup>();
            presentation.AddSystemToUpdateList(view);
            view.AddSystemToUpdateList(sync);
            sync.AddSystemToUpdateList(world.GetOrCreateSystem<MinimapDataSystem>());
            presentation.SortSystems();
        }

        /// <summary>Whether the buffer singleton is published in <paramref name="world"/>.</summary>
        public static bool IsInstalled(World world)
        {
            if (world == null || !world.IsCreated) return false;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MinimapBuffer>());
            return !query.IsEmpty;
        }

        /// <summary>The installed buffer, or null. A consumer reads <see cref="MinimapBuffer.Entries"/> through this.</summary>
        public static MinimapBuffer InstalledBuffer(World world)
        {
            if (world == null || !world.IsCreated) return null;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MinimapBuffer>());
            if (query.IsEmpty) return null;

            return world.EntityManager.GetComponentObject<MinimapBuffer>(query.GetSingletonEntity());
        }

        /// <summary>
        /// Releases the native list, removes the singleton and destroys the producer. Safe on a world
        /// that never had the module, and safe twice. <see cref="MinimapMarker"/> components stay on
        /// their entities — they are the marker's owner's, and harmless with nothing collecting them.
        /// </summary>
        /// <remarks>
        /// The system is destroyed rather than left idle, unlike the view systems: it is the co-owner
        /// of the buffer's lifetime, and a system that outlived its buffer would have an
        /// <c>OnDestroy</c> with nothing to release — correct but a lie about ownership. A consumer
        /// holding the <see cref="MinimapBuffer"/> reference sees <see cref="MinimapBuffer.Count"/>
        /// read 0 from here on.
        /// </remarks>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;

            // Complete anything reading the list before it goes: the producer completes its own job
            // every update, so this covers only a consumer system that chained off it this frame.
            var handle = world.GetExistingSystem<MinimapDataSystem>();
            if (handle != SystemHandle.Null)
            {
                world.Unmanaged.ResolveSystemStateRef(handle).CompleteDependency();
            }

            using (var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<MinimapBuffer>()))
            {
                if (!query.IsEmpty)
                {
                    var entity = query.GetSingletonEntity();
                    ReleaseEntries(entityManager.GetComponentObject<MinimapBuffer>(entity));
                    entityManager.DestroyEntity(entity);
                }
            }

            if (handle != SystemHandle.Null)
            {
                var sync = world.GetExistingSystemManaged<ViewTransformSyncGroup>();
                sync?.RemoveSystemFromUpdateList(handle);
                sync?.SortSystems();
                world.DestroySystem(handle);
            }

            DotsModules.Unregister(world, ModuleName);
        }

        /// <summary>
        /// Disposes <see cref="MinimapBuffer.Entries"/> if it is still allocated and resets the field.
        /// The one release path, shared by <see cref="Uninstall"/> and the system's <c>OnDestroy</c>.
        /// </summary>
        internal static void ReleaseEntries(MinimapBuffer buffer)
        {
            if (buffer == null) return;
            if (buffer.Entries.IsCreated) buffer.Entries.Dispose();
            buffer.Entries = default;
        }
    }
}
