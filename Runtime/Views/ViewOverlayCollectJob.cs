using Cuvara.DOTS.Simulation;
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
    [BurstCompile]
    internal partial struct ViewOverlayCollectJob : IJobEntity
    {
        public NativeList<ViewOverlayData>.ParallelWriter Entries;

        private void Execute(
            in EntityViewLink link,
            in LocalToWorld transform,
            in ViewOverlayAnchor anchor,
            [Optional] in Health health)
        {
            var worldPos = transform.Position + math.mul(transform.Rotation, anchor.WorldOffset);

            float healthFraction = -1f;
            if (health.MaxHp > 0)
            {
                healthFraction = math.saturate((float)health.Hp / health.MaxHp);
            }

            Entries.AddNoResize(new ViewOverlayData
            {
                ViewId = link.ViewId,
                WorldPosition = worldPos,
                HealthFraction = healthFraction,
            });
        }
    }
}
