using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;

namespace Cuvara.DOTS.Simulation
{
    /// <summary>Counts an entity's lifetime down and queues its destruction at zero.</summary>
    /// <remarks>
    /// <para>
    /// <b>Structural change from a parallel job, so an <see cref="EntityCommandBuffer.ParallelWriter"/>
    /// is not optional.</b> Destroying an entity from a worker thread is illegal; recording the
    /// intent is not. The main-thread <c>EntityCommandBuffer</c> is equally illegal here — its
    /// internal buffer is not safe for concurrent writes — which is why the parallel writer exists
    /// rather than being a performance nicety.
    /// </para>
    /// <para>
    /// <b><c>[ChunkIndexInQuery]</c> is the sort key, and it must be a stable index rather than a
    /// running counter.</b> Playback replays commands in sort-key order, so the key is what makes a
    /// parallel recording produce the same result every run: worker threads finish in whatever order
    /// the scheduler gives them, and without a deterministic key the playback order — and therefore
    /// the outcome — would vary run to run on identical input. The chunk index is stable for a given
    /// query and chunk layout; <c>[EntityIndexInQuery]</c> would also work and costs more to compute.
    /// </para>
    /// </remarks>
    [BurstCompile]
    internal partial struct TimeToLiveJob : IJobEntity
    {
        public float DeltaTime;

        public EntityCommandBuffer.ParallelWriter CommandBuffer;

        private void Execute([ChunkIndexInQuery] int chunkIndex, Entity entity, ref TimeToLive timeToLive)
        {
            timeToLive.Remaining -= DeltaTime;
            if (timeToLive.Remaining <= 0f) CommandBuffer.DestroyEntity(chunkIndex, entity);
        }
    }

    /// <summary>Destroys entities whose <see cref="TimeToLive"/> has run out.</summary>
    /// <remarks>
    /// Parallel since 0.17.0. The command buffer is still the package's own end-of-gameplay one, so
    /// playback is unchanged and still happens at a single declared point in the frame — only the
    /// recording moved off the main thread.
    /// </remarks>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(LifecycleSystemGroup))]
    [UpdateAfter(typeof(HealthDeathSystem))]
    internal partial struct TimeToLiveSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimeToLive>();
            state.RequireForUpdate<DotsEndSimulationCommandBufferSystem.Singleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = SystemAPI
                .GetSingleton<DotsEndSimulationCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            state.Dependency = new TimeToLiveJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                CommandBuffer = commandBuffer.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);
        }
    }
}
