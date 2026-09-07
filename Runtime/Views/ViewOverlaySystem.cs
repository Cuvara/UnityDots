using Cuvara.DOTS.Groups;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Collects <see cref="ViewOverlayAnchor"/> data into a <see cref="ViewOverlayBuffer"/>
    /// singleton every frame. The host project's UI system reads this buffer to position
    /// world-space health bars, name plates, and damage numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs in <see cref="ViewTransformSyncGroup"/>, after transform sync — so the overlay
    /// positions reflect this frame's entity positions.
    /// </para>
    /// <para>
    /// <b>Requires the registry singleton, not a non-empty query.</b> Until this change the system
    /// also required at least one anchored entity, which meant that when the last one despawned the
    /// system stopped updating and its final entries stayed in the buffer — a stale name plate over
    /// an empty spot, for as long as the world lived. Now the buffer is cleared on that frame too.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewTransformSyncGroup))]
    [UpdateAfter(typeof(EntityViewTransformSyncSystem))]
    internal partial struct ViewOverlaySystem : ISystem
    {
        private EntityQuery _anchored;

        public void OnCreate(ref SystemState state)
        {
            // Every component the job's Execute reads, LocalToWorld included. Entities refuses to
            // schedule an IJobEntity over a custom query that is narrower than the job — with an
            // InvalidOperationException from inside the group update, which logs and leaves the
            // buffer empty rather than failing anything. The 0.26.0 query omitted LocalToWorld and no
            // test ran the system until 0.28; the first one that did found the buffer always empty.
            _anchored = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityViewLink, ViewOverlayAnchor, LocalToWorld>()
                .Build(ref state);

            state.RequireForUpdate<EntityViewRegistryReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // Ensure the buffer singleton exists
            var buffer = SystemAPI.ManagedAPI.HasSingleton<ViewOverlayBuffer>()
                ? SystemAPI.ManagedAPI.GetSingleton<ViewOverlayBuffer>()
                : null;

            if (buffer == null)
            {
                buffer = new ViewOverlayBuffer
                {
                    Entries = new NativeList<ViewOverlayData>(64, Allocator.Persistent),
                };
                var entity = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(entity, buffer);
            }

            buffer.Entries.Clear();
            buffer.Version++;

            var count = _anchored.CalculateEntityCount();
            if (count == 0) return;
            if (buffer.Entries.Capacity < count)
                buffer.Entries.Capacity = count;

            state.Dependency = new ViewOverlayCollectJob
            {
                Entries = buffer.Entries.AsParallelWriter(),
            }.ScheduleParallel(_anchored, state.Dependency);

            state.Dependency.Complete();
        }

        /// <summary>
        /// Releases the buffer this system allocated. Runs on world disposal — the permanent
        /// teardown — never on a module uninstall, which leaves the system created and idle.
        /// </summary>
        /// <remarks>
        /// The collect job is completed inside <see cref="OnUpdate"/>, so no job can still hold
        /// <c>Entries</c> here; <c>CompleteDependency</c> is the belt to that brace, for the case of
        /// a consumer system that chained off this one's <c>Dependency</c> in the same frame.
        /// </remarks>
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();

            if (SystemAPI.ManagedAPI.HasSingleton<ViewOverlayBuffer>())
            {
                var buffer = SystemAPI.ManagedAPI.GetSingleton<ViewOverlayBuffer>();
                if (buffer.Entries.IsCreated) buffer.Entries.Dispose();
                buffer.Entries = default;
            }
        }
    }
}
