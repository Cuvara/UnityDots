using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// Singleton configuration for <see cref="CameraFollowSystem"/>. Publish as a
    /// managed singleton on the world to enable camera following.
    /// </summary>
    public sealed class CameraFollowConfig : IComponentData
    {
        /// <summary>Offset from the target entity in world space (e.g. (0, 10, -8) for isometric).</summary>
        public float3 Offset = new float3(0, 10f, -8f);

        /// <summary>Smooth damp time in seconds. Lower = snappier. 0 = instant.</summary>
        public float SmoothTime = 0.15f;

        /// <summary>Maximum speed the camera can move in units/s.</summary>
        public float MaxSpeed = 50f;

        /// <summary>Look-at offset from the target (usually 0 for center, or slight up).</summary>
        public float3 LookAtOffset = float3.zero;
    }

    /// <summary>
    /// Tag component marking an entity as the camera follow target (the local player).
    /// Exactly one entity should carry this at a time.
    /// </summary>
    public struct CameraFollowTarget : IComponentData { }
}
