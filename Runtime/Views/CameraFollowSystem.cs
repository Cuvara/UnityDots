using Cuvara.DOTS.Groups;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Smoothly follows the entity tagged with <see cref="CameraFollowTarget"/> using
    /// <c>Camera.main</c>. Runs in <see cref="ViewSystemGroup"/> after transform sync
    /// so the camera sees the final rendered position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not Bursted — it touches <c>Camera.main</c> (managed UnityEngine object).
    /// Not parallelisable — there is one camera.
    /// </para>
    /// <para>
    /// Uses smooth damp (exponential decay) rather than lerp, so the camera converges
    /// at a speed independent of frame rate.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewSystemGroup))]
    [UpdateAfter(typeof(ViewTransformSyncGroup))]
    public partial class CameraFollowSystem : SystemBase
    {
        private EntityQuery _targetQuery;
        private float3 _velocity;

        protected override void OnCreate()
        {
            _targetQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<CameraFollowTarget, LocalToWorld>()
                .Build(EntityManager);

            RequireForUpdate(_targetQuery);
            RequireForUpdate<CameraFollowConfig>();
        }

        protected override void OnUpdate()
        {
            var camera = Camera.main;
            if (camera == null) return;

            var config = SystemAPI.ManagedAPI.GetSingleton<CameraFollowConfig>();
            var targetPos = _targetQuery.GetSingleton<LocalToWorld>().Position;

            var desiredPos = targetPos + config.Offset;
            var currentPos = (float3)camera.transform.position;

            float3 newPos;
            if (config.SmoothTime <= 0f)
            {
                newPos = desiredPos;
            }
            else
            {
                newPos = SmoothDamp(currentPos, desiredPos, ref _velocity,
                    config.SmoothTime, config.MaxSpeed, SystemAPI.Time.DeltaTime);
            }

            camera.transform.position = newPos;
            camera.transform.LookAt(
                (Vector3)(targetPos + config.LookAtOffset),
                Vector3.up);
        }

        private static float3 SmoothDamp(float3 current, float3 target, ref float3 velocity,
            float smoothTime, float maxSpeed, float dt)
        {
            smoothTime = math.max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

            float3 diff = current - target;
            float maxDist = maxSpeed * smoothTime;

            float magSq = math.lengthsq(diff);
            if (magSq > maxDist * maxDist)
                diff = diff / math.sqrt(magSq) * maxDist;

            float3 temp = (velocity + omega * diff) * dt;
            velocity = (velocity - omega * temp) * exp;
            float3 result = target + (diff + temp) * exp;

            // Prevent overshooting
            if (math.dot(target - current, result - target) > 0)
            {
                result = target;
                velocity = float3.zero;
            }

            return result;
        }
    }
}
