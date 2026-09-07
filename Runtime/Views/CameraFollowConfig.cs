using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>What <see cref="CameraFollowSystem"/> does when more than one entity carries <see cref="CameraFollowTarget"/>.</summary>
    public enum CameraFollowMultiTargetPolicy
    {
        /// <summary>Hold the camera still and report the error once. The safe default: a duplicate tag is a bug worth seeing.</summary>
        HoldAndReport = 0,

        /// <summary>Follow the target with the lowest entity index and say nothing. For consumers that stack tags deliberately.</summary>
        FollowLowestIndex = 1,
    }

    /// <summary>What <see cref="CameraFollowSystem"/> does the frame the followed entity changes.</summary>
    public enum CameraFollowSwitchPolicy
    {
        /// <summary>Snap to the new target and reset the spring. A respawn across the map does not glide.</summary>
        Snap = 0,

        /// <summary>Keep damping from the current pose toward the new target, subject to <see cref="CameraFollowConfig.TeleportDistance"/>.</summary>
        Smooth = 1,
    }

    /// <summary>
    /// Singleton configuration for <see cref="CameraFollowSystem"/>. Publish through
    /// <see cref="CameraFollowBootstrap.Install"/>, which validates it.
    /// </summary>
    /// <remarks>
    /// A managed component so it can carry a <see cref="UnityEngine.Camera"/>: a split-screen or
    /// render-texture setup supplies its own camera here instead of the package assuming
    /// <c>Camera.main</c>.
    /// </remarks>
    public sealed class CameraFollowConfig : IComponentData
    {
        /// <summary>Offset from the target entity in world space (e.g. (0, 10, -8) for isometric).</summary>
        public float3 Offset = new float3(0, 10f, -8f);

        /// <summary>Smooth damp time in seconds. Lower = snappier. 0 = instant.</summary>
        public float SmoothTime = 0.15f;

        /// <summary>
        /// Maximum speed the camera can move in units/s. A hard per-frame limit
        /// (<c>MaxSpeed * dt</c>), not Unity's spring-distance clamp — see <see cref="CameraFollowMath"/>.
        /// </summary>
        public float MaxSpeed = 50f;

        /// <summary>Look-at offset from the target (usually 0 for center, or slight up).</summary>
        public float3 LookAtOffset = float3.zero;

        /// <summary>
        /// The camera to drive. Null means <c>Camera.main</c>, resolved every frame so a scene that
        /// swaps its main camera is followed. A destroyed supplied camera holds the system idle.
        /// </summary>
        public Camera Camera;

        /// <summary>Policy when several entities carry the target tag.</summary>
        public CameraFollowMultiTargetPolicy MultipleTargets = CameraFollowMultiTargetPolicy.HoldAndReport;

        /// <summary>Policy the frame the followed entity changes (respawn, possession, spectate).</summary>
        public CameraFollowSwitchPolicy TargetSwitch = CameraFollowSwitchPolicy.Snap;

        /// <summary>
        /// Teleport policy: when &gt; 0 and the desired camera position is further than this from
        /// the current one, snap and reset the spring instead of gliding. 0 disables. Covers a
        /// server-side teleport and a reconnect that lands the player elsewhere.
        /// </summary>
        public float TeleportDistance = 0f;
    }

    /// <summary>
    /// Tag component marking an entity as the camera follow target (the local player).
    /// Exactly one entity should carry this at a time; see <see cref="CameraFollowConfig.MultipleTargets"/>
    /// for what happens otherwise.
    /// </summary>
    public struct CameraFollowTarget : IComponentData { }
}
