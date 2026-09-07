using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Physics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Bridges <see cref="MoveData.Velocity"/> to <see cref="PhysicsVelocity.Linear"/> for entities
    /// tagged <see cref="PhysicsDrivenMovement"/>, letting Unity.Physics handle integration and
    /// collision response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One integrator per entity.</b> The tag is required here and excluded by
    /// <c>MoveBounceSystem</c>/<c>MoveTowardSystem</c>, so an entity is moved either by the direct
    /// movers or by Unity.Physics, never both. <c>PhysicsBodyFactory</c> adds the tag to every
    /// dynamic and kinematic body.
    /// </para>
    /// <para>
    /// <b>Timing.</b> This runs in <c>MovementSystemGroup</c> at render rate and writes a velocity;
    /// Unity.Physics integrates it in <c>FixedStepSimulationSystemGroup</c>. Between fixed steps the
    /// latest written velocity is what the next step uses — a velocity written twice in one fixed
    /// step is not applied twice. A predicted entity (<c>PredictedTransform</c>, netcode prediction
    /// assembly) is outside this path: prediction writes <c>LocalTransform</c> directly and must not
    /// be given a physics body. Documented in <c>Documentation~/PHYSICS.md</c>.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(MovementSystemGroup))]
    public partial struct PhysicsMovementBridge : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            new WriteVelocityJob().ScheduleParallel();
        }
    }

    [BurstCompile]
    [WithAll(typeof(PhysicsDrivenMovement))]
    internal partial struct WriteVelocityJob : IJobEntity
    {
        private void Execute(in MoveData move, ref PhysicsVelocity velocity)
        {
            velocity.Linear = move.Velocity;
        }
    }
}
