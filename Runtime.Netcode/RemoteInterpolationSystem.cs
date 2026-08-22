using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Schedules <see cref="RemoteInterpolationJob"/> over every replicated mirror whose transform
    /// nothing else claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The system is scheduling and nothing else.</b> It reads two blittable singletons, copies
    /// them into job fields and schedules; there is no per-frame allocation, no
    /// <c>ToEntityArray</c>, no <c>Complete()</c> and no main-thread walk over entities. The
    /// interpolated positions are consumed by <c>EntityViewTransformSyncSystem</c> two groups later,
    /// which schedules its own job against <see cref="SystemState.Dependency"/> and completes there
    /// — so the sync point this frame already had is the only one, and this system does not add a
    /// second.
    /// </para>
    /// <para>
    /// <b>Contrast <c>LocalPredictionSystem</c>, which is deliberately not the model here.</b> That
    /// system drives one entity through <c>EntityManager</c> and allocates a managed string per
    /// reconcile; it can afford to, because it touches the local player alone. This one runs over
    /// every entity in the area of interest at frame rate, where the same shape would put a managed
    /// allocation and a main-thread walk on the hot path.
    /// </para>
    /// <para>
    /// <b><c>ScheduleParallel</c> is safe here without further thought only because every write is
    /// to the entity being visited</b> — its own <c>LocalTransform</c>, <c>LocalToWorld</c> and
    /// <c>InterpolationState</c>. The two singletons are read once on the main thread and copied by
    /// value, so no job reads shared mutable state.
    /// </para>
    /// <para>
    /// <b><c>RequireForUpdate</c> on both singletons, so a world without the interpolation
    /// bootstrap does nothing rather than throwing.</b> Consumers install this through
    /// <see cref="DotsNetcodeBootstrap"/>, which seeds both; a world assembled by hand that skipped
    /// the seeding gets a system that never updates, which is the same degradation every other
    /// system in this package chose.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewInterpolationGroup))]
    [BurstCompile]
    internal partial struct RemoteInterpolationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<InterpolationSettings>();
            state.RequireForUpdate<InterpolationTimeline>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var settings = SystemAPI.GetSingleton<InterpolationSettings>();
            var timeline = SystemAPI.GetSingleton<InterpolationTimeline>();

            state.Dependency = new RemoteInterpolationJob
            {
                Clock = timeline.Clock,
                Config = settings.Config,
                Mapping = settings.Mapping,
            }.ScheduleParallel(state.Dependency);
        }
    }
}
