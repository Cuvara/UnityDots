using Cuvara.DOTS.Groups;
using Unity.Burst;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Advances the world's render clock by one frame, before anything is evaluated against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A system of its own rather than the first lines of <see cref="RemoteInterpolationSystem"/>
    /// </b>, because the clock is advanced once per frame and the evaluation happens once per
    /// entity. Folding the advance into the job's <c>OnUpdate</c> would work today and would be
    /// wrong the moment a second consumer of the timeline exists — a diagnostic overlay, a camera
    /// that follows a remote entity — since whichever ran first would silently own the frame's
    /// advance and the other would read a clock that had or had not moved depending on group
    /// sorting.
    /// </para>
    /// <para>
    /// <b>Frame delta, not fixed-step delta.</b> <c>SystemAPI.Time.DeltaTime</c> read from
    /// <see cref="PresentationSystemGroup"/> is the real time since the last drawn frame, which is
    /// exactly what the render clock measures itself in. That is the reason
    /// <see cref="ViewInterpolationGroup"/> is in presentation at all.
    /// </para>
    /// <para>
    /// <b>Ordered before the evaluation by an explicit <c>[UpdateBefore]</c>, not by
    /// <c>OrderFirst</c>.</b> Entities sorts <c>OrderFirst</c> members into a separate batch and
    /// then drops ordering relations between that batch and ordinary members, with a warning — the
    /// same trap <see cref="MovementSystemGroup"/> documents. An explicit relation between two
    /// systems in one group is what actually holds.
    /// </para>
    /// <para>
    /// Nothing happens before the first snapshot: <c>Advance</c> returns immediately while the clock
    /// has no samples, so a world that has connected and heard nothing yet costs one no-op call.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ViewInterpolationGroup))]
    [UpdateBefore(typeof(RemoteInterpolationSystem))]
    [BurstCompile]
    internal partial struct InterpolationClockSystem : ISystem
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

            timeline.Clock.Advance(SystemAPI.Time.DeltaTime, settings.Config);

            SystemAPI.SetSingleton(timeline);
        }
    }
}
