namespace Cuvara.DOTS.Samples.StressBenchmark
{
    /// <summary>
    /// One step of the stress ramp. Each tier spawns <see cref="EntityCount"/> entities,
    /// measures for <see cref="Seconds"/>, then advances.
    /// </summary>
    public readonly struct BenchmarkTier
    {
        public readonly string Label;
        public readonly int EntityCount;
        public readonly float Seconds;

        public BenchmarkTier(string label, int entityCount, float seconds)
        {
            Label = label;
            EntityCount = entityCount;
            Seconds = seconds;
        }

        /// <summary>The default ramp: 100 → 1K → 10K → 1M → 10M → 100M.</summary>
        public static readonly BenchmarkTier[] DefaultTiers =
        {
            new BenchmarkTier("100", 100, 10f),
            new BenchmarkTier("1K", 1_000, 10f),
            new BenchmarkTier("10K", 10_000, 10f),
            new BenchmarkTier("1M", 1_000_000, 15f),
            new BenchmarkTier("10M", 10_000_000, 15f),
            new BenchmarkTier("100M", 100_000_000, 15f),
        };

        /// <summary>A shorter ramp for quick smoke tests.</summary>
        public static readonly BenchmarkTier[] QuickTiers =
        {
            new BenchmarkTier("100", 100, 5f),
            new BenchmarkTier("1K", 1_000, 5f),
            new BenchmarkTier("10K", 10_000, 5f),
            new BenchmarkTier("1M", 1_000_000, 10f),
        };
    }
}
