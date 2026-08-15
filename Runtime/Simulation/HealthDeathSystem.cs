using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>Queues destruction for any entity whose <see cref="Health"/> reached zero.</summary>
    /// <remarks>
    /// Read-only over <see cref="Health"/> and structural only through the command buffer, so it
    /// parallelises with no ordering constraint between entities. See <see cref="TimeToLiveJob"/>
    /// for why the sort key must be a stable chunk index rather than a counter.
    /// </remarks>
    [BurstCompile]
    internal partial struct HealthDeathJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter CommandBuffer;

        private void Execute([ChunkIndexInQuery] int chunkIndex, Entity entity, in Health health)
        {
            if (health.Current <= 0) CommandBuffer.DestroyEntity(chunkIndex, entity);
        }
    }

    /// <summary>Destroys entities at zero health.</summary>
    /// <remarks>
    /// <para>
    /// Parallel since 0.17.0; recording moved off the main thread, playback did not move at all.
    /// </para>
    /// <para>
    /// <b>Destroying twice is not a hazard here.</b> An entity carrying both <see cref="Health"/> at
    /// zero and an expired <see cref="TimeToLive"/> is recorded by both jobs, and
    /// <c>EntityCommandBuffer</c> playback tolerates a repeated destroy. That was already true
    /// single-threaded; parallel recording does not change it.
    /// </para>
    /// </remarks>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(LifecycleSystemGroup))]
    internal partial struct HealthDeathSystem : ISystem
    {
        private EntityQuery _query;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = SystemAPI.QueryBuilder().WithAll<Health>().Build();
            state.RequireForUpdate<Health>();
            state.RequireForUpdate<DotsEndSimulationCommandBufferSystem.Singleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = SystemAPI
                .GetSingleton<DotsEndSimulationCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            // Scheduled only when there is enough work to pay for it — see ParallelScheduling.
            // Both of these measured SLOWER scheduled than run at every count up to 65,536, so the
            // threshold is doing real work here rather than guarding a marginal case.
            var job = new HealthDeathJob
            {
                CommandBuffer = commandBuffer.AsParallelWriter(),
            };
            state.Dependency = _query.CalculateEntityCount() >= ParallelScheduling.MinimumEntities
                ? job.ScheduleParallel(state.Dependency)
                : job.Schedule(state.Dependency);
        }
    }
}
