namespace Cuvara.DOTS.Samples.StressBenchmark
{
    using System;
    using Unity.Mathematics;

    /// <summary>
    /// Accumulates per-frame timing during a tier and produces a summary.
    /// Preallocated, zero-GC during measurement.
    /// </summary>
    public sealed class BenchmarkMetrics
    {
        private readonly float[] samples;
        private int count;
        private double sum;
        private double max;
        private double min;

        public BenchmarkMetrics(int maxSamples = 65536)
        {
            samples = new float[maxSamples];
        }

        public void Reset()
        {
            count = 0;
            sum = 0;
            max = 0;
            min = double.MaxValue;
        }

        public void RecordFrame(float deltaTimeMs)
        {
            sum += deltaTimeMs;
            if (deltaTimeMs > max) max = deltaTimeMs;
            if (deltaTimeMs < min) min = deltaTimeMs;
            if (count < samples.Length)
                samples[count] = deltaTimeMs;
            count++;
        }

        public string Summarize(string tierLabel, int entityCount, double wallSeconds,
            int simCount, int physCount)
        {
            var fps = count / wallSeconds;
            var mean = sum / math.max(count, 1);
            var n = math.min(count, samples.Length);
            Array.Sort(samples, 0, n);

            return $"[STRESS-BENCH] tier={tierLabel} entities={entityCount} " +
                   $"sim={simCount} phys={physCount} " +
                   $"frames={count} wall={wallSeconds:F2}s " +
                   $"fps={fps:F1} mean={mean:F3}ms " +
                   $"median={Pct(0.50f, n):F3}ms " +
                   $"p95={Pct(0.95f, n):F3}ms p99={Pct(0.99f, n):F3}ms " +
                   $"max={max:F3}ms min={min:F3}ms";
        }

        private float Pct(float p, int n)
        {
            if (n == 0) return 0f;
            return samples[math.clamp((int)(p * n), 0, n - 1)];
        }
    }
}
