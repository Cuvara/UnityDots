using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Physics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Bridges <see cref="MoveData.Velocity"/> to <see cref="PhysicsVelocity.Linear"/>,
    /// letting Unity.Physics handle integration and collision response.
    /// </summary>
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
    internal partial struct WriteVelocityJob : IJobEntity
    {
        private void Execute(in MoveData move, ref PhysicsVelocity velocity)
        {
            velocity.Linear = move.Velocity;
        }
    }
}
