namespace Cuvara.DOTS.Samples.StressBenchmark
{
    using Unity.Collections;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// Pure ECS stress benchmark — no GameObjects, no views, no rendering overhead.
    /// Measures raw throughput of cuvara.dots simulation systems + Unity.Physics.
    /// </summary>
    /// <remarks>
    /// This is the ceiling: the fastest the simulation can run on this hardware. The
    /// hybrid benchmark's delta against these numbers is the view-layer cost.
    /// </remarks>
    [AddComponentMenu("Cuvara/DOTS/Stress Benchmark (Pure DOTS)")]
    public sealed class PureDotsBenchmark : StressBenchmarkBase
    {
        protected override void Start()
        {
            Debug.Log("[STRESS-BENCH] Mode: Pure DOTS (no views, no GameObjects).");
            base.Start();
        }
    }
}
