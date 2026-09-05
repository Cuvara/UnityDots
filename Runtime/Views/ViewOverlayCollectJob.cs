using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Burst job collecting overlay anchor data from entities with <see cref="ViewOverlayAnchor"/>.
    /// </summary>
    /// <remarks>
    /// Health is deliberately not read here. <c>Health</c> is optional, and an
    /// <c>IJobEntity</c> parameter makes it a query filter — entities without it would not
    /// get overlay data at all, which is wrong for name plates and damage numbers. The host
    /// project populates <see cref="ViewOverlayData.HealthFraction"/> from its own data source
    /// after reading the buffer.
    /// </remarks>
    [BurstCompile]
    internal partial struct ViewOverlayCollectJob : IJobEntity
    {
        public NativeList<ViewOverlayData>.ParallelWriter Entries;

        private void Execute(
            in EntityViewLink link,
            in LocalToWorld transform,
            in ViewOverlayAnchor anchor)
        {
            var worldPos = transform.Position + math.mul(transform.Rotation, anchor.WorldOffset);

            Entries.AddNoResize(new ViewOverlayData
            {
                ViewId = link.ViewId,
                WorldPosition = worldPos,
                HealthFraction = -1f,
            });
        }
    }
}
