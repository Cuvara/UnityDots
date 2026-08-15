using System.Text;
using Cuvara.DOTS.Simulation;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
// Aliased rather than importing System.Diagnostics: that namespace also has a Debug, and importing
// it alongside UnityEngine makes every Debug.Log in this file CS0104-ambiguous.
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Cuvara.DOTS.Tests
{
    // The jobs are scheduled from systems rather than from the test body, and that is a hard
    // requirement rather than a style choice: IJobEntity's Run/Schedule/ScheduleParallel methods are
    // emitted by the source generator only for call sites inside an ISystem or SystemBase. Calling
    // them from a plain class compiles against the stub and throws at runtime with
    // "This method should have been replaced by source gen." — which names the mechanism but not the
    // rule, so it is worth stating here.
    //
    // Each pair schedules the SAME job struct the shipping system uses. Nothing is reimplemented for
    // the benchmark, so what is timed is the schedule and not a lookalike.

    [DisableAutoCreation]
    internal partial struct SpinRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) => new SpinJob { DeltaTime = 0.016f }.Run();
    }

    [DisableAutoCreation]
    internal partial struct SpinParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // Completed inside the system only because a benchmark has to measure a finished unit of
            // work. The shipping SpinSystem deliberately does NOT complete here — it threads
            // state.Dependency out so the job overlaps with the rest of the frame.
            new SpinJob { DeltaTime = 0.016f }.ScheduleParallel(state.Dependency).Complete();
        }
    }

    [DisableAutoCreation]
    internal partial struct MoveBounceRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) => new MoveBounceJob { DeltaTime = 0.016f }.Run();
    }

    [DisableAutoCreation]
    internal partial struct MoveBounceParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            new MoveBounceJob { DeltaTime = 0.016f }.ScheduleParallel(state.Dependency).Complete();
        }
    }

    /// <summary>
    /// Measures the parallel schedule against the single-threaded one, so "this is faster" is a
    /// number rather than an argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The comparison is the same job scheduled two ways</b> — <c>Run()</c> versus
    /// <c>ScheduleParallel()</c> — not a job against a hand-written loop. Both execute byte-identical
    /// Bursted code over identical chunks, so the difference measured is exactly what the change
    /// bought: worker parallelism, minus scheduling overhead. Comparing against a <c>foreach</c>
    /// would fold in codegen differences and measure the wrong thing.
    /// </para>
    /// <para>
    /// <b>These numbers are a shape, not a spec.</b> They come from whatever machine ran them — a
    /// shared CI runner is capped at a handful of cores and is noisy — so the useful output is the
    /// <i>crossover</i>: the entity count below which scheduling costs more than it saves. That point
    /// moves with core count; that one exists does not.
    /// </para>
    /// <para>
    /// Nothing here asserts a timing. A performance assertion on a shared runner is a flaky test, and
    /// a flaky test inside a gate is worse than no measurement — it teaches people to re-run until
    /// green. The timings are logged; the assertions are about correctness.
    /// </para>
    /// </remarks>
    public sealed class ParallelSchedulingBenchmark
    {
        private static readonly int[] EntityCounts = { 64, 256, 1024, 4096, 16384, 65536 };

        private const int Warmup = 5;
        private const int Iterations = 30;

        private World _world;
        private EntityManager _entityManager;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.ParallelBenchmark");
            _entityManager = _world.EntityManager;
        }

        [TearDown]
        public void TearDown() => _world.Dispose();

        private void Populate(int count)
        {
            _entityManager.DestroyEntity(_entityManager.UniversalQuery);

            var archetype = _entityManager.CreateArchetype(
                typeof(LocalTransform), typeof(SpinSpeed), typeof(MoveData));

            using var entities = _entityManager.CreateEntity(archetype, count, Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                _entityManager.SetComponentData(entities[i], LocalTransform.FromPosition(i, 0f, 0f));
                _entityManager.SetComponentData(entities[i], new SpinSpeed { RadiansPerSecond = 1f + (i % 7) });
                _entityManager.SetComponentData(entities[i], new MoveData
                {
                    Velocity = new float3(1f, 0f, 0.5f),
                    BoundsMin = new float3(-1000f, -1000f, -1000f),
                    BoundsMax = new float3(1000f, 1000f, 1000f),
                });
            }
        }

        private void Tick<T>() where T : unmanaged, ISystem =>
            _world.GetExistingSystem<T>().Update(_world.Unmanaged);

        [Test]
        public void SpinJob_ParallelVersusSingleThreaded()
        {
            _world.GetOrCreateSystem<SpinRunSystem>();
            _world.GetOrCreateSystem<SpinParallelSystem>();

            Report("SpinJob", count =>
            {
                Populate(count);
                return (Time(Tick<SpinRunSystem>), Time(Tick<SpinParallelSystem>));
            });
        }

        [Test]
        public void MoveBounceJob_ParallelVersusSingleThreaded()
        {
            _world.GetOrCreateSystem<MoveBounceRunSystem>();
            _world.GetOrCreateSystem<MoveBounceParallelSystem>();

            Report("MoveBounceJob", count =>
            {
                Populate(count);
                return (Time(Tick<MoveBounceRunSystem>), Time(Tick<MoveBounceParallelSystem>));
            });
        }

        /// <summary>
        /// The determinism check the parallel schedule has to earn: identical input through both
        /// paths must give bit-identical output.
        /// </summary>
        /// <remarks>
        /// Not a formality. A parallel job whose result depends on iteration order is a bug that
        /// reproduces roughly one run in ten, and these systems produce positions a predictor may
        /// later reconcile against. Bit-identical rather than approximately equal, because "close
        /// enough" is how a drift bug survives its own test.
        /// </remarks>
        [Test]
        public void BothSchedules_ProduceBitIdenticalResults()
        {
            const int count = 4096;

            _world.GetOrCreateSystem<MoveBounceRunSystem>();
            _world.GetOrCreateSystem<MoveBounceParallelSystem>();

            float3[] Integrate(bool parallel)
            {
                Populate(count);
                for (var step = 0; step < 8; step++)
                {
                    if (parallel) Tick<MoveBounceParallelSystem>();
                    else Tick<MoveBounceRunSystem>();
                }

                using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<LocalTransform>());
                using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var positions = new float3[transforms.Length];
                for (var i = 0; i < transforms.Length; i++) positions[i] = transforms[i].Position;
                return positions;
            }

            var single = Integrate(parallel: false);
            var scheduled = Integrate(parallel: true);

            Assert.AreEqual(single.Length, scheduled.Length);
            for (var i = 0; i < single.Length; i++)
            {
                Assert.IsTrue(single[i].Equals(scheduled[i]),
                    $"entity {i} diverged: single-threaded {single[i]} vs parallel {scheduled[i]}");
            }
        }

        private static double Time(System.Action action)
        {
            for (var i = 0; i < Warmup; i++) action();

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < Iterations; i++) action();
            clock.Stop();

            return clock.Elapsed.TotalMilliseconds / Iterations;
        }

        private static void Report(string name, System.Func<int, (double Single, double Parallel)> measure)
        {
            var report = new StringBuilder();
            report.AppendLine($"[benchmark] {name} — ms per invocation, {Iterations} iterations after {Warmup} warmup");
            report.AppendLine($"[benchmark] processors reported by the runtime: {SystemInfo.processorCount}");
            // Burst has no public per-job "was this compiled" query, so the global flag plus the
            // per-entity cost is the best evidence available. It matters: with Burst off, this
            // measures IL against IL and the ratio is still meaningful, but the absolute numbers
            // are ~100x the shipping ones and must not be quoted as such.
            report.AppendLine($"[benchmark] BurstCompiler.IsEnabled: {BurstCompiler.IsEnabled}");
            report.AppendLine($"[benchmark] (no public API reports per-job compilation; ns/entity below is the cross-check)");
            // Read this before believing any row. This workflow runs three Unity containers at once,
            // each requesting 4 CPUs from one host, so a benchmark here measures contention as much
            // as parallelism. Two runs of this identical code produced 0.88 ms and 80.07 ms for the
            // same 65536-entity case — 90x apart. Trust the ns/entity column as a sanity check and
            // the crossover as a shape; do not quote a speedup from CI. Run it on real hardware.
            report.AppendLine("[benchmark] WARNING: CI runs three Unity jobs concurrently on one host —");
            report.AppendLine("[benchmark] these timings include contention. Numbers from CI are not quotable.");
            report.AppendLine("[benchmark]   entities |   Run() |  Parallel |  speedup");

            var crossover = -1;
            foreach (var count in EntityCounts)
            {
                var (single, parallel) = measure(count);
                var speedup = parallel > 0d ? single / parallel : 0d;
                if (crossover < 0 && speedup > 1d) crossover = count;

                // ns per entity makes a Burst fallback visible: a trivial job that costs ~1us per
                // entity is not running compiled code, whatever the attribute says.
                var singleNs = single * 1_000_000d / count;
                var parallelNs = parallel * 1_000_000d / count;
                report.AppendLine(
                    $"[benchmark] {count,10} | {single,7:F4} | {parallel,9:F4} | {speedup,7:F2}x" +
                    $"   ({singleNs,7:F1} / {parallelNs,7:F1} ns per entity)");
            }

            report.AppendLine(crossover < 0
                ? "[benchmark] crossover: NONE — the parallel schedule never won at these counts on this machine"
                : $"[benchmark] crossover: parallel first wins at {crossover} entities on this machine");

            Debug.Log(report.ToString());
        }
    }
}
