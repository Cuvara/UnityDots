namespace Cuvara.DOTS.Samples.StressBenchmark
{
    using System;
    using UnityEngine;

    /// <summary>
    /// Any-scene activation. Launch with <c>-stress-pure</c> or <c>-stress-hybrid</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>-stress-pure</c> — pure DOTS, no GameObjects.</item>
    /// <item><c>-stress-hybrid</c> — hybrid, pooled GameObjects per entity (capped).</item>
    /// <item><c>-stress-no-physics</c> — skip Unity.Physics entities.</item>
    /// <item><c>-stress-quick</c> — use quick tiers (100 → 1K → 10K → 1M only).</item>
    /// <item><c>-stress-warmup N</c> — warmup seconds per tier (default 3).</item>
    /// <item><c>-stress-view-cap N</c> — max view entities for hybrid (default 50000).</item>
    /// </list>
    /// </remarks>
    public static class StressBenchmarkBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Activate()
        {
            var args = Environment.GetCommandLineArgs();
            var pure = Has(args, "-stress-pure");
            var hybrid = Has(args, "-stress-hybrid");
            if (!pure && !hybrid) return;

            if (UnityEngine.Object.FindAnyObjectByType<StressBenchmarkBase>() != null)
            {
                Debug.Log("[StressBenchmarkBootstrap] Scene already hosts a benchmark.");
                return;
            }

            var noPhysics = Has(args, "-stress-no-physics");
            var quick = Has(args, "-stress-quick");
            var warmup = Float(args, "-stress-warmup", 3f);
            var viewCap = (int)Float(args, "-stress-view-cap", 50000f);

            var host = new GameObject(pure ? "PureDotsBenchmark" : "HybridBenchmark");
            UnityEngine.Object.DontDestroyOnLoad(host);

            if (pure)
            {
                var bench = host.AddComponent<PureDotsBenchmark>();
                bench.enablePhysics = !noPhysics;
                bench.useDefaultTiers = !quick;
                bench.warmupPerTier = warmup;
            }
            else
            {
                var bench = host.AddComponent<HybridBenchmark>();
                bench.enablePhysics = !noPhysics;
                bench.useDefaultTiers = !quick;
                bench.warmupPerTier = warmup;
            }

            Debug.Log($"[StressBenchmarkBootstrap] {(pure ? "Pure DOTS" : "Hybrid")} " +
                      $"physics={!noPhysics} quick={quick} warmup={warmup}s " +
                      $"{(hybrid ? $"viewCap={viewCap}" : "")}");
        }

        private static bool Has(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static float Float(string[] args, string flag, float fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(args[i + 1], out var v))
                    return v;
            return fallback;
        }
    }
}
