using Cuvara.DOTS.Groups;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Follows the entity tagged with <see cref="CameraFollowTarget"/> with the camera named by
    /// <see cref="CameraFollowConfig"/> (or <c>Camera.main</c>). Runs in <see cref="ViewSystemGroup"/>
    /// after <see cref="ViewTransformSyncGroup"/>, which is after interpolation and lifecycle, so
    /// the camera sees the position that was actually rendered this frame — interpolated for a
    /// remote entity, predicted for the local one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not Bursted — it touches a managed <see cref="Camera"/>. Not parallelisable — one camera.
    /// The arithmetic lives in <see cref="CameraFollowMath"/>, pure and tested without a camera.
    /// </para>
    /// <para>
    /// <b>Behaviour at the edges is a policy, not an exception.</b> No target: the system does
    /// not update (<c>RequireForUpdate</c>) and the camera holds where it is — a destroyed target
    /// leaves finite values behind. Several targets: <see cref="CameraFollowConfig.MultipleTargets"/>.
    /// Target changed: <see cref="CameraFollowConfig.TargetSwitch"/>. Target far away:
    /// <see cref="CameraFollowConfig.TeleportDistance"/>. Zero delta time: the pose is unchanged.
    /// After a reconnect, <see cref="ResetSmoothing"/> (or <see cref="CameraFollowBootstrap.ResetSmoothing"/>)
    /// drops the spring so the first frame snaps rather than swings.
    /// </para>
    /// <para>
    /// Installed and removed by <see cref="CameraFollowBootstrap"/>, never by hand.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewSystemGroup))]
    [UpdateAfter(typeof(ViewTransformSyncGroup))]
    public partial class CameraFollowSystem : SystemBase
    {
        private EntityQuery _targetQuery;
        private float3 _velocity;
        private Entity _lastTarget = Entity.Null;
        private bool _snapNext = true;
        private bool _reportedTargetCount;

        /// <summary>Spring velocity carried between frames. Diagnostic; zero after a snap or reset.</summary>
        public float3 Velocity => _velocity;

        /// <summary>The entity followed last frame, or <see cref="Entity.Null"/>.</summary>
        public Entity CurrentTarget => _lastTarget;

        /// <summary>
        /// Drops the spring state so the next update snaps to the target instead of damping from
        /// wherever the camera was. Call after a reconnect, a scene load or an explicit teleport the
        /// <see cref="CameraFollowConfig.TeleportDistance"/> policy would not catch.
        /// </summary>
        public void ResetSmoothing()
        {
            _velocity = float3.zero;
            _snapNext = true;
        }

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
            var config = SystemAPI.ManagedAPI.GetSingleton<CameraFollowConfig>();

            // A supplied camera that was destroyed is a Unity "fake null": the reference compares
            // equal to null and the system idles until the consumer supplies another or clears it.
            var camera = config.Camera != null ? config.Camera : Camera.main;
            if (camera == null) return;

            if (!TryResolveTarget(config, out var targetEntity, out var targetPos)) return;

            // A target change is a switch, and the switch policy decides whether the spring carries
            // over. The very first frame is also a switch (from nothing), so it always snaps: a
            // camera that starts at the scene origin must not glide to the player on load.
            var switched = targetEntity != _lastTarget;
            _lastTarget = targetEntity;
            var forceSnap = _snapNext || (switched && config.TargetSwitch == CameraFollowSwitchPolicy.Snap);
            _snapNext = false;

            var pose = CameraFollowMath.Step(
                camera.transform.position,
                ref _velocity,
                targetPos,
                config.Offset,
                config.LookAtOffset,
                config.SmoothTime,
                config.MaxSpeed,
                config.TeleportDistance,
                forceSnap,
                SystemAPI.Time.DeltaTime);

            if (pose.Held) return;

            camera.transform.position = pose.Position;
            camera.transform.LookAt((Vector3)pose.LookAt, Vector3.up);
        }

        /// <summary>
        /// Picks the followed entity under the multi-target policy. False means "hold this frame".
        /// </summary>
        private bool TryResolveTarget(CameraFollowConfig config, out Entity target, out float3 position)
        {
            target = Entity.Null;
            position = float3.zero;

            var count = _targetQuery.CalculateEntityCount();
            if (count == 0) return false;

            if (count == 1)
            {
                _reportedTargetCount = false;
                target = _targetQuery.GetSingletonEntity();
                position = _targetQuery.GetSingleton<LocalToWorld>().Position;
                return CameraFollowMath.IsFinite(position);
            }

            if (config.MultipleTargets == CameraFollowMultiTargetPolicy.HoldAndReport)
            {
                if (!_reportedTargetCount)
                {
                    _reportedTargetCount = true;
                    Debug.LogError(
                        $"[Cuvara.DOTS] CameraFollowSystem found {count} entities tagged CameraFollowTarget; " +
                        "exactly one is expected. Remove the tag from every entity but the local player, or set " +
                        "CameraFollowConfig.MultipleTargets = FollowLowestIndex. The camera holds still until then. " +
                        "This is reported once.");
                }

                return false;
            }

            // FollowLowestIndex: deterministic across frames as long as the set does not change, and
            // the previously followed entity keeps priority while it is still tagged so a second tag
            // appearing does not yank the camera.
            using var entities = _targetQuery.ToEntityArray(Allocator.Temp);
            using var transforms = _targetQuery.ToComponentDataArray<LocalToWorld>(Allocator.Temp);

            var chosen = -1;
            for (var i = 0; i < entities.Length; i++)
            {
                if (entities[i] == _lastTarget)
                {
                    chosen = i;
                    break;
                }

                if (chosen < 0 || entities[i].Index < entities[chosen].Index) chosen = i;
            }

            target = entities[chosen];
            position = transforms[chosen].Position;
            return CameraFollowMath.IsFinite(position);
        }
    }
}
