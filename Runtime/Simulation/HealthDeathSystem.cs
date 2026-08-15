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
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Health>();
            state.RequireForUpdate<DotsEndSimulationCommandBufferSystem.Singleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = SystemAPI
                .GetSingleton<DotsEndSimulationCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            state.Dependency = new HealthDeathJob
            {
                CommandBuffer = commandBuffer.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);
        }
    }
}
