using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Rebuilds <see cref="MinimapBuffer.Entries"/> every frame from the entities carrying
    /// <see cref="MinimapMarker"/>. Installed by <see cref="MinimapBootstrap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Runs whether or not any entity is marked.</b> The system requires only the buffer
    /// singleton, never a non-empty query: a system that stopped updating when the last marked entity
    /// left would leave that entity's entry in the buffer for the consumer to draw forever. Clearing
    /// on the frame the set empties is the whole "no stale markers" guarantee, and it costs one
    /// <c>Clear()</c> on an empty list.
    /// </para>
    /// <para>
    /// <b>Independent of the view layer.</b> No <c>EntityViewRegistryReference</c> requirement and no
    /// <c>EntityViewLink</c> in the query: a marked entity whose key is still warming — or that never
    /// gets a view — is on the map at its true position with <c>ViewId = 0</c>. Placed in
    /// <see cref="ViewTransformSyncGroup"/> after the transform sync so it reads the same
    /// <c>LocalToWorld</c> the views were positioned from, including this frame's remote
    /// interpolation, but nothing in the simulation reads anything it writes: enabling the minimap
    /// changes what a renderer sees and nothing about how entities move.
    /// </para>
    /// <para>
    /// The collect job is completed inside <see cref="OnUpdate"/>, for the reason
    /// <c>EntityViewTransformSyncSystem</c> gives: the consumer is main-thread code reading the list
    /// later this frame, and a list a job may still be writing is not a list a renderer can read.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewTransformSyncGroup))]
    [UpdateAfter(typeof(EntityViewTransformSyncSystem))]
    internal partial struct MinimapDataSystem : ISystem
    {
        private EntityQuery _marked;

        private EntityTypeHandle _entityHandle;
        private ComponentTypeHandle<LocalToWorld> _transformHandle;
        private ComponentTypeHandle<MinimapMarker> _markerHandle;
        private ComponentTypeHandle<EntityViewLink> _linkHandle;
        private ComponentTypeHandle<Health> _healthHandle;

        public void OnCreate(ref SystemState state)
        {
            _marked = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MinimapMarker, LocalToWorld>()
                .Build(ref state);

            _entityHandle = state.GetEntityTypeHandle();
            _transformHandle = state.GetComponentTypeHandle<LocalToWorld>(isReadOnly: true);
            _markerHandle = state.GetComponentTypeHandle<MinimapMarker>(isReadOnly: true);
            _linkHandle = state.GetComponentTypeHandle<EntityViewLink>(isReadOnly: true);
            _healthHandle = state.GetComponentTypeHandle<Health>(isReadOnly: true);

            // The singleton and nothing else — see the remarks on why the query is deliberately not
            // a requirement.
            state.RequireForUpdate<MinimapBuffer>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.ManagedAPI.GetSingleton<MinimapBuffer>();
            if (buffer == null || !buffer.Entries.IsCreated) return;

            buffer.Entries.Clear();
            buffer.Version++;

            var count = _marked.CalculateEntityCount();
            if (count == 0) return;

            // Exact capacity + AddNoResize: a ParallelWriter cannot grow the list.
            if (buffer.Entries.Capacity < count) buffer.Entries.Capacity = count;

            _entityHandle.Update(ref state);
            _transformHandle.Update(ref state);
            _markerHandle.Update(ref state);
            _linkHandle.Update(ref state);
            _healthHandle.Update(ref state);

            state.Dependency = new MinimapCollectJob
            {
                EntityHandle = _entityHandle,
                TransformHandle = _transformHandle,
                MarkerHandle = _markerHandle,
                LinkHandle = _linkHandle,
                HealthHandle = _healthHandle,
                Plane = buffer.Plane,
                Entries = buffer.Entries.AsParallelWriter(),
            }.ScheduleParallel(_marked, state.Dependency);

            state.Dependency.Complete();
        }

        /// <summary>
        /// Releases the buffer if <see cref="MinimapBootstrap.Uninstall"/> did not already: the
        /// world-disposal path. Idempotent with the bootstrap's release.
        /// </summary>
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();

            if (!SystemAPI.ManagedAPI.HasSingleton<MinimapBuffer>()) return;

            var buffer = SystemAPI.ManagedAPI.GetSingleton<MinimapBuffer>();
            MinimapBootstrap.ReleaseEntries(buffer);
        }
    }
}
