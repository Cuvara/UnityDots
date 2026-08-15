namespace Cuvara.DOTS.Simulation
{
    /// <summary>
    /// Per-system entity counts at which scheduling across workers starts paying for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, on 12 cores, Burst on, median of 41 interleaved pairs.</b> Each constant is that
    /// job's own crossover — the first tested count where <c>ScheduleParallel</c> overtook
    /// <c>Schedule</c> — not a shared guess:
    /// </para>
    /// <code>
    ///   job              crossover   speedup at 65,536
    ///   SpinJob              4,096        4.03x
    ///   MoveBounceJob       16,384        2.45x
    ///   MoveTowardJob       16,384        3.28x
    ///   HealthDeathJob      65,536        1.16x
    ///   TimeToLiveJob       65,536        1.24x
    /// </code>
    /// <para>
    /// <b>Why per-system rather than one number.</b> 0.19.0 used a single constant and said so
    /// honestly — it was a floor against a measured pessimisation, chosen when per-job data did not
    /// exist. It does now, and the jobs are visibly different in kind: <c>SpinJob</c> writes one
    /// component and scales nearly 4x, while <c>HealthDeathJob</c> and <c>TimeToLiveJob</c> reach
    /// only 1.16x and 1.24x even at 65,536 — barely more than the scheduling costs. Giving those two
    /// the shared 16,384 would have scheduled them at counts where they measured 0.45x–0.88x.
    /// </para>
    /// <para>
    /// <b>Marginal is not the same as useless.</b> The two cheap jobs keep a threshold rather than
    /// being forced serial, because a consumer running hundreds of thousands of entities should get
    /// the win; this package's own consumer will simply never reach it, since its entity count is
    /// bounded by the server's area of interest — tens to low hundreds.
    /// </para>
    /// <para>
    /// <b>These replace numbers measured without Burst, which were wrong in a predictable
    /// direction.</b> An earlier 12-core run reported crossovers of 256 and 1,024 with
    /// <c>BurstCompiler.IsEnabled == false</c>: the serial arm was running managed at ~535 ns/entity
    /// against 1–13 ns/entity compiled. Burst speeds the serial arm by roughly two orders of
    /// magnitude while barely touching scheduling overhead, so the true crossover had to be
    /// <i>higher</i>, never lower. Adopting 256 would have scheduled every job from 256 entities
    /// upward while measuring 0.4x–0.7x in exactly the range this package operates in.
    /// </para>
    /// <para>
    /// The crossover moves with core count, so these remain a compile-time approximation of a runtime
    /// property. They are deliberately conservative in the same direction: staying serial slightly
    /// past the true crossover costs a little throughput, while scheduling below it costs on every
    /// frame at the counts actually seen.
    /// </para>
    /// </remarks>
    internal static class ParallelScheduling
    {
        /// <summary>One component written, best scaling of the five.</summary>
        public const int SpinMinimum = 4096;

        /// <summary>Two components written; reflection branch limits vectorisation.</summary>
        public const int MoveBounceMinimum = 16384;

        /// <summary>Reads a target, writes a transform.</summary>
        public const int MoveTowardMinimum = 16384;

        /// <summary>
        /// Marginal at 1.16x even at 65,536: the per-entity work is a single comparison, so the
        /// command buffer and the schedule dominate.
        /// </summary>
        public const int HealthDeathMinimum = 65536;

        /// <summary>Marginal at 1.24x for the same reason — one subtract and one comparison.</summary>
        public const int TimeToLiveMinimum = 65536;
    }
}
