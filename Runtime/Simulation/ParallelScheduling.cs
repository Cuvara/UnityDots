namespace Cuvara.DOTS.Simulation
{
    /// <summary>
    /// The entity count below which the package's jobs run on the calling thread instead of being
    /// scheduled across workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This number came out of a measurement that contradicted the change it was measuring.</b>
    /// Converting the simulation systems to <c>ScheduleParallel</c> in 0.17.0 was the obvious win;
    /// the benchmark then reported, on 4 cores, median of 41 interleaved pairs:
    /// </para>
    /// <code>
    ///                    64      256     1024     4096    16384    65536
    ///   SpinJob        0.40x    0.41x    0.54x    0.90x    1.63x    1.69x
    ///   MoveBounceJob  0.73x    0.79x    0.91x    0.41x    0.98x    0.88x
    ///   HealthDeathJob 0.67x    0.58x    0.73x    0.88x    0.45x    0.96x
    ///   TimeToLiveJob  0.60x    0.46x    0.39x    0.55x    0.59x    0.88x
    /// </code>
    /// <para>
    /// Below a few thousand entities every job is <b>slower</b> scheduled than run — the scheduling
    /// overhead is fixed and the work is not. That matters here specifically: this package's entity
    /// count is bounded by the server's area of interest, which is tens to low hundreds, so an
    /// unconditional <c>ScheduleParallel</c> would have made the common case worse in exchange for a
    /// win nobody in this project reaches.
    /// </para>
    /// <para>
    /// <b>16,384 is <see cref="SpinJob"/>'s measured crossover and nothing more.</b>
    /// <c>MoveBounceJob</c>, <c>HealthDeathJob</c> and <c>TimeToLiveJob</c> never overtook their
    /// serial form at any count up to 65,536, so their true thresholds are unknown and are certainly
    /// higher than this. Using one constant is a deliberate simplification: it is a floor that
    /// prevents the measured pessimisation, not a per-system tuning. Anyone raising a per-system
    /// value should raise it against a fresh measurement on the target hardware.
    /// </para>
    /// <para>
    /// The crossover moves with core count, so this is a compile-time approximation of a runtime
    /// property. It is deliberately conservative: being serial slightly past the true crossover
    /// costs a little throughput, while being parallel below it costs on every frame at the counts
    /// this package actually runs at.
    /// </para>
    /// </remarks>
    internal static class ParallelScheduling
    {
        /// <summary>Schedule across workers at or above this many entities; run inline below it.</summary>
        public const int MinimumEntities = 16384;
    }
}
