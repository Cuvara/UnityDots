using Cuvara.DOTS.Groups;
using Unity.Collections;
using Unity.Entities;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Collects <see cref="ViewOverlayAnchor"/> data into a <see cref="ViewOverlayBuffer"/>
    /// singleton every frame. The host project's UI system reads this buffer to position
    /// world-space health bars, name plates, and damage numbers.
    /// </summary>
    /// <remarks>
    /// Runs in <see cref="ViewTransformSyncGroup"/>, after transform sync — so the overlay
    /// positions reflect this frame's entity positions.
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewTransformSyncGroup))]
    [UpdateAfter(typeof(EntityViewTransformSyncSystem))]
    internal partial struct ViewOverlaySystem : ISystem
    {
        private EntityQuery _anchored;

        public void OnCreate(ref SystemState state)
        {
            _anchored = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityViewLink, ViewOverlayAnchor>()
                .Build(ref state);

            state.RequireForUpdate(_anchored);
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

            var count = _anchored.CalculateEntityCount();
            buffer.Entries.Clear();
            if (buffer.Entries.Capacity < count)
                buffer.Entries.Capacity = count;

            state.Dependency = new ViewOverlayCollectJob
            {
                Entries = buffer.Entries.AsParallelWriter(),
            }.ScheduleParallel(_anchored, state.Dependency);

            state.Dependency.Complete();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.ManagedAPI.HasSingleton<ViewOverlayBuffer>())
            {
                var buffer = SystemAPI.ManagedAPI.GetSingleton<ViewOverlayBuffer>();
                if (buffer.Entries.IsCreated) buffer.Entries.Dispose();
            }
        }
    }
}
