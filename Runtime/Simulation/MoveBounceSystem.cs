using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>Integrates velocity and reflects off the entity's own bounds.</summary>
    /// <remarks>
    /// Reads and writes only this entity's <see cref="LocalTransform"/> and <see cref="MoveData"/> —
    /// bounds are per-entity, not a shared world volume — so no entity's result depends on another's
    /// and the job parallelises without ordering constraints.
    /// </remarks>
    [BurstCompile]
    internal partial struct MoveBounceJob : IJobEntity
    {
        public float DeltaTime;

        private void Execute(ref LocalTransform transform, ref MoveData move)
        {
            var position = transform.Position + move.Velocity * DeltaTime;
            var velocity = move.Velocity;
            var min = move.BoundsMin;
            var max = move.BoundsMax;

            for (var axis = 0; axis < 3; axis++)
            {
                if (position[axis] >= min[axis] && position[axis] <= max[axis]) continue;

                velocity[axis] = -velocity[axis];
                position[axis] = math.clamp(position[axis], min[axis], max[axis]);
            }

            move.Velocity = velocity;
            transform.Position = position;
        }
    }

    /// <summary>Moves and bounces everything with a <see cref="MoveData"/>.</summary>
    /// <remarks>
    /// Parallel since 0.17.0; see <see cref="SpinSystem"/> for why <c>state.Dependency</c> is
    /// threaded rather than completed here.
    /// </remarks>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(MovementSystemGroup))]
    [UpdateAfter(typeof(MoveTowardSystem))]
    internal partial struct MoveBounceSystem : ISystem
    {
        private EntityQuery _query;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = SystemAPI.QueryBuilder().WithAllRW<Unity.Transforms.LocalTransform>().WithAll<MoveData>().Build();
            state.RequireForUpdate<MoveData>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Scheduled only above this job's own measured crossover — see ParallelScheduling for
            // the table. Below it this job was measurably slower scheduled than run, and this
            // package's entity count is AOI-bounded, so the common case is below it.
            var job = new MoveBounceJob { DeltaTime = SystemAPI.Time.DeltaTime };
            state.Dependency = _query.CalculateEntityCount() >= ParallelScheduling.MoveBounceMinimum
                ? job.ScheduleParallel(state.Dependency)
                : job.Schedule(state.Dependency);
        }
    }
}
