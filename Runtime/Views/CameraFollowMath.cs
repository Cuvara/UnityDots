using Unity.Mathematics;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// The camera-follow step as a pure function: no <c>Camera</c>, no world, no time source. This
    /// is what <see cref="CameraFollowSystem"/> calls once per frame and what the tests drive
    /// directly, at any frame rate, for any distance, with any delta time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The damping is Unity's own.</b> <see cref="SmoothDamp"/> is a line-for-line port of
    /// <c>Vector3.SmoothDamp</c> (the Game Programming Gems 4 critically damped spring with Unity's
    /// polynomial exponential and its overshoot guard) to <see cref="float3"/>, so its behaviour is a
    /// primitive players already know rather than custom math this package would have to defend.
    /// <c>CameraFollowMathTests</c> compares it against <c>Vector3.SmoothDamp</c> to keep it that way.
    /// </para>
    /// <para>
    /// <b>What is added on top, and why.</b> Unity's <c>maxSpeed</c> clamps the <i>distance the
    /// spring sees</i> to <c>maxSpeed * smoothTime</c>, which is not a speed limit in any sense a
    /// designer can reason about: the camera can still cover more than <c>maxSpeed * dt</c> in one
    /// frame while the spring is loaded. <see cref="Step"/> therefore applies a hard clamp on the
    /// per-frame displacement to <c>MaxSpeed * dt</c> after the spring step, and scales the spring's
    /// velocity down by the same factor so it does not accumulate energy it was not allowed to spend.
    /// <c>MaxSpeed</c> thus means exactly what its tooltip says: the camera never moves faster than
    /// this, in units per second, whatever the frame rate.
    /// </para>
    /// <para>
    /// <b>Paused frames do not jump.</b> A <c>dt &lt;= 0</c> returns the current pose unchanged with
    /// the velocity untouched — a paused editor, a zero-length frame after a load, a clamped
    /// <c>Time.maximumDeltaTime</c> of zero all leave the camera exactly where it was. A non-finite
    /// target does the same: the camera holds rather than propagating NaN into its transform, from
    /// where it never comes back.
    /// </para>
    /// </remarks>
    public static class CameraFollowMath
    {
        /// <summary>Result of one <see cref="Step"/>.</summary>
        public struct Pose
        {
            /// <summary>Where the camera should be this frame.</summary>
            public float3 Position;

            /// <summary>What the camera should look at this frame.</summary>
            public float3 LookAt;

            /// <summary>True when the step snapped rather than damped (zero smooth time, teleport, target switch).</summary>
            public bool Snapped;

            /// <summary>
            /// True when the step decided not to move at all (paused frame, non-finite target). The
            /// caller should leave the camera transform untouched rather than writing the pose back.
            /// </summary>
            public bool Held;
        }

        /// <summary>Smallest smooth time the spring accepts, matching <c>Vector3.SmoothDamp</c>.</summary>
        public const float MinSmoothTime = 0.0001f;

        /// <summary>Whether every component of <paramref name="value"/> is a finite number.</summary>
        public static bool IsFinite(float3 value) => math.all(math.isfinite(value));

        /// <summary>
        /// One frame of follow. Pure: reads <paramref name="current"/> and <paramref name="velocity"/>,
        /// returns the new pose and writes the new velocity.
        /// </summary>
        /// <param name="current">Camera position before the step.</param>
        /// <param name="velocity">Spring velocity carried between frames. Reset to zero on a snap.</param>
        /// <param name="target">Followed entity's world position this frame.</param>
        /// <param name="offset">Camera offset from the target (<see cref="CameraFollowConfig.Offset"/>).</param>
        /// <param name="lookAtOffset">Look-at offset from the target (<see cref="CameraFollowConfig.LookAtOffset"/>).</param>
        /// <param name="smoothTime">Damping time; <c>&lt;= 0</c> snaps.</param>
        /// <param name="maxSpeed">Hard per-second speed limit; must be &gt; 0.</param>
        /// <param name="teleportDistance">
        /// When &gt; 0 and the desired position is further than this from the current one, snap
        /// instead of gliding across the map. <c>0</c> disables.
        /// </param>
        /// <param name="forceSnap">Snap this frame regardless (a target switch under the snap policy, an explicit reset).</param>
        /// <param name="dt">Frame delta time; <c>&lt;= 0</c> holds.</param>
        public static Pose Step(
            float3 current,
            ref float3 velocity,
            float3 target,
            float3 offset,
            float3 lookAtOffset,
            float smoothTime,
            float maxSpeed,
            float teleportDistance,
            bool forceSnap,
            float dt)
        {
            var hold = new Pose { Position = current, LookAt = current, Snapped = false, Held = true };

            // A camera that has already gone non-finite cannot be damped back; the only safe
            // response is to accept the target pose outright the moment there is a finite one.
            if (!IsFinite(target) || !IsFinite(offset) || !IsFinite(lookAtOffset)) return hold;

            var desired = target + offset;
            var lookAt = target + lookAtOffset;

            if (!IsFinite(current) || !IsFinite(velocity))
            {
                velocity = float3.zero;
                return new Pose { Position = desired, LookAt = lookAt, Snapped = true };
            }

            if (dt <= 0f || !math.isfinite(dt))
            {
                hold.LookAt = lookAt;
                return hold;
            }


            var snap = forceSnap || smoothTime <= 0f;
            if (!snap && teleportDistance > 0f && math.distancesq(desired, current) > teleportDistance * teleportDistance)
            {
                snap = true;
            }

            if (snap)
            {
                velocity = float3.zero;
                return new Pose { Position = desired, LookAt = lookAt, Snapped = true };
            }

            var next = SmoothDamp(current, desired, ref velocity, smoothTime, dt);

            // The documented meaning of MaxSpeed, enforced: never more than maxSpeed * dt in a frame.
            var maxStep = maxSpeed * dt;
            var delta = next - current;
            var stepSq = math.lengthsq(delta);
            if (maxStep > 0f && math.isfinite(maxStep) && stepSq > maxStep * maxStep)
            {
                var scale = maxStep / math.sqrt(stepSq);
                next = current + delta * scale;
                velocity *= scale;
            }

            return new Pose { Position = next, LookAt = lookAt, Snapped = false };
        }

        /// <summary>
        /// <c>Vector3.SmoothDamp</c> on <see cref="float3"/>, without the distance clamp — see the
        /// class remarks for why the speed limit lives in <see cref="Step"/> instead.
        /// </summary>
        public static float3 SmoothDamp(float3 current, float3 target, ref float3 velocity, float smoothTime, float dt)
        {
            smoothTime = math.max(MinSmoothTime, smoothTime);
            var omega = 2f / smoothTime;
            var x = omega * dt;
            var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

            var change = current - target;
            var originalTarget = target;
            target = current - change;

            var temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            var output = target + (change + temp) * exp;

            // Unity's overshoot guard: if we passed the target, land on it and stop.
            if (math.dot(originalTarget - current, output - originalTarget) > 0f)
            {
                output = originalTarget;
                velocity = (output - originalTarget) / dt;
            }

            return output;
        }
    }
}
