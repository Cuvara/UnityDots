using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>Steps an entity toward its own target, stopping inside its stop distance.</summary>
    [BurstCompile]
    internal partial struct MoveTowardJob : IJobEntity
    {
        public float DeltaTime;

        private void Execute(ref LocalTransform transform, in MoveToward move)
        {
            var position = transform.Position;
            var toTarget = move.Target - position;
            var distance = math.length(toTarget);

            // Arrived — and the zero-distance case must be caught here, because normalising a
            // zero vector below would produce NaN and poison the transform permanently.
            if (distance <= move.StopDistance || distance <= math.EPSILON) return;

            var step = math.normalize(toTarget) * move.Speed * DeltaTime;
            if (math.length(step) > distance) step = toTarget;

            transform.Position = position + step;
        }
    }

    /// <summary>Moves everything with a <see cref="MoveToward"/> toward its target.</summary>
    /// <remarks>
    /// Parallel since 0.17.0. The target is a per-entity value rather than another entity's
    /// position, so nothing here reads state another worker may be writing — which is what makes
    /// the parallel schedule correct rather than merely fast.
    /// </remarks>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(MovementSystemGroup))]
    internal partial struct MoveTowardSystem : ISystem
    {
        private EntityQuery _query;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = SystemAPI.QueryBuilder().WithAllRW<Unity.Transforms.LocalTransform>().WithAll<MoveToward>().Build();
            state.RequireForUpdate<MoveToward>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Scheduled only when there is enough work to pay for scheduling. See
            // ParallelScheduling for the measurement — below the threshold every one of these jobs
            // was slower scheduled than run, and this package's entity count is AOI-bounded, so the
            // common case is below it.
            var job = new MoveTowardJob { DeltaTime = SystemAPI.Time.DeltaTime };
            state.Dependency = _query.CalculateEntityCount() >= ParallelScheduling.MinimumEntities
                ? job.ScheduleParallel(state.Dependency)
                : job.Schedule(state.Dependency);
        }
    }
}
