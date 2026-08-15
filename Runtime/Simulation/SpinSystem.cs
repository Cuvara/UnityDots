using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>Rotates entities about Y at <see cref="SpinSpeed.RadiansPerSecond"/>.</summary>
    /// <remarks>
    /// Each entity's new rotation depends only on its own current rotation, so the work is
    /// embarrassingly parallel and the job is scheduled with <c>ScheduleParallel</c>.
    /// </remarks>
    [BurstCompile]
    internal partial struct SpinJob : IJobEntity
    {
        public float DeltaTime;

        private void Execute(ref LocalTransform transform, in SpinSpeed spin)
        {
            transform = transform.RotateY(spin.RadiansPerSecond * DeltaTime);
        }
    }

    /// <summary>Spins everything with a <see cref="SpinSpeed"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Parallel since 0.17.0.</b> Before that this was a Bursted <c>SystemAPI.Query</c> loop —
    /// optimised machine code on exactly one thread. The foundation was right and the parallelism
    /// was never built on top of it.
    /// </para>
    /// <para>
    /// <c>state.Dependency</c> is threaded in and out rather than the job being completed here:
    /// completing inside the system would serialise it against every other system in the frame and
    /// throw away most of the benefit. The job system resolves the ordering from the component
    /// access patterns it already knows.
    /// </para>
    /// </remarks>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(MovementSystemGroup))]
    [UpdateAfter(typeof(MoveBounceSystem))]
    internal partial struct SpinSystem : ISystem
    {
        private EntityQuery _query;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = SystemAPI.QueryBuilder().WithAllRW<Unity.Transforms.LocalTransform>().WithAll<SpinSpeed>().Build();
            state.RequireForUpdate<SpinSpeed>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Scheduled only when there is enough work to pay for scheduling. See
            // ParallelScheduling for the measurement — below the threshold every one of these jobs
            // was slower scheduled than run, and this package's entity count is AOI-bounded, so the
            // common case is below it.
            var job = new SpinJob { DeltaTime = SystemAPI.Time.DeltaTime };
            state.Dependency = _query.CalculateEntityCount() >= ParallelScheduling.MinimumEntities
                ? job.ScheduleParallel(state.Dependency)
                : job.Schedule(state.Dependency);
        }
    }
}
