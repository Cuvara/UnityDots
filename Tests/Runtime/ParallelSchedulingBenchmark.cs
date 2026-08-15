using System;
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
    // emitted by the source generator only for call sites inside an ISystem or SystemBase. Called
    // from a plain class they compile against a stub and throw at runtime with
    // "This method should have been replaced by source gen." — which names the mechanism but not the
    // rule, so it is worth stating here.
    //
    // Every pair below schedules the SAME job struct the shipping system uses. Nothing is
    // reimplemented for the benchmark, so what is timed is the schedule and not a lookalike.

    [DisableAutoCreation]
    internal partial struct SpinRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) => new SpinJob { DeltaTime = 0.016f }.Run();
    }

    [DisableAutoCreation]
    internal partial struct SpinParallelSystem : ISystem
    {
        // Completed inside the system only because a benchmark must measure a finished unit of work.
        // The shipping SpinSystem deliberately does NOT complete — it threads state.Dependency out
        // so the job overlaps the rest of the frame.
        public void OnUpdate(ref SystemState state) =>
            new SpinJob { DeltaTime = 0.016f }.ScheduleParallel(state.Dependency).Complete();
    }

    [DisableAutoCreation]
    internal partial struct MoveBounceRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) => new MoveBounceJob { DeltaTime = 0.016f }.Run();
    }

    [DisableAutoCreation]
    internal partial struct MoveBounceParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) =>
            new MoveBounceJob { DeltaTime = 0.016f }.ScheduleParallel(state.Dependency).Complete();
    }

    [DisableAutoCreation]
    internal partial struct MoveTowardRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) => new MoveTowardJob { DeltaTime = 0.016f }.Run();
    }

    [DisableAutoCreation]
    internal partial struct MoveTowardParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state) =>
            new MoveTowardJob { DeltaTime = 0.016f }.ScheduleParallel(state.Dependency).Complete();
    }

    // The two structural jobs need a command buffer. It is created and played back inside the
    // measured region deliberately: recording through a ParallelWriter is part of what the parallel
    // schedule costs, and excluding it would flatter the result.

    [DisableAutoCreation]
    internal partial struct HealthDeathRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            using var buffer = new EntityCommandBuffer(Allocator.TempJob);
            new HealthDeathJob { CommandBuffer = buffer.AsParallelWriter() }.Run();
            buffer.Playback(state.EntityManager);
        }
    }

    [DisableAutoCreation]
    internal partial struct HealthDeathParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            using var buffer = new EntityCommandBuffer(Allocator.TempJob);
            new HealthDeathJob { CommandBuffer = buffer.AsParallelWriter() }
                .ScheduleParallel(state.Dependency).Complete();
            buffer.Playback(state.EntityManager);
        }
    }

    [DisableAutoCreation]
    internal partial struct TimeToLiveRunSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            using var buffer = new EntityCommandBuffer(Allocator.TempJob);
            new TimeToLiveJob { DeltaTime = 0.016f, CommandBuffer = buffer.AsParallelWriter() }.Run();
            buffer.Playback(state.EntityManager);
        }
    }

    [DisableAutoCreation]
    internal partial struct TimeToLiveParallelSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            using var buffer = new EntityCommandBuffer(Allocator.TempJob);
            new TimeToLiveJob { DeltaTime = 0.016f, CommandBuffer = buffer.AsParallelWriter() }
                .ScheduleParallel(state.Dependency).Complete();
            buffer.Playback(state.EntityManager);
        }
    }

    /// <summary>
    /// Measures each parallel schedule against its single-threaded form and reports the entity count
    /// where parallel overtakes serial.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same job, two schedules</b> — <c>Run()</c> versus <c>ScheduleParallel()</c> — not a job
    /// against a hand-written loop. Both execute byte-identical Bursted code over identical chunks,
    /// so what is measured is worker parallelism minus scheduling overhead, and nothing else.
    /// </para>
    /// <para>
    /// <b>The two arms are interleaved and the ratio is a median, because the absolutes are noisy.</b>
    /// An earlier revision timed thirty serial iterations and then thirty parallel ones; on a shared
    /// cloud runner, two runs of that identical code reported 0.88 ms and 80.07 ms for the same case
    /// — 90x apart — because a stall landing inside one arm skews only that arm. Interleaving
    /// A/B/A/B puts any stall in both halves of the same pair, and a median discards the pairs that
    /// were hit. The absolutes remain untrustworthy on a shared runner; the ratio survives.
    /// </para>
    /// <para>
    /// <b>Nothing here asserts a timing.</b> A performance assertion on a shared runner is a flaky
    /// test, and a flaky test inside a gate teaches people to re-run until green. Timings are logged;
    /// the assertions are about correctness.
    /// </para>
    /// </remarks>
    public sealed class ParallelSchedulingBenchmark
    {
        private static readonly int[] EntityCounts = { 64, 256, 1024, 4096, 16384, 65536 };

        private const int Warmup = 10;
        private const int Pairs = 41;

        private World _world;
        private EntityManager _entityManager;

        /// <summary>
        /// Refuses to measure anything unless Burst is actually compiling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A table printed under a <c>burst=False</c> line is a quotable-looking number that is
        /// wrong, and that is the same shape as a gate reporting green over zero tests.</b> It
        /// happened: a 12-core run produced a clean six-row table at 535 ns/entity for a
        /// <c>RotateY</c> — the managed path — with the disabled flag one line above it, and it was
        /// nearly read past. So this skips the test rather than printing anything.
        /// </para>
        /// <para>
        /// <b>Why Burst may be off, from its own source.</b> <c>BurstCompilerOptions</c>'s static
        /// constructor sets <c>ForceDisableBurstCompilation</c> for exactly four reasons: the
        /// <c>--burst-disable-compilation</c> command-line argument; a non-empty
        /// <c>UNITY_BURST_DISABLE_COMPILATION</c> environment variable; <c>ENABLE_CORECLR</c> in the
        /// Editor; and <c>CheckIsSecondaryUnityProcess()</c>, which includes
        /// <c>AssetDatabase.IsAssetImportWorkerProcess()</c>. Separately the Editor's
        /// <i>Jobs &gt; Burst &gt; Enable Compilation</i> menu toggle persists across sessions, and a
        /// batchmode run inherits whatever it was last left at — the likeliest cause when none of
        /// the four applies, and one no command-line flag overrides.
        /// </para>
        /// <para>
        /// So this <b>tries to turn it on</b> before giving up. The setter coerces back to false when
        /// <c>ForceDisableBurstCompilation</c> is set, which is what separates "the toggle was off"
        /// (fixed here) from "this process cannot Burst at all" (not fixable here, and the message
        /// says which).
        /// </para>
        /// <para>
        /// Synchronous compilation is requested too: Burst compiles asynchronously by default, so the
        /// first calls run the managed path and a warmup loop would quietly measure it.
        /// </para>
        /// </remarks>
        private static void RequireBurst()
        {
            if (!BurstCompiler.IsEnabled)
            {
                BurstCompiler.Options.EnableBurstCompilation = true;
            }

            if (!BurstCompiler.IsEnabled)
            {
                Assert.Ignore(
                    "Burst is not compiling, so every timing here would measure the managed path — " +
                    "535 ns/entity for a RotateY rather than ~18. No table is printed, on purpose. " +
                    "Setting BurstCompiler.Options.EnableBurstCompilation did not take, so " +
                    "ForceDisableBurstCompilation is set: check --burst-disable-compilation, a " +
                    "UNITY_BURST_DISABLE_COMPILATION env var, ENABLE_CORECLR, or a secondary Unity " +
                    "process (AssetDatabase.IsAssetImportWorkerProcess). If none apply, the Editor's " +
                    "Jobs > Burst > Enable Compilation toggle is off and persists across sessions.");
            }

            // Async is the default, so the first calls run managed and the warmup would measure that.
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
        }

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.ParallelBenchmark");
            _entityManager = _world.EntityManager;
        }

        [TearDown]
        public void TearDown() => _world.Dispose();

        /// <remarks>
        /// Health and TimeToLive are seeded so that <b>nothing is destroyed</b>: the realistic steady
        /// state is a scan over live entities, and a benchmark that deletes its own working set
        /// measures a shrinking one.
        /// </remarks>
        private void Populate(int count)
        {
            _entityManager.DestroyEntity(_entityManager.UniversalQuery);

            var archetype = _entityManager.CreateArchetype(
                typeof(LocalTransform), typeof(SpinSpeed), typeof(MoveData),
                typeof(MoveToward), typeof(Health), typeof(TimeToLive));

            using var entities = _entityManager.CreateEntity(archetype, count, Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                _entityManager.SetComponentData(entities[i], LocalTransform.FromPosition(i % 512, 0f, 0f));
                _entityManager.SetComponentData(entities[i], new SpinSpeed { RadiansPerSecond = 1f + (i % 7) });
                _entityManager.SetComponentData(entities[i], new MoveData
                {
                    Velocity = new float3(1f, 0f, 0.5f),
                    BoundsMin = new float3(-1000f, -1000f, -1000f),
                    BoundsMax = new float3(1000f, 1000f, 1000f),
                });
                _entityManager.SetComponentData(entities[i], new MoveToward
                {
                    Target = new float3(1000f, 0f, 1000f), Speed = 5f, StopDistance = 0.1f,
                });
                _entityManager.SetComponentData(entities[i], new Health { Current = 100, Max = 100 });
                _entityManager.SetComponentData(entities[i], new TimeToLive { Remaining = 1e9f });
            }
        }

        private void Tick<T>() where T : unmanaged, ISystem =>
            _world.GetExistingSystem<T>().Update(_world.Unmanaged);

        [Test] public void Spin() => Measure<SpinRunSystem, SpinParallelSystem>("SpinJob");
        [Test] public void MoveBounce() => Measure<MoveBounceRunSystem, MoveBounceParallelSystem>("MoveBounceJob");
        // MoveToward's 65,536 row was incoherent before interleaving — ~585 ns/entity for three
        // sizes then a 58x cliff to 10.1. With A/B interleaving and Burst on it reads 73 -> 5.5
        // ns/entity monotonically and the caveat is gone. Recorded because the fix was statistical:
        // nothing about the job changed.
        [Test] public void MoveToward() => Measure<MoveTowardRunSystem, MoveTowardParallelSystem>("MoveTowardJob");
        [Test] public void HealthDeath() => Measure<HealthDeathRunSystem, HealthDeathParallelSystem>("HealthDeathJob");
        [Test] public void TimeToLive() => Measure<TimeToLiveRunSystem, TimeToLiveParallelSystem>("TimeToLiveJob");

        /// <summary>
        /// The determinism check the parallel schedule has to earn: identical input through both
        /// paths must give bit-identical output.
        /// </summary>
        /// <remarks>
        /// Not a formality. A parallel job whose result depends on iteration order is a bug that
        /// reproduces about one run in ten, and these systems produce positions a predictor may later
        /// reconcile against. Bit-identical rather than approximately equal, because "close enough"
        /// is how a drift bug survives its own test.
        /// </para>
        /// <para>
        /// <b>Deliberately not guarded on Burst.</b> Determinism is a property of the schedule, not
        /// of the compiler — so this is exactly the assertion still worth running when Burst is off,
        /// and it is the one that ran and passed on the 12-core machine where every timing was
        /// invalid.
        /// </para>
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

        private void Measure<TRun, TParallel>(string name)
            where TRun : unmanaged, ISystem
            where TParallel : unmanaged, ISystem
        {
            // Before anything is populated or timed: no Burst, no table.
            RequireBurst();

            _world.GetOrCreateSystem<TRun>();
            _world.GetOrCreateSystem<TParallel>();

            var report = new StringBuilder();
            report.AppendLine($"[benchmark] {name} — median of {Pairs} interleaved pairs after {Warmup} warmup");
            report.AppendLine($"[benchmark] processors={SystemInfo.processorCount}  burst={BurstCompiler.IsEnabled}");
            report.AppendLine("[benchmark]   entities |  serial ms | parallel ms | speedup | ns/entity serial");

            var crossover = -1;
            foreach (var count in EntityCounts)
            {
                Populate(count);

                for (var i = 0; i < Warmup; i++) { Tick<TRun>(); Tick<TParallel>(); }

                var serial = new double[Pairs];
                var parallel = new double[Pairs];
                for (var i = 0; i < Pairs; i++)
                {
                    // Interleaved: a stall lands in both arms of the pair rather than in one column.
                    serial[i] = Once(Tick<TRun>);
                    parallel[i] = Once(Tick<TParallel>);
                }

                var s = Median(serial);
                var p = Median(parallel);
                var speedup = p > 0d ? s / p : 0d;
                if (crossover < 0 && speedup > 1d) crossover = count;

                report.AppendLine(
                    $"[benchmark] {count,10} | {s,9:F4} | {p,11:F4} | {speedup,6:F2}x | {s * 1_000_000d / count,8:F1}");
            }

            report.AppendLine(crossover < 0
                ? $"[benchmark] {name} crossover: NONE at these counts on this machine"
                : $"[benchmark] {name} crossover: parallel first wins at {crossover} entities");

            Debug.Log(report.ToString());
        }

        private static double Once(Action action)
        {
            var clock = Stopwatch.StartNew();
            action();
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds;
        }

        private static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }
    }
}
