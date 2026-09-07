using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The follow arithmetic, driven directly: parity with Unity's <c>Vector3.SmoothDamp</c>, the
    /// documented meaning of <c>MaxSpeed</c>, paused frames, zero smooth time, teleport and switch
    /// snaps, extreme distances, frame-rate independence, and non-finite inputs.
    /// </summary>
    public sealed class CameraFollowMathTests
    {
        private static readonly float3 Offset = new float3(0f, 10f, -8f);

        private static CameraFollowMath.Pose Step(
            float3 current, ref float3 velocity, float3 target, float dt,
            float smoothTime = 0.15f, float maxSpeed = 50f, float teleport = 0f, bool forceSnap = false)
            => CameraFollowMath.Step(current, ref velocity, target, Offset, float3.zero, smoothTime, maxSpeed, teleport, forceSnap, dt);

        [Test]
        public void SmoothDamp_MatchesUnitysVector3SmoothDamp_StepForStep()
        {
            // Same inputs into both; Unity's maxSpeed set to infinity so its distance clamp is off,
            // which is the one thing the port leaves out on purpose.
            float3 ours = new float3(0f, 0f, 0f), ourVel = float3.zero;
            Vector3 unity = Vector3.zero, unityVel = Vector3.zero;
            var target = new float3(12f, -3f, 7f);

            for (var i = 0; i < 120; i++)
            {
                ours = CameraFollowMath.SmoothDamp(ours, target, ref ourVel, 0.3f, 1f / 60f);
                unity = Vector3.SmoothDamp(unity, (Vector3)target, ref unityVel, 0.3f, float.PositiveInfinity, 1f / 60f);

                Assert.AreEqual(unity.x, ours.x, 1e-4f, $"x at step {i}");
                Assert.AreEqual(unity.y, ours.y, 1e-4f, $"y at step {i}");
                Assert.AreEqual(unity.z, ours.z, 1e-4f, $"z at step {i}");
            }

            Assert.Less(math.distance(ours, target), 0.05f, "and both have converged after two seconds");
        }

        [Test]
        public void MaxSpeed_IsAHardPerFrameLimit_AtAnyDistance()
        {
            var velocity = float3.zero;
            var current = float3.zero;
            var target = new float3(100000f, 0f, 0f); // extreme distance
            const float maxSpeed = 50f, dt = 1f / 60f;

            for (var i = 0; i < 30; i++)
            {
                var pose = Step(current, ref velocity, target, dt, maxSpeed: maxSpeed);
                var moved = math.distance(pose.Position, current);
                Assert.LessOrEqual(moved, maxSpeed * dt + 1e-4f, $"step {i} moved {moved}");
                Assert.IsTrue(CameraFollowMath.IsFinite(pose.Position));
                Assert.IsTrue(CameraFollowMath.IsFinite(velocity), "the spring does not bank energy it could not spend");
                current = pose.Position;
            }

            Assert.Greater(current.x, 0f, "it does move");
            Assert.LessOrEqual(current.x, maxSpeed * dt * 30f + 1e-3f, "and never faster than the limit says");
        }

        [Test]
        public void ZeroDeltaTime_HoldsPoseAndVelocity()
        {
            var velocity = new float3(3f, 0f, 0f);
            var before = velocity;
            var current = new float3(1f, 2f, 3f);

            var pose = Step(current, ref velocity, new float3(50f, 0f, 0f), 0f);
            var negative = Step(current, ref velocity, new float3(50f, 0f, 0f), -0.016f);
            var nan = Step(current, ref velocity, new float3(50f, 0f, 0f), float.NaN);

            Assert.IsTrue(pose.Held);
            Assert.IsTrue(negative.Held);
            Assert.IsTrue(nan.Held);
            Assert.AreEqual(current, pose.Position, "a paused frame does not move the camera");
            Assert.AreEqual(before, velocity, "nor touches the spring");
        }

        [Test]
        public void ZeroSmoothTime_SnapsToTheDesiredPose_AndZeroesVelocity()
        {
            var velocity = new float3(9f, 9f, 9f);
            var target = new float3(4f, 0f, 4f);

            var pose = Step(float3.zero, ref velocity, target, 1f / 60f, smoothTime: 0f);

            Assert.IsTrue(pose.Snapped);
            Assert.AreEqual(target + Offset, pose.Position);
            Assert.AreEqual(target, pose.LookAt);
            Assert.AreEqual(float3.zero, velocity);
        }

        [Test]
        public void ForceSnap_SnapsRegardlessOfSmoothTime()
        {
            var velocity = new float3(1f, 1f, 1f);
            var pose = Step(new float3(500f, 0f, 0f), ref velocity, float3.zero, 1f / 60f, forceSnap: true);

            Assert.IsTrue(pose.Snapped);
            Assert.AreEqual(Offset, pose.Position);
            Assert.AreEqual(float3.zero, velocity);
        }

        [Test]
        public void TeleportDistance_SnapsBeyondIt_DampsWithinIt()
        {
            var velocity = float3.zero;
            var near = Step(Offset, ref velocity, new float3(5f, 0f, 0f), 1f / 60f, teleport: 20f);
            Assert.IsFalse(near.Snapped, "5 units is within the 20-unit teleport radius: damped");
            Assert.Less(math.distance(near.Position, Offset), 5f);

            velocity = float3.zero;
            var far = Step(Offset, ref velocity, new float3(500f, 0f, 0f), 1f / 60f, teleport: 20f);
            Assert.IsTrue(far.Snapped, "500 units is a teleport: snapped");
            Assert.AreEqual(new float3(500f, 0f, 0f) + Offset, far.Position);

            velocity = float3.zero;
            var disabled = Step(Offset, ref velocity, new float3(500f, 0f, 0f), 1f / 60f, teleport: 0f);
            Assert.IsFalse(disabled.Snapped, "0 disables the teleport policy");
        }

        [Test]
        public void Converges_WithoutOvershoot_FromRest()
        {
            var velocity = float3.zero;
            var current = float3.zero;
            var target = new float3(10f, 0f, 0f);
            var desired = target + Offset;
            var lastDistance = math.distance(current, desired);

            for (var i = 0; i < 240; i++)
            {
                current = Step(current, ref velocity, target, 1f / 60f, maxSpeed: 1000f).Position;
                var distance = math.distance(current, desired);
                Assert.LessOrEqual(distance, lastDistance + 1e-4f, $"distance grew at step {i}: no overshoot from rest");
                lastDistance = distance;
            }

            Assert.Less(lastDistance, 1e-2f);
        }

        [Test]
        public void FrameRateVariation_LandsInTheSamePlace()
        {
            var target = new float3(20f, 0f, -5f);

            float3 Run(float dt, int steps)
            {
                var velocity = float3.zero;
                var current = float3.zero;
                for (var i = 0; i < steps; i++) current = Step(current, ref velocity, target, dt, maxSpeed: 1000f).Position;
                return current;
            }

            var at60 = Run(1f / 60f, 60);
            var at30 = Run(1f / 30f, 30);
            var at144 = Run(1f / 144f, 144);

            Assert.Less(math.distance(at60, at30), 0.15f, "one simulated second at 60 vs 30 Hz");
            Assert.Less(math.distance(at60, at144), 0.15f, "one simulated second at 60 vs 144 Hz");
        }

        [Test]
        public void VariableDeltaTimes_NeverExceedMaxSpeed_AndNeverGoNonFinite()
        {
            var velocity = float3.zero;
            var current = float3.zero;
            var target = new float3(300f, 0f, 300f);
            var random = new Unity.Mathematics.Random(7);
            const float maxSpeed = 40f;

            for (var i = 0; i < 500; i++)
            {
                var dt = random.NextFloat(0f, 0.25f); // includes zero and a hitch
                var pose = Step(current, ref velocity, target, dt, maxSpeed: maxSpeed);
                Assert.LessOrEqual(math.distance(pose.Position, current), maxSpeed * dt + 1e-3f, $"step {i} dt {dt}");
                Assert.IsTrue(CameraFollowMath.IsFinite(pose.Position));
                current = pose.Position;
            }
        }

        [Test]
        public void NonFiniteTarget_Holds_NonFiniteCamera_Recovers()
        {
            var velocity = float3.zero;
            var current = new float3(1f, 1f, 1f);

            var nanTarget = Step(current, ref velocity, new float3(float.NaN, 0f, 0f), 1f / 60f);
            Assert.IsTrue(nanTarget.Held, "a destroyed or corrupt target does not drag the camera to NaN");
            Assert.AreEqual(current, nanTarget.Position);

            var infTarget = Step(current, ref velocity, new float3(0f, float.PositiveInfinity, 0f), 1f / 60f);
            Assert.IsTrue(infTarget.Held);

            velocity = new float3(float.NaN, 0f, 0f);
            var recovered = Step(new float3(float.NaN, 0f, 0f), ref velocity, new float3(2f, 0f, 0f), 1f / 60f);
            Assert.IsTrue(recovered.Snapped, "a camera that went non-finite is put back on the target");
            Assert.AreEqual(new float3(2f, 0f, 0f) + Offset, recovered.Position);
            Assert.AreEqual(float3.zero, velocity);
        }
    }
}
