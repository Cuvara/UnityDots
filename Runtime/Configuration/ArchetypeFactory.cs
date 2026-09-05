using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Creates entities from <see cref="EntityArchetypePreset"/> definitions.
    /// </summary>
    public static class ArchetypeFactory
    {
        /// <summary>
        /// Creates an entity with all components defined by the preset.
        /// </summary>
        /// <param name="em">Entity manager.</param>
        /// <param name="preset">The archetype definition.</param>
        /// <param name="position">Initial world position.</param>
        /// <returns>The created entity.</returns>
        public static Entity Create(EntityManager em, EntityArchetypePreset preset, float3 position = default)
        {
            var entity = em.CreateEntity();

            // Transform — always present
            em.AddComponentData(entity, new LocalTransform
            {
                Position = position,
                Rotation = quaternion.identity,
                Scale = 1f,
            });
            em.AddComponentData(entity, new LocalToWorld { Value = float4x4.TRS(position, quaternion.identity, new float3(1)) });

            // View request
            if (!string.IsNullOrEmpty(preset.viewKey))
            {
                em.AddComponentData(entity, new EntityViewRequest
                {
                    ViewKey = new FixedString64Bytes(preset.viewKey),
                });
            }

            // Health
            if (preset.hasHealth)
            {
                em.AddComponentData(entity, new Health
                {
                    Current = preset.defaultHp,
                    Max = preset.defaultMaxHp,
                });
            }

            // MoveData
            if (preset.hasMoveData)
            {
                em.AddComponentData(entity, new MoveData());
            }

            // TimeToLive
            if (preset.hasTimeToLive && preset.timeToLive > 0f)
            {
                em.AddComponentData(entity, new TimeToLive
                {
                    Remaining = preset.timeToLive,
                });
            }

            // Overlay anchor
            if (preset.hasOverlayAnchor)
            {
                em.AddComponentData(entity, new ViewOverlayAnchor
                {
                    WorldOffset = new float3(preset.overlayOffset.x, preset.overlayOffset.y, preset.overlayOffset.z),
                });
            }

            return entity;
        }

        /// <summary>
        /// Creates multiple entities from the same preset at different positions.
        /// More efficient than calling <see cref="Create"/> in a loop for large batches.
        /// </summary>
        public static void CreateBatch(EntityManager em, EntityArchetypePreset preset,
            NativeArray<float3> positions, NativeList<Entity> output)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                output.Add(Create(em, preset, positions[i]));
            }
        }
    }
}
