using Cuvara.DOTS.Simulation;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Writes one <see cref="MinimapEntry"/> per <see cref="MinimapMarker"/> entity into the buffer.
    /// </summary>
    /// <remarks>
    /// An <see cref="IJobChunk"/> rather than an <c>IJobEntity</c> because two of the inputs are
    /// optional — <see cref="EntityViewLink"/> (an entity without a view is still on the map) and
    /// <see cref="Health"/> (hp is a bonus, not a requirement) — and an <c>IJobEntity</c> parameter
    /// is a query filter. The chunk job asks each chunk whether it has them and reads them only when
    /// it does, so entities with and without a view land in the same list from the same pass.
    /// </remarks>
    [BurstCompile]
    internal struct MinimapCollectJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle EntityHandle;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> TransformHandle;
        [ReadOnly] public ComponentTypeHandle<MinimapMarker> MarkerHandle;
        [ReadOnly] public ComponentTypeHandle<EntityViewLink> LinkHandle;
        [ReadOnly] public ComponentTypeHandle<Health> HealthHandle;

        public MinimapPlane Plane;

        public NativeList<MinimapEntry>.ParallelWriter Entries;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(EntityHandle);
            var transforms = chunk.GetNativeArray(ref TransformHandle);
            var markers = chunk.GetNativeArray(ref MarkerHandle);

            var hasLinks = chunk.Has(ref LinkHandle);
            var links = hasLinks ? chunk.GetNativeArray(ref LinkHandle) : default;

            var hasHealth = chunk.Has(ref HealthHandle);
            var health = hasHealth ? chunk.GetNativeArray(ref HealthHandle) : default;

            var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (enumerator.NextEntityIndex(out var i))
            {
                var position = transforms[i].Position;
                var marker = markers[i];

                var fraction = -1f;
                if (hasHealth && health[i].Max > 0)
                {
                    fraction = math.saturate((float)health[i].Current / health[i].Max);
                }

                Entries.AddNoResize(new MinimapEntry
                {
                    Entity = entities[i],
                    ViewId = hasLinks ? links[i].ViewId : 0,
                    Category = marker.Category,
                    Position = Plane == MinimapPlane.XY ? position.xy : position.xz,
                    IsLocal = marker.IsLocal,
                    HealthFraction = fraction,
                });
            }
        }
    }
}
