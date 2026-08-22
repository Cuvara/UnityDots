# Changelog

All notable changes to the Cuvara DOTS package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.24.0] - 2026-08-22

### Remote entities are now interpolated in ECS, by netcode's core rather than by a second copy of it

Until this release the DOTS path had exactly one way to place a replicated entity: whatever position
reached `IEntityView.SetState` was written straight to `LocalTransform`. That was correct and it was
never the whole story — the smoothing was happening one layer up, inside `WorldViewBinder`, and this
package was consuming its output. It worked, and it fixed nothing that netcode's own interpolation
could not fix, but it meant the ECS path could never render a remote entity from anything richer
than a single position, and it meant an ECS client had no way to use the buffered, tick-bracketed,
clock-driven core that netcode 0.18.0 extracted precisely so a Burst job could call it.

It can now. `RemoteInterpolationSystem` is a `[BurstCompile]` `ISystem` scheduling an `IJobEntity`
over `(DynamicBuffer<SnapshotSample>, ref LocalTransform, ref LocalToWorld, ref InterpolationState)`,
and the body of that job is one call to
`Cuvara.Netcode.Interpolation.SnapshotInterpolation.Evaluate` — the identical method the GameObject
path calls, over a `SnapshotSampleBuffer` instead of over a pooled array. **There is no interpolation
arithmetic anywhere in this package.** That is the whole design constraint and not a stylistic one:
the day two implementations disagree, the symptom is a remote avatar drawn in the wrong place with
both copies passing their own tests, and the only defence is that the second copy does not exist.

**What a player gets.** A remote avatar that moves at frame rate along the path the server actually
described, rather than one that holds still for 66 ms and jumps. It is drawn about 100 ms behind the
newest received tick — netcode's `TargetDelay`, one and a half snapshot intervals at the 15 Hz world
rate — and that delay is what buys the smoothness: an early snapshot waits in the buffer instead of
displacing the segment being drawn, a late one is covered by the margin instead of stalling, and a
dropped one interpolates across two ticks' worth of distance in two ticks' worth of time instead of
sprinting and freezing. **The local player pays none of it.** A predicted entity carries
`PredictedTransform`, the job excludes it with `WithNone`, and the one entity whose response delay
the player is holding a key to feel keeps its zero.

**It is opt-in, because the server tick is.** `IEntityView.SetState` carries `(id, x, y, hp, maxHp)`
and no tick, and a sample without a tick cannot be placed on a timeline — the same wall
`ReconciliationAnchor` documents about the anchor's own tick. So the adapter gained its own entry
point, `DotsEntityView.SetStateAtTick(id, x, y, hp, maxHp, tick, receiveTimeSeconds)`, for a caller
that has the tick in its hand: a snapshot handler reading `WorldState.Tick`. Nothing else changes.
A consumer that keeps calling `SetState` gets 0.23.1's behaviour byte for byte.

**And the two paths are mutually exclusive per entity, enforced rather than documented.**
`WorldViewBinder.Tick` interpolates on the netcode side and hands the *result* to `SetState`, so a
view driven by the binder is already receiving a rendered position. Buffering those and evaluating
them again would stack a second `TargetDelay` on top of the first: every remote entity twice as far
behind the server, moving perfectly smoothly, with no error, no exception and nothing in any log to
notice — the failure shape this workspace keeps paying for. The drain therefore decides per state:
a tick means "buffer it, the transform belongs to interpolation", no tick means "write it, the buffer
stays empty and the job passes over this entity". A refused sample — a duplicate or reordered tick,
rejected by netcode's shared `InterpolationRing.Accepts` — deliberately does **not** fall back to a
direct write, because the entity is still owned by interpolation and its superseded state is not
worth rendering.

### Added

- **`ViewInterpolationGroup`**, inside `ViewSystemGroup` and `UpdateBefore(ViewLifecycleGroup)`.
  Presentation, not simulation, and that is the load-bearing choice: interpolation answers "where is
  this drawn on this frame", which is asked once per drawn frame, and `SimulationSystemGroup` would
  tie it to fixed-step semantics it must not have — a rendered position evaluated at 60 Hz fixed
  steps while the client draws at 144 Hz is the stutter this exists to remove, reintroduced one layer
  up. Before the lifecycle group because both of the other view groups *read* the transform:
  `ViewLifecycleGroup` places a newly spawned view from `LocalToWorld` and `ViewTransformSyncGroup`
  copies it onto every live GameObject, so running later would show this frame's views at last
  frame's position — a constant one-frame lag on every remote entity, visible as softness and blamed
  on the render delay. Created empty by `DotsViewBootstrap` even without netcode, like every other
  group here, so a consumer's `[UpdateAfter]` resolves today and does not change meaning later.
  Because `LocalTransform` is now written in presentation, after `TransformSystemGroup` has already
  finished for the frame, the job composes `LocalToWorld` itself — exactly as `ApplySpawn` and
  `ApplyState` already do, for exactly the same reason.
- **`SnapshotSample`**, an `[InternalBufferCapacity(8)]` `IBufferElementData` wrapping netcode's
  `InterpolationSample`, and **`InterpolationState`**, recording what was last drawn and at which
  render tick. Both are added in `ApplySpawn` alongside `ReconciliationAnchor`, for the reason that
  component already states: a component set that changed on the first state would change archetype at
  snapshot rate, and every query over mirrors would then iterate two chunk sets. The capacity is 8
  because 8 x 24 B = 192 B is what keeps the buffer inline in the chunk; a ninth sample moves the
  whole thing to a per-entity heap allocation that area-of-interest churn would repeat. It is the
  same 8 that `InterpolationConfig.RingCapacity` defaults to, and that constant's own documentation
  names this attribute.
- **`InterpolationSettings` and `InterpolationTimeline`**, two blittable singletons: netcode's
  `InterpolationConfig` plus the `SnapshotSpaceMapping`, and the world's `InterpolationClock`.
  Components rather than a `ScriptableObject` and rather than a managed reference, because the
  consumer is a Bursted job and a Bursted job cannot follow either. The mapping is seeded from the
  view's own, so the space the samples were produced in is the space they are rendered in — samples
  are stored in server coordinates verbatim, exactly as `ReconciliationAnchor.ServerPosition` is, and
  projected once after evaluation. Seed the tuning with
  `DotsNetcodeBootstrap.Install(world, view, interpolation)`; `default` means netcode's defaults,
  every non-positive field filled in by `Normalized()`.
- **`InterpolationClockSystem`**, advancing the render clock once per frame from
  `SystemAPI.Time.DeltaTime` — a system of its own rather than the first lines of the evaluation,
  so that the frame's advance has one owner no matter how many things come to read the timeline.
  Ordered by an explicit `[UpdateBefore]` rather than `OrderFirst`, because Entities sorts
  `OrderFirst` members into a separate batch and then drops ordering relations between that batch and
  ordinary members, with a warning — the trap `MovementSystemGroup` already documents. The timeline
  has two writers at two declared points in the frame and that is deliberate: the drain calls
  `NoteSnapshot` on arrival in initialization, this calls `Advance` once per frame in presentation,
  and an arrival is not a frame, so they cannot be collapsed into one.
- **Zero per-frame allocation on the whole path.** The samples live in chunk memory, the singletons
  are copied by value into job fields, the query is `ScheduleParallel` with no `ToEntityArray`, no
  `Complete()` and no main-thread walk, and the `ISampleBuffer` wrapper is a struct passed to a
  `where TBuffer : struct` generic so the indexer calls are constrained calls Burst specialises
  rather than interface dispatch that boxes. The only copy is the front-shift when a full buffer
  admits a new sample — at most seven 24-byte elements, inside the chunk, on snapshot arrivals and
  never on the frame path.
- **Eight tests in `Tests/Editor.Netcode/RemoteInterpolationTests.cs`**, driven through the public
  groups and with `SystemAPI.Time` stamped explicitly, because a world updated group by group never
  advances it and every assertion about motion would otherwise be an assertion about nothing: a
  ticked state is buffered and drawn from the buffer rather than at the newest state; the rendered
  position never steps backwards across twenty frames and never runs past what the server sent; a
  predicted entity is not interpolated *and* keeps accumulating samples so that releasing the tag
  hands interpolation a history rather than a cold buffer; an unticked state is still written
  straight to the transform; an empty buffer and a single-sample buffer are both legal and neither
  throws; a duplicate tick is refused without falling back to a direct write; the clock does not
  advance before the first snapshot. `NetcodeSystemLayoutTests` gains three more asserting the group
  containment, the `UpdateBefore` relations in both directions, and that both new systems are
  internal and not auto-created. `ViewSystemGroupLayoutTests`' hand-maintained group roster gains
  `ViewInterpolationGroup` — that list is what the "no package group is auto-created" and "every
  group is public" checks iterate, so a group missing from it is a group nothing checks.

### Changed

- **The `versionDefines` floor for `CUVARA_NETCODE` moves `0.4.0` -> `0.19.0`**, in
  `Cuvara.DOTS.Netcode`, `Cuvara.DOTS.Netcode.Prediction`, both of their test assemblies and the
  `NetworkedPrediction` sample. It had to. `Cuvara.Netcode.Interpolation` does not exist before
  netcode 0.18.0, and 0.19.0 is the version this package now pins and builds against. **Left at
  0.4.0 the define would still be set against a netcode with no such namespace**, and the adapter
  would fail to compile with a missing-type error naming `SnapshotInterpolation` — a message that
  looks like a typo and says nothing about versions, in an assembly whose whole purpose is to be
  *absent* rather than broken when its dependency is too old. The prediction assemblies move with it
  rather than staying at `0.15.0` for a reason that is not tidiness: they reference
  `Cuvara.DOTS.Netcode`, so a project on netcode 0.16 would set their define and not this one,
  leaving them referencing an assembly that did not compile.
- **CI's netcode pin moves `v0.16.1` -> `v0.19.0`**, the coupling that file's own header warns about:
  the pin and what depends on it move together. Nothing in this change compiles against the old pin.
- `DotsNetcodeBootstrap.Install` gained an optional `InterpolationConfig` parameter and now creates
  the presentation half of the adapter as well as the initialization half. Existing two-argument call
  sites compile and behave unchanged.

## [0.23.1] - 2026-08-20

### Fixed
- **Two folder `.meta` files were truncated and had been repaired only in the client's vendored
  copy.** `Runtime.Netcode.Prediction.meta` and `Tests/Editor.Prediction.meta` carried a `guid` and
  then stopped — no trailing newline, no `folderAsset: yes`, no `DefaultImporter` block — which is
  the shape of a meta written by hand, not one Unity generated. Every sibling folder meta in the
  package has the full body.

  Unity tolerates this: it fills the missing fields in on import, which is why nothing ever failed
  and why the defect survived to be noticed only when the two copies were compared byte for byte.
  The repaired versions have been sitting in `IndieRPGMMOAdventure`'s vendored copy — same GUIDs,
  full body — since the package was first vendored, and never came back upstream.

  The GUIDs are unchanged, so no asset reference moves. What this buys is not a behaviour fix but a
  comparison that means something: with these repaired, upstream and the vendored copy are
  byte-identical, and the client's drift check can assert plain equality instead of carrying an
  allowlist entry for a difference nobody could explain.

## [0.23.0] - 2026-08-20

### Fixed
- **`LocalPredictionSystem` never called `SeedBaseTick`, so netcode's #13 fix was inert on the DOTS
  path — the only path the DOTS sample actually runs.** netcode v0.16.0 added
  `LocalMovePredictor.SeedBaseTick(long serverTick)` to align the predictor's local tick counter
  with the server's world tick, and wired its own `WorldViewBinder`. Its CHANGELOG states plainly
  that a consumer binding views itself — a DOTS system reading `LocalMovePredictor.Position` into
  `LocalTransform`, which is precisely this system — **must call it, or the feature is inert and
  you keep the defect**. This system was that consumer and was not calling it.
  Without the seed, `_baseTick` starts at zero while the server's tick is wherever that server
  happens to be, so the two counters never share an epoch and every held-movement decision is made
  against a phase that is wrong by a constant. Seeded first and only once per reconcile;
  `SeedBaseTick` is idempotent by contract.

### Changed
- **CI's netcode pin moves `v0.15.1` → `v0.16.1`.** It had to: `SeedBaseTick` does not exist before
  v0.16.0, so the fix above would not compile against the old pin. This is the coupling the CI
  header already warns about — the pin and what depends on it move together.

## [0.22.0] - 2026-08-15

### The 15 Hz-render defect: the DOTS path was already correct, and now proves it

netcode's `WorldViewBinder` advanced prediction and updated the view **inside the snapshot loop**, so
the avatar moved only when a snapshot arrived — at the world rate, however fast the client drew. Five
releases of render smoothing were computed and discarded.

**`LocalPredictionSystem` never had that shape.** It lives in `PredictionSystemGroup` →
`NetcodeSystemGroup` → `InitializationSystemGroup`, so it runs **every frame**, and both
`predictor.Advance(SystemAPI.Time.DeltaTime)` and the `LocalTransform`/`LocalToWorld` writes sit
**outside** the `if (ackTick > _lastAckTick)` block that gates reconciliation.

That was true by construction and untested, which is not the same as verified. Three tests now cover
it:

- **`PredictedTransform_IsRewrittenEveryFrame_EvenWithNoSnapshot`** — clobbers the transform with a
  sentinel, runs one frame delivering **no** snapshot, asserts the sentinel is gone. This is the
  defect one layer along: advancing every frame is still invisible if the *write* is snapshot-driven.
- **`PredictedPosition_AdvancesBetweenSnapshots`** — one snapshot to establish a baseline, then held
  input and 30 frames with **no further snapshot**, asserting the position moved.

  The first version of this test delivered no snapshot at all and **failed**: advancing from a cold
  start moves nothing, because the predictor has no baseline to extrapolate from until a reconcile
  has happened. That was the test asserting behaviour the predictor does not have, not a defect —
  and the fixture was corrected rather than the assertion weakened, because runtime always has a
  snapshot first: the entity only exists because one arrived.
- **`ZeroDeltaTime_DoesNotAdvance_SoTheTestsAboveMeasureSomething`** — guards the harness. `Advance(0)`
  early-returns, so a future change dropping the clock would make the other two pass vacuously.

### The frame loop is now modelled, which is why these tests can exist

`Tick` takes a `deltaTime` and pushes it into the world via `SetTime`. **A bare `World` has no player
loop**, so `SystemAPI.Time.DeltaTime` is zero and `Advance(0)` early-returns — every frame-rate
assertion would have been vacuous.

That is the same root cause as netcode's blind spot: its `WorldViewBinderTests` drove the binder
entirely by feeding snapshots, so a position that moved *only* on snapshots was indistinguishable from
a correct one. **The fixture could not express the failure** — the same shape as a CI gate that cannot
go red, and as a smoothing fixture using one constant for two rates. Every existing test in this file
drove the system by delivering state; none could have caught this before `Tick` took a time step.

### Changed

- CI installs `com.cuvara.netcode` **v0.15.1**, and the three `versionDefines` minimums move to 0.15.0
  (the version whose API they need; 0.15.1 is a packaging fix and adds no API).

  **0.15.0 could not be used**: it shipped `Tests/Editor/HeldMovementParityTests.cs` with no `.meta`,
  Unity logged an Error, and the test framework turned it into an `UnhandledLogMessageException` that
  failed the whole run — with **137/137 EditMode and 29/29 PlayMode passing and not one failing
  test**. Two defects stacked: the parity test never ran anywhere, and the Error failed every
  consumer's suite. Fixed upstream in 0.15.1, and the meta gate this package already runs is now
  ported to netcode so it cannot recur silently.
  The adapter and its tests stay at `0.4.0`: they call nothing newer.
- CI's `com.rpgmmo.shared-gamelogic` pin moves to **`sgl-v0.1.8`**. netcode 0.15.0 does not compile
  against 0.1.7 — `GameConstants.MaxBankedMovementTicks` does not exist there — which is the **second
  time** a netcode bump has silently required an sgl bump, and the second time the failure surfaced as
  a `CS0117` inside *netcode's own source* rather than in this package. The CI header already says the
  pins are part of the configuration under test and must move together; this is what it looks like
  when they do not.

  Incidentally this is direct evidence the multi-rate work is real and landed in the shared library:
  `MaxBankedMovementTicks` is a 60 Hz-input concept that did not exist at 0.1.7.

### Unverified

**Whether the user's stutter is gone is a measurement, not a claim made here.** Burstiness that reads
the same with prediction on and off is not measuring prediction; after the fix, prediction-on should
fall well below prediction-off. These tests prove the DOTS path advances and writes per frame. They do
not prove the frame *looks* smooth, and this package still has no path that has run against a live
server.

The tick-rate divergence reported in 0.21.0 is untouched and still upstream: netcode v0.15.0's
`JoinTokenResponse` carries no `TickRate` and `LocalMovePredictor` has no setter for it.

## [0.21.0] - 2026-08-15

### Per-system thresholds, from real numbers

12 cores, **Burst on**, median of 41 interleaved pairs:

| job | crossover | speedup @ 65,536 |
|---|---|---|
| `SpinJob` | 4,096 | 4.03× |
| `MoveBounceJob` | 16,384 | 2.45× |
| `MoveTowardJob` | 16,384 | 3.28× |
| `HealthDeathJob` | 65,536 | 1.16× |
| `TimeToLiveJob` | 65,536 | 1.24× |

`ParallelScheduling.MinimumEntities` is replaced by five named constants, each that job's own measured
crossover. 0.19.0's single constant was honest for the data that existed — a floor against a measured
pessimisation — but the jobs are different in kind: `SpinJob` writes one component and scales nearly
4×, while `HealthDeathJob` and `TimeToLiveJob` reach 1.16× and 1.24× even at 65,536, where the
per-entity work is one comparison and the command buffer dominates. The shared 16,384 would have
scheduled those two at counts where they measured 0.45×–0.88×.

They keep a threshold rather than being forced serial: a consumer running hundreds of thousands of
entities should get the win, and this package's own consumer simply never reaches it.

### Why the earlier crossovers were wrong, and predictably so

A 12-core run with `BurstCompiler.IsEnabled == false` reported crossovers of 256 and 1,024. Those
were **refused rather than adopted**, on the argument that Burst speeds the serial arm far more than
it speeds scheduling overhead, so the true crossover had to be *higher*. Measurement confirmed it:
serial `ns/entity` fell from ~535 to 1–13 — roughly two orders of magnitude — and the crossovers rose
to 4,096–65,536.

Adopting 256 would have scheduled every job from 256 entities upward while measuring 0.4×–0.7× in
exactly the AOI-bounded range this package operates in: every frame made worse to win a case nobody
reaches.

### Fixed

`MoveTowardJob`'s benchmark row is credible again — 73 → 5.5 ns/entity monotonically, instead of
~585 for three sizes and then a 58× cliff. **Nothing about the job changed**; the fix was statistical,
which is worth recording because the row looked like a workload bug and was a measurement bug.

### Unverified — and a live risk from outside this package

**The backend now runs multi-rate: critical 60 Hz, world 15 Hz**, with `tick_rate` on
`JoinTokenResponse`. This package cannot consume it, and the chain is broken in two places upstream:

- `com.cuvara.netcode` v0.10.4's `JoinTokenResponse` has **no `TickRate` field**, so the value is
  dropped at parse.
- `LocalMovePredictor` has **no tick-rate setter** — only `SetServerSpeed`. `PredictionSettings.TickRate`
  is fixed at construction.

So `Samples~/NetworkedPrediction` constructs the predictor with `GameConstants.DefaultTickRate`, which
is **15**. If inputs are now drained and integrated at 60 Hz server-side, replay uses a `dt` four
times too large and every reconcile overshoots — `PredictionSettings` itself says a tick-rate mismatch
"scales every predicted step by the ratio". **This is the speed bug again, with a larger multiplier
and no way to fix it from this package.** The moment netcode surfaces the wire value and adds a setter,
`LocalPredictionSystem` feeds it in one branch beside the existing `SetServerSpeed` call.

## [0.20.0] - 2026-08-15

### The benchmark refuses to print a table when Burst is off

A 12-core run on the target machine produced a clean six-row table — and `BurstCompiler.IsEnabled:
False` one line above it. 535 ns/entity for a `RotateY` is the managed path; the `ns/entity` column
caught it, and the table was still nearly read as real. **A quotable-looking table under a
`burst=False` line is the same shape as a gate reporting green over zero tests**, so the guard now
skips the test and prints nothing instead.

It **tries to fix the condition before giving up**: `BurstCompiler.Options.EnableBurstCompilation =
true`, then re-checks. The setter coerces back to false when `ForceDisableBurstCompilation` is set,
which is exactly what separates the two cases, and the skip message says which one you are in.

### Why Burst was off, read out of Burst's own source

`BurstCompilerOptions`' static constructor sets `ForceDisableBurstCompilation` for **four** reasons
and no others:

| Cause | Overridable from script? |
|---|---|
| `--burst-disable-compilation` command-line argument | no |
| non-empty `UNITY_BURST_DISABLE_COMPILATION` env var | no |
| `ENABLE_CORECLR` in the Editor | no |
| `CheckIsSecondaryUnityProcess()` — includes `AssetDatabase.IsAssetImportWorkerProcess()` | no |

None of those was present on the machine that ran it. The remaining cause is the Editor's own
**Jobs > Burst > Enable Compilation** menu toggle, which is per-machine, **persists across sessions,
and has no command-line override** — a batchmode run silently inherits whatever it was last left at.

**So the honest position: this measurement needs the toggle on, and that is a human action in the
Editor.** The guard now makes a run with it off produce a skip and an explanation rather than a
table, so the next person does not rediscover this by nearly quoting a wrong number.

Synchronous compilation is also requested (`EnableBurstCompileSynchronously`), because Burst compiles
asynchronously by default and the warmup loop would otherwise measure the managed path on the way in.

### Not guarded, deliberately

`BothSchedules_ProduceBitIdenticalResults` runs regardless. Determinism is a property of the schedule,
not of the compiler — it is the assertion still worth having when Burst is off, and it is the one that
ran and passed on the machine where every timing was invalid.

### The numbers that are sound, and the ones that are not

From the 12-core run: **the speedup ratios are valid** — both arms ran under identical conditions, so
`SpinJob` at 5.98× and `MoveBounceJob` at 6.47× at 65,536 are real parallel wins, plateauing near 6 on
12 cores as memory bandwidth and scheduling begin to bind.

**The crossovers from that run are not, and must not go into docs**: 256 and 1,024 entities were
measured on the managed path, where the serial arm is ~30× slower than it will be with Burst on. Burst
speeds the serial arm far more than it speeds scheduling overhead, so **the real crossover is
substantially higher**. `ParallelScheduling.MinimumEntities` stays at 16,384 — still the CI figure, and
still explicitly provisional — rather than being lowered to a number measured without the compiler.

## [0.19.0] - 2026-08-15

**The measurement contradicted the change, so the change moved.** 0.17.0 scheduled five simulation
jobs with `ScheduleParallel` unconditionally. Measured on 4 cores, median of 41 interleaved pairs:

```
                   64      256     1024     4096    16384    65536
  SpinJob        0.40x    0.41x    0.54x    0.90x    1.63x    1.69x
  MoveBounceJob  0.73x    0.79x    0.91x    0.41x    0.98x    0.88x
  HealthDeathJob 0.67x    0.58x    0.73x    0.88x    0.45x    0.96x
  TimeToLiveJob  0.60x    0.46x    0.39x    0.55x    0.59x    0.88x
```

**Below a few thousand entities, every job is slower scheduled than run**, and three of the four
never overtook their serial form at any count tested. Scheduling overhead is fixed; the work is not.

That is not academic here: this package's entity count is bounded by the server's area of interest —
tens to low hundreds — so shipping unconditional `ScheduleParallel` would have made the common case
worse in exchange for a win nobody in this project reaches. It is the spatial-index mistake with
different ceremony.

### Changed — the schedule is chosen from the measurement

Every simulation system is still an `IJobEntity`. Each now picks its schedule per update:

```csharp
state.Dependency = _query.CalculateEntityCount() >= ParallelScheduling.MinimumEntities
    ? job.ScheduleParallel(state.Dependency)
    : job.Schedule(state.Dependency);
```

`ParallelScheduling.MinimumEntities` is **16,384** — `SpinJob`'s measured crossover, and explicitly
nothing more. The other three have unknown, certainly higher thresholds; one constant is a
deliberate simplification documented as a floor against the measured pessimisation, not a per-system
tuning. The crossover moves with core count, so this is a compile-time approximation of a runtime
property, chosen conservatively: serial slightly past the true crossover costs a little throughput,
parallel below it costs on every frame at the counts this package actually runs.

### One benchmark row is not credible, and says so

`MoveTowardJob` reports ~585 ns/entity at 1,024, 4,096 and 16,384 and then **10.1** at 65,536 — a
58× drop no scheduling effect produces. That row is left in place with a comment rather than deleted:
a visibly broken measurement is more useful than a missing one, and the job's schedule is driven by
the shared threshold rather than by that number. It needs re-measuring on real hardware before
anyone trusts a `MoveToward` figure.

`SpinJob`, `HealthDeathJob` and `MoveBounceJob` all show flat ns/entity across the top three sizes,
which is the internal consistency check that makes their rows usable.

### Unverified

The threshold is one machine's number, on four cores, from a shared runner. The **ratio** is credible
after interleaving; the absolute crossover is not portable. A filtered PlayMode run on the target
machine would give a real value, and is the one thing that would justify changing the constant.

## [0.18.0] - 2026-08-15

Completes the parallelism bar: every core system now either runs as a parallel job or carries a
written, evidenced reason it does not, and the measurement is trustworthy enough to quote a ratio.

### Corrected — a cause I asserted without checking

0.17.0 blamed the 90x run-to-run timing variance on "three Unity containers sharing one host". That
is wrong: **each GitHub Actions job runs on its own runner VM.** The variance is ordinary
shared-cloud noise, and the fix is statistical rather than structural. Recording the correction
because the original claim was exactly the kind of confident wrong diagnosis this package keeps
paying for.

### The benchmark can now be trusted for a ratio

Timing all serial iterations and then all parallel ones lets a stall skew one column — which is how
the same case reported 0.88 ms in one run and 80.07 ms in the next. Now:

- **arms interleaved A/B/A/B**, so a stall lands in both halves of a pair;
- **median of 41 pairs**, which discards the pairs that were hit;
- **all five parallelised jobs measured**, not two standing in for five;
- the structural pair creates and plays back its command buffer **inside** the measured region,
  because recording through a `ParallelWriter` is part of what the parallel schedule costs and
  excluding it would flatter the result;
- `Health` and `TimeToLive` seeded so nothing is destroyed — the realistic steady state is a scan
  over live entities, and a benchmark that deletes its own working set measures a shrinking one.

Absolute timings on a shared runner are still not quotable. The **ratio and the crossover** are.

### The complete system inventory, which is the actual pass criterion

| System | Schedule | Why |
|---|---|---|
| `SpinSystem` | `ScheduleParallel` | |
| `MoveBounceSystem` | `ScheduleParallel` | |
| `MoveTowardSystem` | `ScheduleParallel` | |
| `HealthDeathSystem` | `ScheduleParallel` + `ParallelWriter` | |
| `TimeToLiveSystem` | `ScheduleParallel` + `ParallelWriter` | |
| `EntityViewTransformSyncSystem` | **already parallel** | Bursted `IJobEntity` collects blittable samples; a flat main-thread loop applies them, because `UnityEngine.Transform` is main-thread-only |
| `EntityViewSpawnSystem` | main thread | `EntityViewRegistry.Spawn` instantiates a pooled `GameObject`; Unity's object API is main-thread-only by contract, not by convention |
| `EntityViewDespawnSystem` | main thread | same managed pool call, plus a structural removal |
| `NetworkViewCommandSystem` | main thread | the drain is ordered by definition, and two `SetState`s for one id can share a drain where "last wins" — splitting races two workers on one component, and de-duplicating first is a serial pass over the same data |
| `LocalPredictionSystem` | main thread, **must stay** | one predictor with an order-dependent input ring buffer; `Reconcile` replays the whole backlog in sequence. Parallelising yields a plausible wrong position rather than a crash, for nothing, since exactly one entity is predicted |

Five parallel, one already parallel, four with reasons. No entry is "did not get to it".

### The hybrid half, audited rather than assumed

- **Zero `MonoBehaviour` in shipping code.** `grep -rln ": MonoBehaviour" Runtime*` returns nothing;
  the only ones in the repository are in `Samples~`, which is presentation by definition.
- **No simulation state outside ECS.** `EntityViewRegistry` is a plain `sealed class` holding an
  id→`GameObject` map — a presentation-side lookup, not simulation state.
- **The seam is one-directional**: entities carry `EntityViewLink` (an `int` handle, blittable,
  readable from a job); nothing reads a `GameObject` back into simulation. The handle exists
  precisely so the component stays unmanaged.

### Unverified

**The crossover numbers still come from a shared runner.** The interleaved median makes the ratio
credible; it does not make the absolutes real, and the crossover point moves with core count. One
filtered PlayMode run on the target machine settles it — that remains the only way to get figures
worth acting on.

## [0.17.0] - 2026-08-15

### The core is actually multithreaded now

Every pure simulation system was `[BurstCompile]` `ISystem` with a `SystemAPI.Query` loop —
**optimised machine code on exactly one thread**. There was not a single `IJobEntity` or
`ScheduleParallel` in the package. The foundation was right; the parallelism was never built on it.

Five systems converted to `IJobEntity` scheduled with `ScheduleParallel`:

| System | Job | Structural |
|---|---|---|
| `SpinSystem` | `SpinJob` | no |
| `MoveBounceSystem` | `MoveBounceJob` | no |
| `MoveTowardSystem` | `MoveTowardJob` | no |
| `HealthDeathSystem` | `HealthDeathJob` | `EntityCommandBuffer.ParallelWriter` |
| `TimeToLiveSystem` | `TimeToLiveJob` | `EntityCommandBuffer.ParallelWriter` |

`state.Dependency` is threaded in and out rather than completed inside each system: completing there
would serialise the job against every other system in the frame and throw away most of the benefit.

**The sort key is `[ChunkIndexInQuery]`, and that is a determinism decision, not a style one.**
Command-buffer playback replays in sort-key order. Worker threads finish in whatever order the
scheduler gives them, so without a stable key the playback order — and the outcome — would vary run
to run on identical input.

### Two systems deliberately left single-threaded

Examined, not skipped, and the reasoning is in the code where the next person will look.

**`NetworkViewCommandSystem`** — the queue drain is ordered by definition (spawn precedes its first
state, despawn follows its last), so a parallel drain would have to rebuild the FIFO with a sequence
number. Worse, two `SetState`s for one id can arrive in a single drain and the correct result is
"last wins" — splitting the apply would race two workers on one component, and de-duplicating first
is a serial pass over the same data the serial apply already walks. The work is AOI-bounded anyway:
tens of commands per frame, not thousands.

**`LocalPredictionSystem`** — one predictor instance owns an input ring buffer, and `RecordInput`,
`Reconcile`, `Advance` are order-dependent against it; `Reconcile` replays the whole unacknowledged
backlog in sequence. Parallelising would not crash, it would produce a plausible wrong position —
the failure shape this project has paid for most — in exchange for nothing, since exactly one entity
is ever predicted.

### Measured, not asserted

`ParallelSchedulingBenchmark` (PlayMode) times **the same job two ways** — `Run()` versus
`ScheduleParallel().Complete()` — across 64 → 65,536 entities and prints a table with the crossover
point. Same Bursted code over the same chunks both ways, so what is measured is worker parallelism
minus scheduling overhead, and nothing else. Comparing a job against a hand-written loop would fold
in codegen differences and measure the wrong thing.

**No timing is asserted.** A performance assertion on a shared CI runner is a flaky test, and a flaky
test inside a gate is worse than no measurement — it teaches people to re-run until green. The
numbers are logged for reading; the assertions are about correctness.

`BothSchedules_ProduceBitIdenticalResults` is the one that had to be earned: identical input through
both paths, **bit-identical** output, eight steps of integration. A parallel job whose result depends
on iteration order is a bug that reproduces about one run in ten, and these systems produce positions
a predictor may later reconcile against. "Approximately equal" is how a drift bug survives its own
test.

### Measured — and the measurement environment failed, which is itself the result

**CI cannot measure this, and the benchmark proved it rather than papering over it.** Two runs of
identical code, same commit, gave for the same 65,536-entity `SpinJob` case:

```
run 1:  Run() 0.88 ms   Parallel 0.57 ms    (13 ns/entity)
run 2:  Run() 80.07 ms  Parallel 72.60 ms   (1221 ns/entity)
```

**90× apart.** The cause is in this workflow: it runs three Unity jobs concurrently, each requesting
four CPUs from one host, so a benchmark measures contention as much as parallelism. `ns/entity` is
the tell — a trivial `RotateY` costing a microsecond per entity is not measuring compute.

So **no speedup figure from CI is quotable**, and none is quoted. What survives:

- **The shape is consistent across both runs**: parallel loses at small counts and wins at large
  ones, which is the crossover behaviour the design predicts. Run 1 put `SpinJob`'s crossover at
  4,096 entities.
- **`BurstCompiler.IsEnabled: True`** in the run, and Burst exposes no per-job "was this compiled"
  query — so `ns/entity` is the only available cross-check, and it is reported for exactly that.
- **The correctness assertion passed in every run**, and it is machine-independent:
  `BothSchedules_ProduceBitIdenticalResults`, eight integration steps, bit-identical output.

The benchmark now prints a warning saying its own numbers are not quotable from CI. **Running it on
the real machine is one filtered PlayMode run** and is the only way to get figures worth acting on.

### Unverified

Everything about performance. The conversion is correct — determinism asserted, both structural
systems recording through a parallel writer with a stable sort key — but **whether it is faster on
the target hardware is unmeasured**, and the CI numbers are evidence about the runner rather than
about the code.

## [0.16.1] - 2026-08-15

Two corrections that missed the 0.16.0 merge by minutes — the branch was merged while this commit
was still in flight, which orphaned it from CI and from the release. Content unchanged from what
0.16.0 describes; this is the version that actually carries it.

## [0.16.0] - 2026-08-15

### Fixed — the prediction driver now feeds the server's speed

`LocalPredictionSystem` reads the local entity's speed from `WorldState` and calls
`LocalMovePredictor.SetServerSpeed` immediately before each `Reconcile`.

**Nothing failed without this, which is the point.** `PredictionSettings.Speed` is fixed at
construction, and a client integrating at a different rate from the server desyncs every tick with no
error on either side. By eye it is indistinguishable from a badly tuned predictor, so the debugging
goes to the wrong place. The wire has carried per-entity speed since netcode 0.8.0; until now the
DOTS path ignored it and ran on whatever literal the consumer constructed with.

**Why it belongs in the driver rather than in the sample or on the anchor:**

- Not on `ReconciliationAnchor` — the anchor is written from `IEntityView.SetState`, which carries no
  speed. The wire value exists only on `WorldState`.
- Not left to the consumer — `WorldViewBinder` feeds it, but **only in its predictor overload**, which
  netcode's own docs tell the DOTS path not to use: *"hand the predictor to that system instead"*.
  That leaves this system as the only thing that can.
- Speed is set **before** position, matching the binder. `Reconcile` replays every unacknowledged
  input, so replaying at a stale speed integrates the whole backlog at the wrong rate.

A non-positive wire value means "not sent" and is ignored inside `SetServerSpeed`, so a server that
never populates it leaves the constructed fallback standing rather than collapsing speed to zero.

### Changed

- The sample's `moveSpeed` is now `fallbackMoveSpeed` — used before the first snapshot and nothing
  more. It no longer has to match the server, which removes a literal that was **only correct until
  someone changed a server constant, and would then have failed silently**.
- The overlay shows `speed`, so a mismatch is visible rather than inferred.
- CI's netcode row installs **v0.9.1** (was 0.6.2). The gate was sound — the `versionDefines`
  expression is `>=` — but the row was validating against an older netcode than the project runs.

### Fixed — three `versionDefines` minimums that were understated

Found by auditing every pin after the stale `sgl` one, rather than by anything failing.

`Cuvara.DOTS.Netcode.Prediction`, its tests and the sample declared `com.cuvara.netcode >= 0.6.0`
(the sample, `0.6.2`) — while now calling `SetServerSpeed` and `EffectiveSpeed`, **which arrived in
0.8.0**. On netcode 0.6.x the define would fire, the assemblies would compile, and they would fail
with `CS1061`. That is **broken, not absent** — the exact inversion of the property those constraints
exist to guarantee, introduced by this release's own fix. All three now say `0.8.0`.

The adapter and its tests stay at `0.4.0`: they call nothing newer, and a minimum should be the
version an assembly actually needs rather than the newest one available.

### Fixed — the all-zero diagnostic

When **no** result XML is produced, every floor reported `actual 0`, which reads exactly like every
assembly vanishing. The real cause is almost always one compile error: a single test assembly that
fails to build collapses the whole EditMode run. The 0.13.0 entry predicted this trap and it then
cost someone twenty minutes for real, so the script now says so explicitly before printing any floor,
and points at version pins as well as `defineConstraints` — a package pinned behind what another
package requires fails inside *that package's* source, not in ours.

### Tests

16 in `Tests/Editor.Prediction/` (was 14). Two new, and they exist because nothing else can fail when
this is wrong:

- the wire's speed reaching the predictor and **winning over** the constructed value;
- a zero wire speed leaving the fallback in place rather than freezing prediction while every counter
  still looks healthy.

### Measured

Prediction removes **~72 ms** of input-to-visible on a live local server — median 0.1 ms on, 72.0 ms
off, 20 samples per configuration. Not keypress-to-visible: keyboard, OS input stack and display sit
outside the engine, but those legs are identical in both runs, so the difference is sound.

That measurement was taken with netcode's own harness, not this sample. **This package's sample still
has not been run against a live server.**

## [0.15.0] - 2026-08-14

### Added

- **`Samples~/NetworkedPrediction`** — the first thing that drives `DotsEntityView` and the
  prediction driver **against a real server**. Their unit tests assert wiring with hand-built
  snapshots; nothing until now proved that a server's actual entity types resolve, that its
  coordinates arrive intact, or that exactly one thing writes `LocalTransform` under real traffic.

  No prefabs, no Addressables, no DI: `PrimitiveViewProvider` pools Unity primitives so the sample
  drops into an empty scene, and it pools for real so the recycle path is exercised rather than
  hidden behind Instantiate/Destroy. The catalog is built in code — the same data a project would
  author as assets, typed.

  Tick rate and map bounds come from `GameConstants`, not from literals in the sample. A literal copy
  compiles, passes, and then disagrees with the server the moment the shared package moves — the trap
  `SimConstants` was written to avoid, and a sample is not exempt from it.

  **Input is sampled and sent by the sample, not by the driver.** The tick recorded must be the tick
  that went to the server; a driver inventing its own input builds a buffer the server never saw.

  The overlay is the deliverable, not decoration. `writer:` reads `predictor` or `adapter` for the
  local entity, and it must never be ambiguous: both writing is the failure `PredictedTransform`
  exists to prevent, and neither writing is a frozen avatar. Toggling **Prediction Enabled** flips it,
  which is the A/B this sample is for.

### Changed — an API gap the sample exposed

`ViewConfig.Configure` and `ViewArchetypeLibrary.Configure` are **public**. Both docstrings already
said they were "for tests and for code that generates configs" while being `internal`, so no consumer
could ever be the second kind of caller. Building this sample is what surfaced the contradiction: a
package whose whole premise is spawning from **server snapshots at runtime** must let a consumer
assemble a catalog without authored assets, and the fields are `[SerializeField] private`, so there is
no other route outside the Editor.

Additive — nothing that compiled before stops compiling. It is also the second time a sample has paid
for itself before running: the first was catching that `Samples~` compiles nowhere at all.

### Samples are now compiled by CI

**`Samples~/` is excluded from Unity import, so a sample compiles nowhere by default.**
`com.cuvara.netcode`'s DOTS sample is in exactly that state: 185 tests green while
`DOTSNetworkBridge.cs` is read and never built. A sample nothing compiles is a sample that rots, and
this one would have rotted the same way.

Every Unity job now copies `Samples~/.` into `Assets/` before running, and asserts per configuration:

| | netcode absent | netcode 0.6.2 | no optional packages |
|---|---|---|---|
| `Samples.HybridViews.dll` | present | present | present |
| `Samples.NetworkedPrediction.dll` | **absent** | **present** | **absent** |

`HybridViews` needs no optional package and must always build. `NetworkedPrediction` is gated on both
defines, so its absence in two of three rows is itself an assertion — the same falsifiable shape as
the assembly rows.

### Known, deferred

The `.meta` generator emits guid-only stubs. Unity rewrites them on import with its defaults, which is
harmless for folders, `.cs` and `.md`, and is what those types want. **It is not harmless in general**:
a stub meta does not mean "no settings", it means "every default" — netcode's `Google.Protobuf.dll`
shipped a stub, imported with `validateReferences: 1`, and that refused the plugin, poisoned
`Cuvara.Netcode.Runtime` and produced `0/0 Passed` under a green check. This package ships no binary
assets today. If it ever does, the generator must emit a real importer block for that type and
**fail** on a type it does not know how to write, rather than emitting a stub.

### Not proven

**The sample has not been run.** No Unity Editor was available on this side, and it needs a live
backend plus interactive input. CI proves it *compiles* in the configuration that matters, which is
strictly less than proving it works. It also does not measure keypress-to-visible — the number the
prediction effort is actually aimed at — which needs a capture rig rather than an overlay.

## [0.14.0] - 2026-08-14

### Added — a third CI configuration

`Unity Tests (no optional packages)`: neither `com.cuvara.netcode` nor
`com.rpgmmo.shared-gamelogic`.

**Until this row existed, every job installed `com.rpgmmo.shared-gamelogic`**, so the absent path of
`CUVARA_SHARED_GAMELOGIC` had never once been taken. That gate was asserted, never proven — the shape
of a gate that quietly never fires.

It is also the only row that can prove `Cuvara.DOTS.Netcode.Prediction` is gated on **both** defines
rather than only on netcode: the assembly must be absent here for a reason that is not the netcode
one. A row that can only fail one way is worth less than one that can fail two.

| | netcode absent | netcode 0.6.2 | no optional packages |
|---|---|---|---|
| `Tests.Editor` | 30 | 30 | 30 |
| `Tests.Runtime` | 23 | 23 | 23 |
| `Tests.GameLogic` | 41 | 41 | **0** |
| `Tests.Netcode` | 0 | 47 | 0 |
| `Tests.Prediction` | 0 | 14 | 0 |
| `Cuvara.DOTS.GameLogic.dll` | present | present | **absent** |
| `Cuvara.DOTS.Netcode.dll` | absent | present | absent |
| `Cuvara.DOTS.Netcode.Prediction.dll` | absent | present | absent |

### Added — two tests

- **`TheDependencyRunsOneWayOnly`** — neither `Cuvara.DOTS.Runtime` nor `Cuvara.DOTS.Netcode` may
  reference `Cuvara.DOTS.Netcode.Prediction`. If either ever did, the standalone-install property
  would be gone, and it would go *quietly*: a project with both optional packages compiles either
  way, so only a project missing one would notice — which is to say, only the third CI row.
- **`TheTwoAnchorFields_Correspond_UnderAnIdentityMapping`** — `Position` and `ServerPosition`
  describe the same point in different spaces, asserted under `XZPlane` where the mapping is a pure
  swizzle and correspondence is exact. It is explicitly **not** licence to derive one from the other;
  `ServerPosition_SurvivesAMappingThatWouldNotRoundTrip` is the test that forbids that, and the two
  are meant to be read together.

### Documented

The CI header now records, as a standing property rather than an incidental one, that **every job
installs the minimum its configuration names and hand-feeds nothing on a dependency's behalf** — with
the two findings that property has already produced about `com.cuvara.netcode`, and an instruction not
to "simplify" a failing row by adding the convenient dependency. A gate that supplies what the package
under test failed to declare is not a gate.

### Changed

- CI installs netcode **v0.6.2**, which adds `PredictionSurfaceContractTests` pinning the six members
  that cross the seam. The driver was written against the tagged package and its four call sites —
  `Reconcile(Vec2, long)`, `Advance(float)`, `Position`, `IsEnabled` — match the pinned signatures.

### Found in another package

**`com.cuvara.netcode` 0.6.2 still cannot be installed standalone**, and this is the third undeclared
dependency in it: `Cuvara.Netcode.Runtime.asmdef` references `Shared.GameLogic` with no
`defineConstraints` gate, while `package.json` declares only `com.cysharp.unitask` and two Unity
modules. `Shared.GameLogic` is a git-URL package, and the `gitDependencies` key netcode carries is not
a UPM field — UPM ignores it. So every consumer must hand-add that git URL, and netcode's own CI does
exactly that in its bootstrap manifest, which is why the gap is invisible there.

## [0.13.0] - 2026-08-14

### Added

- **`Cuvara.DOTS.Netcode.Prediction`** — the DOTS half of client-side prediction. Reads the local
  entity's `ReconciliationAnchor`, drives netcode's `LocalMovePredictor`, owns `PredictedTransform`,
  and writes `LocalTransform`.
  - `DotsPredictionBootstrap.Install(world, predictor, worldState)`.
  - `LocalPredictionReference` — managed singleton carrying the predictor and the `WorldState` that
    supplies `AckTick`.
  - `LocalPredictionSystem` (internal) in the new `PredictionSystemGroup`.
- **`SnapshotApplyGroup` and `PredictionSystemGroup`** in the core assembly, both inside
  `NetcodeSystemGroup`, prediction ordered after snapshot apply.

### The seam, and why the split falls where it does

netcode owns the algorithm — input buffer, replay through `TryMove`, smoothing. This package owns
everything ECS-shaped: reading the anchor, supplying the tick, claiming and releasing the marker,
writing the transform. A DOTS system in netcode would mean netcode depending on Entities, and the
arrow between these packages is one-way.

**A third assembly, gated on both `CUVARA_NETCODE` and `CUVARA_SHARED_GAMELOGIC`**, rather than code
added to `Cuvara.DOTS.Netcode`. The driver names `Vec2`, so it needs `Shared.GameLogic`; widening the
adapter's gate to require it would change what both CI rows mean and break one of the two
standalone-install properties CI now guards. The cost is one more assembly; the alternative was
coupling two independent optional dependencies into one.

### Two ordering groups instead of one `[UpdateAfter]`

Prediction must run **after** snapshot application: the anchor it reconciles against is written
there, and reconciling first uses the previous frame's authoritative position — a one-frame-stale
correction that reads as mistuned prediction rather than as an ordering bug, and gets chased in the
wrong package.

Expressing that with `[UpdateAfter(typeof(NetworkViewCommandSystem))]` would have needed an
`InternalsVisibleTo` grant and turned an internal system name into a cross-assembly ordering promise —
exactly what keeping systems internal is meant to prevent. Two public groups say the same thing
without naming a system, which is the package's stated contract everywhere else.

Both groups are created empty by `DotsViewBootstrap`, so a consumer's `[UpdateAfter]` resolves in a
project with neither optional package installed and does not change meaning when they arrive.

### The marker has two failure modes, not one

`PredictedTransform` says "something else owns `LocalTransform`". 0.10.0 guarded the case where it is
absent and the adapter keeps writing. This release guards the other side: **the marker present with
nothing writing** leaves the transform with *no* writer at all and freezes the avatar. It is reachable
three ways, and each has a test:

- a predictor with unusable settings (`IsEnabled == false`) — the driver releases rather than claims;
- a predictor that becomes disabled mid-session — the driver releases a marker it had claimed;
- `DotsPredictionBootstrap.Uninstall` — removes the marker from every entity before dropping the
  reference.

All three surface in a build rather than in CI, because a disabled predictor is a runtime
configuration. `DisabledPredictor_LeavesTheAdapterDrivingTheTransform` asserts the positive half too:
not claiming is only correct if the adapter is still writing, and asserting the marker's absence alone
would pass on a frozen avatar.

### Deviation worth flagging

`SimConversions` was the instructed conversion site, and it is `internal` to `Cuvara.DOTS.GameLogic` —
a differently gated assembly. Reaching it meant widening that assembly's public API or an
`InternalsVisibleTo` grant coupling two independent gates, for one line. The driver keeps a single
private conversion site instead, which is what "convert at the boundary, not once per call" asks for.

### Tests

12 in `Tests/Editor.Prediction/`, driven through the public groups. Adapter floor stays 47; the new
assembly's floor is 12, and `==0` in the netcode-absent configuration.

### Unverified

The driving system has **never run against a live server**. Its tests assert wiring — which
coordinates reach the predictor, when the marker is claimed and released, that the mapping is shared
with the adapter — not that prediction feels better. Keypress-to-visible is a measurement, and it has
not been taken.

## [0.12.0] - 2026-08-14

### Added

- **`ReconciliationAnchor.ServerPosition`** (`float2`) — the server's own `(x, y)`, stored exactly as
  `IEntityView.SetState` delivered it, before `SnapshotSpaceMapping` touches it.

  ```csharp
  public struct ReconciliationAnchor : IComponentData
  {
      public float3 Position;        // world space — what LocalTransform wants
      public float2 ServerPosition;  // verbatim (x, y) — what a predictor rewinds to
  }
  ```

**The world-space field could not do this job, and 0.10.0 claimed it could.** That release said the
anchor was "already through `SnapshotSpaceMapping`, so it needs no further conversion" — true for
writing `LocalTransform`, which was the only use then, and false for feeding a predictor. The shared
simulation clamps against map bounds expressed in **server** coordinates, and
`LocalMovePredictor.Reconcile` takes a server-space `Vec2` for that reason. A predictor handed only
the world-space value has to get back, and `SnapshotSpaceMapping` has `ToWorld` with no inverse.

**Adding an inverse was the rejected option.** `dot(p - Origin, Right)` is one line, and a float round
trip through a projection is **not bit-exact**: the recovered value differs in the last place, replay
integrates from a position the server never held, and the outcome is sub-ULP drift in the one system
whose entire justification is bit-exactness — most likely diagnosed as FMA contraction, in a
different package, by someone who never saw the inverse. Eight bytes per mirror entity removes the
possibility instead of making it unlikely. `SnapshotSpaceMapping` still has no inverse on purpose:
adding one would put the trap back within reach.

The settling argument is the anchor's own docstring — it exists as *"the value a predictor rewinds
to"*, and a predictor rewinds in the space it simulates in.

At spawn `ServerPosition` is `float2.zero` rather than a mapped value, which agrees with `Position`
being `mapping.Origin`: both say "the server has said nothing yet".

### Changed

- CI's netcode row now installs **v0.6.0** and the adapter floor is **47** (was 44).

### Tests

47 in `Tests/Editor.Netcode/`. Three new: the raw field carried verbatim alongside the mapped one;
the raw field unaffected by a hostile mapping with a `1e7` origin offset — the case where a round trip
would lose precision, asserted to prove the field does not depend on the mapping at all; and both
fields agreeing at spawn. A fourth existing test now also asserts `ServerPosition` survives while
`PredictedTransform` suppresses the transform write, which is the exact combination a predictor runs in.

### Corrected from 0.9.0

`"[0.4.0,)"` was named as the fallback if the bare `versionDefines` expression did not work. It is
**invalid syntax** — Unity throws `ExpressionNotValidException`. The bare `"0.4.0"` form is correct
and means `>=`, confirmed in CI in both directions and in the Unity project.

## [0.11.0] - 2026-08-14

CI/CD, for the first time. This repository had no `.github/` at all.

### Added — delivery

- **`release.yml`**, driven by a `v*` tag and **only** by a tag. No branch trigger: `npm publish`
  cannot be undone, and a bad version can only be superseded, never withdrawn. Pushing the tag is the
  last human gate before a permanent artifact exists.
  - **`Verify package.json version matches tag`**, a hard `exit 1`. Content checked against label by
    machine — the defect class that has cost this workspace the most.
  - Release notes are `awk`-extracted from the `## [VERSION]` CHANGELOG heading, which makes the
    CHANGELOG load-bearing rather than decorative: a missing or misspelled heading ships empty notes.
    A warning fires when the extraction comes back empty.
  - **`publish` job**, `needs: release`, publishing **`@cuvara/dots`** to GitHub Packages. The UPM
    name in `package.json` is `com.cuvara.dots`; GitHub Packages requires an npm scope, so the name is
    rewritten at publish time only and never committed. The rewrite asserts the expected input name
    first, so a rename upstream fails the publish instead of silently publishing something else.
- **`release-reminder.yml`** — never tags, never publishes; only notices that `main` carries an
  untagged version. Three states: tag on this commit (notice), tag elsewhere so commits since are
  unreleased (warning), no tag (warning with the exact commands). Needs `fetch-depth: 0`; a shallow
  fetch has no tags and would make every version look untagged. **It matters more here than in
  netcode**: the consuming project takes this package as a git *subtree*, so nothing downstream breaks
  when a version goes untagged and nothing downstream notices either.

**On publishing at all**: the project consumes this package as a subtree, so a registry artifact has
no current consumer. That was raised and overruled — publishing is wanted, and the argument is
recorded here rather than re-litigated. The consequence is what the gate section is about: publishing
turns every tag into a permanent artifact, so the test floors stop being hygiene and become the only
thing between an untested commit and an immutable version.

### Added — the gate

- **CI, for the first time.** This repository had no `.github/` at all. That was *honest* — a
  repository with no checks cannot mislead anyone — but it meant every verification the package ever
  had came from one person's Editor, and the numbers quoted in earlier releases were the consuming
  project's, not this package's.

  The gate deliberately does **not** assert "the test runner exited zero". A green run over **zero
  tests** is worse than no gate: it converts the absence of verification into a positive signal and
  spends the reviewer's budget for them. That is not hypothetical — `com.cuvara.netcode`'s gate
  reported `No tests were executed. 0/0 Passed` under a green check while a breaking interface change
  went through it.

  **This package is unusually exposed to that failure, by its own design.** Two of its four test
  assemblies are compiled out when their optional dependency is missing:

  | Assembly | Vanishes without |
  |---|---|
  | `Cuvara.DOTS.Tests.Netcode` | `com.cuvara.netcode` >= 0.4.0 |
  | `Cuvara.DOTS.Tests.GameLogic` | `com.rpgmmo.shared-gamelogic` |

  Nothing fails when they vanish. "Absent beats broken" is the right rule for a *consumer* and a
  dangerous one for a *gate*, and the workflow is where those two jobs are told apart.

- **`.github/scripts/assert_test_floors.py`** — per-assembly test-count floors parsed from the NUnit
  XML, never an exit code. `Assembly>=N` fails if the assembly ran nothing, which is what catches an
  assembly compiled out of existence; `Assembly==0` is satisfied by an absent assembly, which is what
  "correctly compiled out" looks like. Counts come from the `test-case` elements rather than the
  suite's own `total`/`passed` attributes, because those vary across Unity and NUnit versions and a
  missing attribute reads as zero — indistinguishable from the failure being checked for. A
  non-passing case fails the run independently of any floor.
- **`.github/scripts/test_assert_test_floors.py`** — 11 self-tests for the floor script, run in
  `validate`, no Unity needed. The gate is a program and it was wrong once; case 7 is the regression
  test for the `.dll`-suffix bug specifically, and one case asserts that a spec written `Foo.dll` is
  **not** silently accepted, because normalisation happens on one side only — a spec that can be
  written two ways will be written both ways.
- **`.github/scripts/check_metas.py`** — every Unity-visible tracked file and folder must have a
  committed `.meta`. Same failure family: a missing `.meta` disabled parts of this package for seven
  releases without failing anything.

### Two configurations, and one of them is not yet provable

| Job | Asserts |
|---|---|
| `Unity Tests (netcode absent)` | `Cuvara.DOTS.Netcode.dll` **absent**; Editor >= 30, Runtime >= 23, GameLogic >= 8, **Netcode == 0** |
| `Unity Tests (netcode 0.4.0)` | `Cuvara.DOTS.Netcode.dll` **present**; the same three, plus **Netcode >= 44** |

The first automates the standalone-install check that had been carried by hand — the one a human
stops re-running once it has passed twice. **The second cannot pass until `com.cuvara.netcode`
v0.4.0 is tagged**, and is left red rather than trimmed to make the run green. A gate shaped to pass
is the thing this whole file argues against.

Floors are lower bounds, not headcounts: they stop an assembly vanishing, and do not need editing
every time a test is added.

### Also

- The `pull_request` trigger fires on **every** base branch, not only `main`. netcode's fires only on
  PRs into `main`, so a stacked PR — how this repo has actually been shipping, #5 based on #4 — is
  ungated there.

### Proven by running it, including the red run

The scripts were exercised locally in both directions first (a missing `.meta` caught; floors
passing, and failing on a compiled-out assembly, an empty artifacts directory, a missing directory,
and a met floor with a failing test inside it). Then the workflow was landed with a **deliberately
failing test**, because a gate that has only ever been green is indistinguishable from one that
cannot fail.

**The red run earned its keep immediately — it found a bug in the gate itself.** Unity names the
NUnit Assembly suite after the built *file*, `Cuvara.DOTS.Tests.Editor.dll`, while the floor specs
name the *assembly*. Every floor therefore read `actual 0` while 95 test cases had in fact executed
and were printed two lines above. The bug failed **closed** — permanently red, never falsely green —
but the obvious fix for a permanently red gate is to lower the floors, which would have produced
exactly the useless gate this file argues against. Nothing but a real run would have surfaced it.

Real counts observed, and the floors now match them: `Tests.Editor` 30, `Tests.Runtime` 23,
`Tests.GameLogic` **41** — not the 8 a static `[Test]` grep suggested, because its `[TestCase]`
source expands — and `Tests.Netcode` 44 once netcode resolves.

The red run also confirmed two things that had only ever been argued: `Cuvara.DOTS.Netcode.dll` **is**
absent with no netcode installed (the standalone-install check, now automated rather than carried by
hand), and a single failing test does fail the run through the floor script independently of any
floor.

### Final numbers, both configurations green

| Assembly | netcode absent | netcode 0.4.0 |
|---|---|---|
| `Cuvara.DOTS.Tests.Editor` | 30/30 | 30/30 |
| `Cuvara.DOTS.Tests.GameLogic` | 41/41 | 41/41 |
| `Cuvara.DOTS.Tests.Runtime` (PlayMode) | 23/23 | 23/23 |
| `Cuvara.DOTS.Tests.Netcode` | **0, and required to be 0** | **44/44** |
| `Cuvara.DOTS.Netcode.dll` | **absent** | **present** |

Both halves of the `versionDefines` check are now automated and passing, which retires the manual
"remove the package and look" pass as the only evidence.

### Found in another package

`com.cuvara.netcode` 0.4.0 has **two undeclared dependencies** that a rich host project happens to
satisfy — so both are invisible in the Editor and appear only in a minimal project like CI.

1. **VContainer.** `Cuvara.Netcode.Runtime.asmdef` lists it in `references` with no
   `defineConstraints` gate and no `package.json` dependency. Fails loudly: `CS0246` from inside the
   package.
2. **`System.Runtime.CompilerServices.Unsafe`.** netcode ships `Runtime/Plugins/Google.Protobuf.dll`,
   which needs it; netcode neither ships it nor depends on it. **Fails silently, and cascades:**
   `Google.Protobuf` does not load → `Cuvara.Netcode.Runtime` does not load → `Cuvara.DOTS.Netcode`
   does not load → `Cuvara.DOTS.Tests.Netcode` does not load → 44 tests do not run, **and the runner
   still exits 0**. The real project masks it with four separate providers (`com.gdk.core`, Burst, a
   NuGet folder, and R3's transitive `org.nuget.system.runtime.compilerservices.unsafe`).

The second is very likely the root cause of netcode's own `No tests were executed. 0/0 Passed`: its
test assembly references the same runtime assembly that fails to load, so its test count collapses to
zero by the same cascade.

Both are worked around in this workflow's manifest, with a comment saying they are someone else's
defects rather than dependencies of this package.

## [0.10.0] - 2026-08-14

Prepares for prediction by giving the local player's transform a single writer, **before** a predictor
exists. Nothing behaves differently today: with no `PredictedTransform` in the world, every entity is
positioned by the adapter exactly as in 0.9.0.

### Added

- **`ReconciliationAnchor`** — the last authoritative position the server reported, in world space
  (already through `SnapshotSpaceMapping`). Written for every replicated entity, at spawn and on every
  state.
- **`PredictedTransform`** — a marker something else adds to say "I own this entity's
  `LocalTransform`". While present, the adapter writes the anchor and leaves the transform alone.

### Why, since nothing is broken yet

Once prediction owns the local player's per-frame position, prediction and this adapter would both
write `LocalTransform` in the same frame — the adapter first in `InitializationSystemGroup`,
prediction second in `SimulationSystemGroup`. **That works.** On every frame prediction runs, the
later write wins and the result is correct. It fails only on the frames prediction does *not* run,
where the avatar snaps back to the server position for one frame: intermittent, visible only as feel,
local player only, and presenting as a bug in the release whose entire purpose is prediction. Every
expensive defect in this project so far has been of that family — green, running, silently wrong — so
this one is paid for in advance.

The split is not a new mechanism. `NetworkEntityState` already separates "what the server said" from
"what the client shows", for hp, and for the same reason. Position for a predicted entity is that same
split arriving at the one field that just started needing it.

### Design notes

- **Named for what a predictor does with it.** Not `ServerPosition` or `NetworkPosition`: those name
  the source and invite the obvious wrong move, someone deciding the local entity looks stale and
  writing this into `LocalTransform`. A predictor *rewinds to and replays from* an anchor. The name is
  meant to make the misuse read as wrong before it is run. Checked against every installed package
  first — `Anchor` alone is taken (`Unity.Physics.PhysicsJointComponents`); `ReconciliationAnchor` and
  `PredictedTransform` are free.
- **Presence, not a flag**, matching `ViewConfigRef`'s reasoning: a `bool` has a default, and a
  default meaning "predicted" or "not predicted" is a decision made silently for every entity that
  never set it.
- **Keyed off the tag, not off `NetworkEntity.IsLocal`.** That was the obvious shortcut and is wrong
  in a way that bites immediately: with no predictor installed the local avatar would simply stop
  moving. A test guards exactly that.
- **No tick on the anchor, deliberately.** An anchor is a position *at a tick*, and this adapter does
  not know the tick — `IEntityView.SetState` carries `(id, x, y, hp, maxHp)`. The tick is
  `WorldState.AckTick`, which netcode documents as "the reconciliation anchor for the prediction
  layer" and a predictor reads directly. Inventing one here, or inferring it from arrival order, would
  produce a number that looks authoritative and is not.
- **Written for remotes too, and the reason is chunk layout.** Adding it only to predicted entities
  would split local and remote mirrors into **different archetypes** — two sets of chunks, with every
  query over mirror entities iterating both, in a package whose whole justification is chunk
  iteration. A structural cost at query time on every system, to save one `float3` per entity.
  Uniform keeps one archetype and pays 12 bytes. Recorded because "why do remotes carry this" is the
  obvious instinct and splitting the archetype is the obvious, expensive fix for it.

### Tests

44 in `Tests/Editor.Netcode/` (was 39). Five new: the anchor present from spawn and tracking every
state; the anchor written for remotes; `PredictedTransform` suppressing the transform write while the
anchor still lands; removing the tag handing the transform back; and — the guard for the shortcut not
taken — the local entity moving exactly as before when no predictor exists.

### Verified after the fact

This shipped uncompiled. It is compiled now: the CI gate added in 0.11.0 runs the adapter's tests
against `com.cuvara.netcode` 0.4.0 and reports **44/44 passing**, which covers everything this release
added. The one claim still not made is a performance claim —
`EntityManager.HasComponent<PredictedTransform>` per entity per state is one lookup on a path that
already calls `HasComponent<Health>`, so it is no new shape, but it remains unmeasured.

## [0.9.0] - 2026-08-14

Follows `com.cuvara.netcode` 0.4.0, which added the server's entity kind to `IEntityView.Spawn`. The
guessing this package did in 0.8.0 is gone — and the escalation that produced the netcode change
started here, in 0.8.0's own note that the prefix resolver was a workaround with a named exit.

### Changed — breaking

- **`DotsEntityView.Spawn` now takes the entity type**: `Spawn(string id, bool isLocal, string type)`,
  matching netcode 0.4.0. It is not source-compatible with 0.8.0, and could not be: the old signature
  no longer satisfies `IEntityView`, and an overload does not rescue callers who hold the interface
  rather than the class.
- **`INetworkArchetypeResolver.TryResolve` now takes a `NetworkEntityDescriptor`** — `Id`, `Type`,
  `IsLocal` in one readonly struct — instead of `(string id, bool isLocal)`.
  **A parameter object, deliberately.** `IEntityView` has just broken every implementation and every
  call site over adding exactly one field. This seam is younger and smaller and can decline to repeat
  that: a fifth signal — faction, team, level — becomes a field on the struct, and existing resolvers
  keep compiling. It is not a claim netcode should have done the same; its interface is three narrow
  methods where a struct would be a heavier promise than the seam wants.
- **`NetworkEntity` gains `Type`** (`FixedString32Bytes`), so a system can filter by kind without a
  managed lookup — what the reference implementation's `EnemyTag` was for, as data rather than as a
  tag the package cannot name. Truncating rather than refusing, unlike `Id`: resolution runs on the
  full managed string before the command is queued, so a long kind still reaches the right archetype
  and only reads back clipped in this convenience field.
- **The asmdefs now require netcode >= 0.4.0** via the `versionDefines` expression (`"0.4.0"` instead
  of `""`). With an older netcode installed, `CUVARA_NETCODE` is never defined and
  `Cuvara.DOTS.Netcode` does not compile into the project at all. That is the point: **absent beats
  broken.** Without it, netcode 0.3.x plus dots 0.9.0 is a `CS0535` in a package the consumer did not
  write and cannot easily fix. Expressed here rather than as a `package.json` dependency because the
  adapter is optional — a `dependencies` entry would force netcode on every consumer of this package.

### Removed

- **`PrefixArchetypeResolver` is deleted**, along with its tests. **Argued, not defaulted.** The case
  for keeping it as a fallback is that netcode documents `type` as "empty when the server sent no type
  at all", so a server that never populates the field is representable. The case against, which wins:
  a server that does not send `Type` has no obligation to encode kind in its ids either — the
  `"enemy-"` convention was one sample's, not a protocol — so the fallback would be guessing against a
  server whose vocabulary we would not know, to avoid guessing against one that tells us. And a
  strictly better answer already exists for the empty-type case: `TypeArchetypeResolver`'s
  `unknownArchetype`, which is explicit, uniform, and fails *visibly* — every unknown entity looks the
  same and obviously placeholder — where prefix matching fails invisibly, some ids happening to match
  and some not. A consumer that genuinely wants id-based dispatch writes its own
  `INetworkArchetypeResolver`; that is what the seam is for.
  Nothing depended on it: 0.8.0 shipped hours earlier and the client has no gameplay code yet.

### Added

- **`TypeArchetypeResolver`** — exact ordinal mapping from the server's entity kind to an archetype
  name, plus an optional local-player override and an optional catch-all.
  - **No case folding and no prefix matching.** The type is a wire enum in string clothing; treating
    `"Mob"` as `"mob"` would paper over a schema disagreement that should be visible.
  - **The local override beats the type rule.** `IsLocal` is derived by comparing the id with the
    client's own `NetworkClient.UserId`, so it is the one field in a snapshot that does not depend on
    the server's vocabulary matching this build's. The ordinary case is `"player"` + `IsLocal` → a
    distinct local archetype; the incoherent case — a `"mob"` whose id is the local player's — is
    server confusion, answered with the client's own belief.
  - **An unmapped or empty kind is refused and logged once per kind**, not mapped to a silent
    default. A build talking to a newer server would otherwise render every unknown kind as a player
    and look like it was working. The two cases get different messages, because "I don't know that
    kind" and "you sent no kind" are different diagnoses.
  - One constructor, `(localArchetype, unknownArchetype, params Rule[])`. An `IReadOnlyList` overload
    was written and removed: with both present, `new TypeArchetypeResolver(null, "x")` is `CS0121`.
- **`NetworkEntityDescriptor`** — the resolver's input. Normalises null `Id`/`Type` to empty at the
  boundary so no implementation has to null-check.

### Tests

39 in `Tests/Editor.Netcode/` (was 28), still driven through the public groups rather than named
systems. The 20 `Spawn` call sites carry a type now, and **the ids were rewritten to carry no
meaning**: the mob's id is `"uuid-e1"`, and one test spawns a *player* whose id is literally
`"enemy-9"`. Under the old prefix resolver the first would have been a player and the second a
goblin — both wrong, both silent. New coverage: type-decides-archetype in both directions, the type
landing on `NetworkEntity`, unmapped and empty kinds refused, the once-per-kind logging, the catch-all,
ordinal matching, duplicate/incomplete rules throwing at construction, and a respawn that re-resolves
a *different* kind for a reused id.

### Verified after the fact

**This shipped uncompiled**, and the list below is what a build had to settle. The CI gate added in
0.11.0 settled all of it: `Cuvara.DOTS.Netcode.dll` is **absent** under no netcode and **present**
under 0.4.0, so the `versionDefines` expression `"0.4.0"` behaves as ">= 0.4.0"; `FixedString32Bytes`
as an `IComponentData` field and the `in`-parameter interface implementation both compile; and all
**44/44** tests pass. The original list, kept because the reasoning is still the reason each one
mattered:

- The `versionDefines` expression `"0.4.0"` behaving as "0.4.0 or newer". The check is that
  `Cuvara.DOTS.Netcode.dll` **disappears** under netcode 0.3.2 and **appears** under 0.4.0. If the
  expression syntax is wrong the assembly silently never compiles, which is this package's least
  favourite failure mode; a range literal (`[0.4.0,)`) is the fallback if the bare version does not
  work. The bare form is what 140 non-empty expressions across this project's own resolved packages
  use — `com.unity.physics`, `com.unity.collections`, `com.cysharp.messagepipe` gating on VContainer
  `1.14.0` — so it is the well-trodden shape, but it is still unrun here.

  **Known edge, not a defect**: a bare version excludes that version's prereleases, because
  `0.4.0-pre.1` sorts below `0.4.0` under semver. Unity's own packages work around it by writing the
  predecessor (`9.9.9` for "10.0.0 or newer"). `com.cuvara.netcode` has only ever tagged plain
  versions, so this does not bite today; if it ever ships an `0.4.0-pre`, the expression has to
  become `0.3.99`.
- `FixedString32Bytes` as an `IComponentData` field and its `CopyFromTruncated` overload.
- `in`-parameter interface implementation (`TryResolve(in NetworkEntityDescriptor, out string)`)
  across the assembly boundary.
- All 39 tests, and the `LogAssert.Expect` calls in particular — an over- or under-counted expected
  error fails the test either way.

## [0.8.0] - 2026-08-14

### Added

- **`Cuvara.DOTS.Netcode` — the `IEntityView` adapter.** With `com.cuvara.netcode` installed, server
  snapshots drive ECS entities through this package's existing view pipeline. It is the piece that
  makes the package usable by a client rather than only by a scene.
  - `DotsEntityView` implements `Cuvara.Netcode.View.IEntityView` (the three methods; the interface is
    not widened). Each replicated id becomes an entity carrying `NetworkEntity` (the wire id and
    `IsLocal`), `NetworkEntityState` (newest authoritative hp), a `LocalTransform`/`LocalToWorld`
    pair, and the `EntityViewRequest` + `ViewConfigRef` the spawn path already understands.
  - `INetworkArchetypeResolver` and `PrefixArchetypeResolver` decide which archetype an id is shown
    as. `SnapshotSpaceMapping` decides where the server's 2D plane lands in the world.
  - `DotsNetcodeBootstrap.Install(world, view)` publishes `NetworkEntityViewReference` and creates
    the internal drain system inside `NetcodeSystemGroup` — the group that shipped empty in 0.6
    precisely so this could land without changing what a consumer's `[UpdateAfter]` means.
  - Gated by `versionDefines` on `com.cuvara.netcode` + a matching `defineConstraints`, like
    `Cuvara.DOTS.GameLogic` is for the shared logic. **The package still installs and compiles with
    netcode absent**, and `Cuvara.DOTS.Runtime` keeps exactly its five Unity references — the netcode
    dependency exists in the new assembly and nowhere else. The arrow is one-way: DOTS may reference
    netcode, netcode never references DOTS.

### Design notes

- **`SetState` enqueues; it does not write components.** Three reasons, in order. (1) *Thread
  affinity*: `WorldViewBinder.Tick` is called by the consumer, and the netcode's own guidance is to
  drive world state from the socket thread — `EntityManager` writes from there are undefined
  behaviour, not an exception, and the reference implementation is main-thread-only without saying
  so. (2) *Structural changes belong at a declared point in the frame* rather than wherever the
  caller happens to run, possibly mid-`SimulationSystemGroup`. (3) *Ordering*: one FIFO preserves
  spawn → state → despawn.
  The queue costs no view latency. `NetcodeSystemGroup` is in `InitializationSystemGroup`, so a drain
  runs before `TransformSystemGroup` computes `LocalToWorld` and long before `PresentationSystemGroup`
  runs `ViewSystemGroup` → `ViewLifecycleGroup` → `ViewTransformSyncGroup`. A snapshot enqueued before
  frame N's initialization is an entity, a transform, a view and a *positioned* view within frame N.
  A direct write cannot beat that and can lose to it: one landing after `TransformSystemGroup` gets a
  stale `LocalToWorld` and spawns its view in the wrong place.
- **The 2D → 3D mapping is caller-supplied.** The reference implementation wrote
  `new float3(x, 0.5f, y)` inline, and that literal is two unrelated things fused: which plane the
  server's coordinates live on (a property of the *world*, identical for every entity — now
  `SnapshotSpaceMapping`, defaulting to `XZPlane`) and a half-height lift so a capsule's pivot sits on
  the ground (a property of the *art*, different per archetype — already `ViewConfig.PositionOffset`,
  and applied to the view instance rather than to the entity). Splitting them is strictly better than
  the constant: gameplay maths keeps a 2D entity position, and only the visual is lifted. A
  `ViewConfig` field was rejected because it would let two archetypes disagree about which axis is up.

### Changed

- `Runtime/AssemblyInfo.cs` grants `InternalsVisibleTo` to `Cuvara.DOTS.Tests.Netcode`, for
  `ViewConfig.Configure` / `ViewArchetypeLibrary.Configure` — the same reason the other two grants
  exist. The adapter's tests do **not** name a package system: they drive `NetcodeSystemGroup` and
  `ViewSystemGroup`, so what they assert is the published ordering contract.

### Not carried over from the reference implementation

`Samples~/DOTSSample/DOTSEntityView.cs` in `com.cuvara.netcode` is one game's rules. What was left
behind, and why:

- **The `"enemy-"` id prefix** — kept as a *mechanism* (`PrefixArchetypeResolver`), dropped as a
  *value*. The prefix and the archetype it names are constructor arguments. It is still a workaround:
  `IEntityView.Spawn` takes `(id, isLocal)`, and the snapshot's `ResolvedEntity.Type` is not forwarded
  through `WorldViewBinder`, so the id is the only signal a view has. `INetworkArchetypeResolver` is
  the named exit — if a later `com.cuvara.netcode` forwards the type, the move is a resolver over
  `Type` and the deletion of `PrefixArchetypeResolver`, not more prefix rules.
- **`Health { 30, 30 }` at spawn** — the wire already carries hp and maxHp, so the adapter writes the
  real values instead of a literal. They land on `NetworkEntityState`; `Health` is opt-in
  (`writeHealth: true`) because `Health` means "destroy at zero" in this package, and mirroring server
  hp into it lets `HealthDeathSystem` destroy an entity the server is still listing. When opted in,
  `Health` is added on the first *state* rather than at spawn, so no entity ever carries `Health{0,0}`
  across a simulation tick.
- **`AutoAttack { 0.3f, 10f, 1 }` and `PlayerCombatTag`/`EnemyTag`** — **no config equivalent exists,
  and none was invented.** This package has no combat components beyond `Health`, and cooldown, range
  and damage are game rules, not view configuration. A consumer that wants them adds them to entities
  carrying `NetworkEntity`, from its own system.
- **The eight-colour player palette and per-instance material creation** — **no config equivalent
  exists.** `ViewConfig` carries a view key, pool size, scale, offsets and 2D sorting; it has no
  colour, material or renderer field, and the view layer spawns pooled prefabs rather than building
  `RenderMeshArray` entities. Per-player tinting is a consumer concern today. If it should become a
  package concern, the honest shape is a colour field on `ViewConfig` plus something applying it in
  `ViewLifecycleGroup` — that is a separate change with a separate justification.
- **`GetEntityLabels` and the OnGUI overlay** — debug UI for a sample scene.
- **`RenderMeshUtility` / `Unity.Entities.Graphics`** — the adapter drives the pooled-GameObject view
  layer this package already has, so it needs no rendering dependency at all.

### Tests

`Tests/Editor.Netcode/` — 28 tests: the adapter end to end through the public groups (same-frame
positioned view, the config-driven archetype, the mapping/offset split, despawn and re-entry, the
duplicate-spawn and unknown-id guards, hp routing with and without `writeHealth`, full drain), the
mapping maths, the resolver's ordering rules, and a reflection layout test asserting the drain system
is internal, `[DisableAutoCreation]`, and two hops above `PresentationSystemGroup`.

A separate test assembly, mirroring `Cuvara.DOTS.Tests.GameLogic`, rather than the existing ones:
netcode-dependent tests in `Cuvara.DOTS.Tests.Runtime` would force that assembly to reference
`com.cuvara.netcode`, which breaks the no-netcode install this release is built around.

### Unverified

**Nothing here has been compiled.** No Unity Editor was available — another workstream owned it — so
this release is reviewed code, not built code. Specifically unverified: that the asmdef
`versionDefines`/`defineConstraints` pair resolves as intended in a project with and without
`com.cuvara.netcode`; that `SystemAPI.ManagedAPI.GetSingleton` works from this `ISystem` as it does in
`EntityViewSpawnSystem`; and every one of the tests above.

## [0.7.0] - 2026-08-14

### Added

- **ViewConfig and data setup** — the last piece of the original hybrid core. Until now a consumer
  configured views by hardcoding asset keys at the call site.
  - `ViewConfig` (ScriptableObject): view key, pool size, uniform scale, position/rotation offsets,
    and 2D sorting layer/order. **Runtime authoring, no `Baker`** — consumers spawn from server
    snapshots, where there is no subscene and no authoring GameObject to bake, and baking would also
    pull in `Unity.Entities.Hybrid`.
  - `ViewArchetypeLibrary` (ScriptableObject): named archetype definitions. The name is the join
    between the server's vocabulary and the art; names are distinct from view keys so two archetypes
    can share a prefab and differ only in scale or pool size.
  - `ViewConfigTable`, a blob of `ViewConfigRecord`, published as the unmanaged singleton
    `ViewConfigTableReference` and built by `ViewConfigCatalog` — which owns the blob's lifetime,
    resolves names to indices, and reports `PoolSizesByKey()` for feeding `ChunkViewProvisioner`.
  - `ViewConfigRef`, `ViewTransformOffset` and `ViewSortingKey` components.
- The spawn path resolves a `ViewConfigRef` against the table for its key and offsets. **The bare-key
  path is unchanged**: an entity carrying only `EntityViewRequest.ViewKey` behaves exactly as before,
  which is what the sample and existing consumers use.

- **Simulation components and systems** — `TimeToLive`, `Health`, `MoveToward`, `SpinSpeed`, `MoveData`
  as `IComponentData`, each with an `internal` Bursted `ISystem` in the groups that were declared
  empty for them: `MoveTowardSystem` → `MoveBounceSystem` → `SpinSystem` in `MovementSystemGroup`,
  `HealthDeathSystem` → `TimeToLiveSystem` in `LifecycleSystemGroup`, every position in the chain
  written down rather than left to Entities' fallback. `DotsSimulationBootstrap.InstallSimulationSystems(World)`
  installs them; it is separate from the view bootstrap because an entity that moves, spins and
  expires needs no GameObject, so the simulation half must not require an `IViewAssetProvider`.
  Three couplings from the reference implementations were removed rather than carried across: the
  game-specific enemy tag and the stats singleton that `HealthDeathSystem` wrote, and Unity's
  `EndSimulationEntityCommandBufferSystem` — both destroying systems now use the package's own
  buffer, which plays back before the transform systems so no view is ever synced against an entity
  that died this frame. `MoveToward` gains a `StopDistance` field in place of the reference's
  hardcoded 0.1-unit arrival epsilon, which is a tuning value for one game's scale rather than a
  constant a shared package may impose. The countdown component is `TimeToLive` rather than the
  reference's `Lifetime`: that name is ambiguous against `VContainer.Lifetime` in any file importing
  both, which broke `Cuvara.DOTS.DI` — a common word in a shared assembly will collide again. Covered by `SimulationSystemsTests` (play mode), which ticks
  `GameplaySystemGroup` rather than individual systems so the shipped ordering is what is asserted.

### Changed

- The transform sync now composes `ViewTransformOffset` into each sample, inside the Bursted job. The
  sync overwrites the GameObject transform every frame, so an offset applied only at spawn would last
  exactly one frame — which reads as "offsets don't work" rather than as a lifetime bug.
- `EntityViewSpawnSystem` adds `ViewTransformOffset` to every linked entity, identity when
  unconfigured, so the sync keeps a single query.

### Accepted limitations

- **`ViewSortingKey` is carried, not applied.** Nothing in the package touches a `SpriteRenderer`;
  that is the 2D roadmap item. Authoring it now costs two ints and means the 2D branch need not
  re-open the asset format, but no sorting order is set on anything today.
- **Offsets are uniform-scale only**, matching `ViewTransformSample`, which carries one float.
- **A `ViewConfigRef` with an out-of-range index warns and falls back to the request's own key**
  rather than throwing or rendering an arbitrary archetype — that state means the catalog was rebuilt
  without its referencing entities being updated.
- **Config lookup by name is linear** over the blob. A catalog holds tens of archetypes; resolve once
  at request time and carry the index rather than scanning per frame.
- **A view key longer than 61 UTF-8 bytes is truncated, not rejected.** The record's key is a
  `FixedString64Bytes`; truncating stops one over-long key from failing catalog construction for the
  whole library. It warns by asset name, and a truncated key matches nothing in the pool, so the view
  never spawns rather than spawning something wrong.
- **`ViewConfigCatalog.Build` is only safe between frames**, never while a tick is in flight: it frees
  the previous blob immediately, and entities hold indices into it. A rebuild also invalidates every
  index handed out before it — re-resolve names afterwards. The spawn path catches an out-of-range
  index, but an index that is merely wrong cannot be detected.
- **Compiles clean**, verified in the consuming project. EditMode 210 (was 205), PlayMode 15 (was 10).

## [0.6.3] - 2026-08-14

### Documentation

- **The test suite has now run in a consuming project, and passes.** *Known debts* said no test in
  this package had ever executed — true when written, false now. Measured: EditMode **205/205**, of
  which **66 belong to this package** (the project alone was 139 before its assemblies existed);
  PlayMode **10/10**; still 205/205 after the package became a git subtree, with six
  `Cuvara.DOTS.Tests.*` files in `Library/ScriptAssemblies`.
- **The `testables` requirement stays documented, because it is still load-bearing.** It was found
  with a git-URL install, but the assemblies are built through `testables` even now that the package
  is embedded — so it is not a `PackageCache`-only quirk. The remaining debt in this area is
  narrower and unchanged: no test references `Cuvara.DOTS.DI` or `Cuvara.DOTS.GameFoundation`, so
  the MessagePipe and GameFoundation adapters are compile-checked only.
- Version bumped to 0.6.3 so the tag carrying these docs matches the manifest. Documentation only —
  no runtime, test or asmdef change.
- **Consumers must list this package in `testables` or its tests do not exist for them.** Written up
  in the README install section. A git-URL install lives in `Library/PackageCache` and Unity builds a
  package's test assemblies only when the project's `Packages/manifest.json` names it in `testables`;
  the entry this package declares in its own `package.json` does not substitute. The failure is
  silent — no `Cuvara.DOTS.Tests.*` in `Library/ScriptAssemblies`, and a Test Runner filtered to
  `Cuvara.DOTS` reporting *no tests found*, which is indistinguishable from a package that ships no
  tests. Editing the manifest is necessary but not sufficient: an Editor that already resolved the
  package caches that resolution until restarted, so verification is the presence of
  `Cuvara.DOTS.Tests.Editor.dll`, not the manifest edit.
- README install snippets updated from `#v0.1.0` to `#v0.6.2`; the old ones pinned a tag from before
  any runtime code existed.
- *Known debts* in `ROADMAP.md` upgraded from suspicion to fact. The measurement it recorded — a full
  EditMode run passing 139/139 while `Cuvara.DOTS` contributed **zero** — was accurate before the
  Editor restart made `testables` take effect, and is superseded by the 205/205 above; it is kept
  here as the before-half of that pair. It also records that `Cuvara.DOTS.Tests.GameLogic` carries a second, deliberate gate
  (`CUVARA_SHARED_GAMELOGIC` plus a `Shared.GameLogic` assembly reference), so it can legitimately
  contribute no cases when the optional dependency is absent, and that no test references
  `Cuvara.DOTS.DI` or `Cuvara.DOTS.GameFoundation`.

### Changed

- **`ROADMAP.md` rewritten from the tree instead of from the plan.** It described the package as
  mid-v0.2.0 while `package.json` read 0.6.2, and omitted everything shipped since — cascade
  release, the messaging seam, the system group tree. Now organised Done / In progress / Planned,
  with shipped work carrying the real version it shipped in and unshipped work carrying only an
  order, since a milestone number assigned in advance goes stale the moment the order changes.
  Two items are stated as having **no code in the tree** rather than as in progress: ViewConfig +
  data setup, and the simulation components and systems. The `ISimulationModel` seam is recorded as
  a deliberate reorder — pulled ahead to settle the `Shared.GameLogic` question early — not as
  work that happened by accident. A *Known debts* section names the never-executed test suite, the
  MessagePipe and GameFoundation adapters that no test exercises, the empty
  `Cuvara.DOTS.Editor` assembly, and the load-bearing `.meta` files. Net 13 lines shorter.

### Verified

- **The package has now been compiled and run in a real Unity 6 Editor** (6000.3.9f1, URP project,
  embedded install). Every line below is an observed result, not an expectation.
  - `Cuvara.DOTS.Runtime`, `.DI`, `.GameFoundation`, `.GameLogic`, `.Editor` and the sample assembly
    compile with **zero errors** at 0.6.2. The 0.6.2 asmdef fix is sufficient on its own — no further
    references were needed.
  - `Samples~/HybridViews` imports, its scene opens with all three root objects, and the
    `HybridViewsSample` MonoBehaviour **binds** (pinned `.meta` GUIDs survive the import) with its
    serialized values intact: `_stepSeconds = 4`, `_warmCountPerKey = 4`, three view definitions.
  - The full sample timeline ran green in play mode. Observed: key de-duplication (`cube` listed
    twice → refcount 1); 10 entities spawned with 2 deferred on the cold key; shared `sphere`
    reaching refcount 2; **cascade release** reporting `Released=True, ViewsDespawned=2` while
    `sphere` survived at refcount 1; a second release returning `Released=False, WasTracked=False`
    (no-op, not a refusal); and a clean finish — `instantiated=12, acquires=10, recycles=10,
    key teardowns=3, live views=0, tracked chunks=0`. Every acquire was matched by a recycle.
  - Transform sync confirmed by sampling live view positions mid-run: cubes orbiting at r≈4, y=0 and
    spheres at r≈6.5, y=1.5, matching the `OrbitMotion` values the entities carry.

### Fixed

- The sample's closing log line claimed "acquires above instantiations is the pool doing its job",
  which the real run contradicts: prewarming instantiates 4 per key up front, so instantiations
  (12) exceed acquires (10) by design. It now states what the numbers actually mean.

### Notes

- **Running the sample from an unfocused Editor needs `Application.runInBackground`.** With the
  Editor in the background the player loop does not tick — the observed symptom is play mode active
  with `Time.time == 0` and `frameCount == 1`, so the sample logs step 1 and appears to hang. This is
  Editor behaviour, not a package or sample defect. Now written up as a troubleshooting section in
  the sample README, alongside the duplicate-sample case: two version folders under
  `Assets/Samples/Cuvara DOTS/` collide on the assembly name
  `Cuvara.DOTS.Samples.HybridViews` and block play mode until the stale one is deleted, because
  importing a sample does not remove the copy a previous version left behind.

## [0.6.2] - 2026-08-14

### Fixed

- **First compile: five errors, from two missing assembly references.**
  - `Unity.Transforms` added to `Cuvara.DOTS.Runtime`, `Cuvara.DOTS.Tests.Runtime`,
    `Cuvara.DOTS.Tests.Editor` and the sample assembly. `LocalToWorld` and `TransformSystemGroup`
    live in `Unity.Transforms`, which is a separate assembly from `Unity.Entities` — this was fallout
    from the `LocalTransform` → `LocalToWorld` change, where the reference was never added, and it
    had been asserted rather than checked.
  - `UniTask` added to `Cuvara.DOTS.DI`. MessagePipe's `ISubscriber<T>` surface exposes `UniTask` in
    its signatures, so a reference to it is required to compile against that interface at all.
- Audited every asmdef's `references` against the types its files actually use, rather than fixing
  only the assemblies that happened to error. Three of the four `Unity.Transforms` gaps were in
  assemblies the compiler had not reached yet.

### Notes

- The package produced **no** assemblies until its `.meta` files were committed: a git-URL install
  lands in `Library/PackageCache`, which Unity treats as immutable, so it would not generate them and
  silently ignored every source file. Nothing in 0.1.0–0.6.1 had ever been compiled. Add a `.meta`
  alongside every new file or the same silence returns.

## [0.6.1] - 2026-08-14

### Changed

- **The view group is nested rather than flat**, so a consumer can inject work between "views exist"
  and "views are positioned" without naming a package system:
  `ViewSystemGroup` → `ViewLifecycleGroup` (despawn, then spawn) and `ViewTransformSyncGroup`
  (`UpdateAfter(ViewLifecycleGroup)`).
- **Despawn runs before spawn again.** Recycling a dead entity's view first makes the freed pool
  instance available to the same frame's spawns, so a frame that destroys ten entities and creates
  ten more instantiates nothing; the reverse order grows the pool to the sum instead of the maximum.
- The sample ticks `ViewLifecycleGroup` rather than the whole `ViewSystemGroup` when it needs a
  despawn flushed early — the narrower group is exactly what it needs, and ticking the parent would
  run the transform sync twice in one frame.

## [0.6.0] - 2026-08-14

### Changed

- **Chunk release now cascades instead of refusing.** 0.5.0 refused a release while live views stood
  on the keys it would drop; the approved behaviour is to take those views down first. `ReleaseChunk`
  finds every entity whose `EntityViewLink` points at an instance of an expiring key, puts it through
  the **ordinary despawn path** — recycle the instance, drop the handle, clear the link — and only
  then lets the reference count reach zero and calls `IViewAssetProvider.Release`. That is the exact
  inversion of the original bug, where the asset died first and the links were left dead with nobody
  to clean them. Only keys the chunk is the *last* referencer of are cascaded: a key another chunk
  still lists is not being released, so its views are in no danger.
- `ChunkReleaseResult` now carries `ViewsDespawned` and `KeysReleased`; the refusal fields are gone.
  `ReleaseAll` returns total views cascaded. `ChunkReleased.LiveViewCount` became `ViewsDespawned`.
- `ILiveViewCounter` is replaced by `IViewCascadeSink` — the provisioner no longer needs to *count*
  live views, it needs to *end* them, and one seam is better than two overlapping ones.

### Added

- `ChunkCascadeReleased(ChunkId, KeyCount, ViewsDespawned)` — published whenever a release tears down
  views. Surviving without a view is the intended outcome of a streaming unload, but a consumer
  cannot infer it from anything else, so shipping it silently would make an entity that quietly
  stopped rendering indistinguishable from a bug. A log line is not enough: the code unloading a
  region is usually not the code owning the entities in it.
- `EntityViewCascade` — the `IViewCascadeSink` implementation, and the only type that knows both the
  registry and the world.
- `EntityViewRegistry.TryGetKey`, so the cascade can map a handle back to the key it came from.
- `EntityViewCascadeTests` (play mode, real `World`) and reworked `ChunkReleaseSafetyTests`.

### Accepted limitations

- **The cascade is synchronous in its managed half and deferred in its structural half.** Recycling
  the instance and dropping the handle happen inside the `ReleaseChunk` call; removing
  `EntityViewLink`/`EntityViewLinkCleanup` is recorded into `DotsEndSimulationCommandBufferSystem`,
  because an `EntityManager` structural change from arbitrary consumer code would invalidate any
  chunk iteration in flight. **The state a caller observes mid-cascade**, until that buffer plays back
  at the end of the next `GameplaySystemGroup`: GameObjects already gone and assets already released,
  while the entities still carry an `EntityViewLink` whose handle no longer resolves. Nothing
  misbehaves in that window — `ApplyTransform` ignores an unknown handle and `Despawn` on a dropped
  one is a no-op — but query for a live view via `EntityViewRegistry.Get`, not by the presence of the
  component. Pinned by a test.
- **No respawn loop**: the cascade removes the link and does not re-add an `EntityViewRequest`, and
  the spawn system acts only on requests, so a cascaded entity stays view-less until something
  deliberately requests a view again. Pinned by a test.
- A provisioner built without an `IViewCascadeSink` still releases unconditionally; the constructor
  documents it as unsafe for streaming and a test pins it, so it is a decision rather than an accident.
- **Nothing has been compiled.** The MessagePipe adapters remain entirely unexercised.

## [0.5.0] - 2026-08-14

### Fixed

- **Releasing a chunk no longer destroys live views.** `ChunkViewProvisioner` refcounts *chunks*,
  and a live view is held by an entity it has never heard of, so a release whose key count hit zero
  destroyed on-screen pooled instances while `EntityViewRegistry` kept their handles and the entities
  kept an `EntityViewLink` that could never resolve or respawn — views silently gone, no error, no
  recovery, on the ordinary streaming path. `ReleaseChunk` now **refuses**: it returns a
  `ChunkReleaseResult` carrying `LiveViewCount` and a `BlockingKey`, logs a warning, publishes
  `ChunkReleased` with `Released == false`, and changes nothing. Refusal was chosen over deferring
  (reports success for something that did not happen) and over cascading despawns (puts entity
  lifetime decisions in the asset layer, which cannot see entities) because it is the only one whose
  failure mode a consumer can see. Live views on a key another chunk also references do not block —
  that release would not tear the key down. Backed by `ChunkReleaseSafetyTests`.
- **A never-warmed view key no longer defers in silence.** `EntityViewSpawnSystem` counts deferrals
  per key and warns once after 300 frames, so a typo'd key is distinguishable from a slow load. The
  deferral policy itself is unchanged.
- **Views spawn already rotated.** `EntityViewRegistry.Spawn` took the entity's rotation from
  `LocalToWorld` instead of always using identity, which showed one frame of wrong facing.

### Added

- **MessagePipe messaging, without a core dependency.** `Cuvara.DOTS.Runtime` declares two
  one-method interfaces — `IDotsPublisher<T>` and `IDotsSubscriber<T>` — plus four message types
  (`ViewSpawned`, `ViewDespawned`, `ChunkWarmed`, `ChunkReleased`), and never names a MessagePipe
  type. The adapters binding them to MessagePipe's `IPublisher<T>`/`ISubscriber<T>` live in
  `Cuvara.DOTS.DI` behind a `versionDefines` gate on `com.cysharp.messagepipe`. **With MessagePipe
  absent, publishing is a no-op via `NullDotsPublisher<T>` — that is the documented behaviour, and no
  signal bus is written to fill the gap.** Nothing inside the package subscribes; the messages exist
  for consumers.
- `ILiveViewCounter`, implemented by `EntityViewRegistry`, so the provisioning layer can ask about
  live views without knowing what a view or an entity is.
- `ChunkViewProvisioner.IsChunkLoaded` — the "have the loads finished?" question callers actually
  want.

### Changed

- **Breaking: `Cuvara.DOTS.VContainer` renamed to `Cuvara.DOTS.DI`** (folder `Runtime.VContainer/` →
  `Runtime.DI/`), matching the namespace it already used.
- **Breaking: `IsChunkWarm` renamed to `IsChunkTracked`.** It returns true the instant
  `PrewarmChunkAsync` is called, before anything has loaded, which every caller would read as "loads
  finished". `IsChunkLoaded` is that question.
- **Breaking: `ReleaseChunk` returns `ChunkReleaseResult`, not `bool`**, and `ReleaseAll` returns the
  number of chunks it could not release. A bool collapsed "unknown chunk" and "refused" into the same
  value, which is how a streaming bug hides.
- `RegisterDotsViews()` now owns the `ChunkViewProvisioner` registration and wires the registry in as
  its `ILiveViewCounter`; `RegisterGameFoundationViewProvisioning()` no longer registers one, since
  registering it in both places would silently give whichever ran last.

### Accepted limitations

- **A provisioner built without an `ILiveViewCounter` still releases unconditionally.** The parameter
  is optional so the type stays usable without the view layer; the constructor documents it as unsafe
  for streaming, and a test pins the behaviour so it is a decision rather than an accident.
- **Nothing has been compiled.** The MessagePipe adapters are unexercised — no test assembly declares
  a MessagePipe dependency.

### Added

- **`Samples~/HybridViews` sample ("Hybrid Views"), declared in `package.json`.** A `MonoBehaviour`
  bootstrap that installs the view layer into the default world, a `PrimitiveViewAssetProvider`
  implementing `IViewAssetProvider` over `GameObject.CreatePrimitive` and a `Stack<GameObject>` pool
  — so the sample runs on a bare install with only the four pinned dependencies and proves the
  standalone claim — orbiting entities that make the transform sync visible, a mid-run despawn that
  exercises the recycle path, and a narrated chunk warm/release in which two chunks share a key: the
  shared key survives the first release and is torn down only on the second. A cold key is left
  deferred on purpose so the "invisible for a few frames" behaviour can be seen rather than read
  about. Sample `README.md` states which optional packages are *not* needed and why.
- **A ready-to-play scene for that sample**, `Samples~/HybridViews/Scenes/HybridViewsSample.unity`:
  camera aimed at the origin, directional light, and the bootstrap GameObject with its three view
  definitions already filled in — import, open, press Play. It carries **no render-pipeline-specific
  components** (no `UniversalAdditionalCameraData`, no volume, no URP asset reference) so it opens
  clean under URP, HDRP or the built-in pipeline. `.meta` files with fixed GUIDs ship alongside the
  sample's files, because a scene that references a script by GUID breaks if the GUID is regenerated
  at import time.
- **The hazard this sample surfaced** — releasing a chunk whose entities are still alive used to
  destroy those views through `IViewAssetProvider.Release` while the `EntityViewRegistry` kept their
  handles and the entities kept an `EntityViewLink` that could never resolve or respawn. Writing the
  sample is what made it visible; it is **fixed in this same version** (see *Fixed* above) and
  replaced by the cascade in 0.6.0. Kept here only as the origin of that fix.
- Sample updated for the 0.4.0 transform change: entities created from code now add `LocalToWorld`
  explicitly (`TransformSystemGroup` writes into it but does not add it — baking would, runtime
  creation does not), and `OrbitMotionSystem` names its group instead of relying on the default.

## [0.4.0] - 2026-08-14

### Added

- **The package's full system group tree**, all positions fixed now so a consumer's `[UpdateAfter]`
  resolves today and does not change meaning as systems land:
  `NetcodeSystemGroup` → `ProvisioningSystemGroup` in `InitializationSystemGroup`;
  `GameplaySystemGroup` (`UpdateBefore(TransformSystemGroup)`) containing `MovementSystemGroup`,
  `LifecycleSystemGroup` and `DotsEndSimulationCommandBufferSystem` (`OrderLast`);
  `ViewSystemGroup` in `PresentationSystemGroup`. Groups other than the view group are empty in this
  version.
- `DotsEndSimulationCommandBufferSystem` — the package's own ECB, played back at the end of
  `GameplaySystemGroup` and therefore *before* `TransformSystemGroup` and long before presentation.
  Unity's `EndSimulationEntityCommandBufferSystem` plays back after the transform systems, which
  would leave a window in which a view could be synced against an entity that died this frame.
- `DotsViewBootstrap.InstallSystems(World)`, called by `Install`.

### Changed

- **Breaking: the view systems moved into `ViewSystemGroup` and became `internal`.** The group tree
  is the ordering contract; a public system name is an accidental API promise. `InternalsVisibleTo`
  grants access to the two test assemblies only.
- **Breaking: transform sync reads `LocalToWorld`, not `LocalTransform`.** `LocalTransform` is
  relative to the parent, so a parented entity's view was placed at local coordinates — correct for
  root entities and wrong for everything else. `LocalToWorld` is what `TransformSystemGroup` computed
  earlier in the same frame and is right in both cases. Uniform scale is recovered as the length of
  the matrix's first basis vector; non-uniform and sheared transforms are not represented.
- **Breaking: every package system and group is `[DisableAutoCreation]`** and created explicitly by
  `DotsViewBootstrap`. Unity's default bootstrap creates every non-disabled system in *every* world,
  so a multi-world setup would have had two view groups driving one `EntityViewRegistry` and
  double-spawning every entity.
- Spawn now runs before despawn (was despawn-first in 0.2.0), matching the agreed tree.
- Group names dropped their `Cuvara` prefix — `Cuvara.DOTS.*` already scopes them. `Dots` is kept on
  the ECB system precisely so it cannot be mistaken for Unity's.

### Resolved

- **The open question about optional asmdef references is answered: Unity drops a name-based
  reference to an assembly that does not exist.** Verified locally rather than assumed — four
  installed `UniT.*.DI` asmdefs in the consuming project reference `"Zenject"`, `com.svermeulen.extenject`
  is not in the project, and it compiles. Those assemblies are not even excluded by `defineConstraints`,
  so this covers the stronger case. `Cuvara.DOTS.GameFoundation`'s references to `UniT.*` / `UniTask` /
  `VContainer` and `Cuvara.DOTS.VContainer`'s reference to `Cuvara.DOTS.GameLogic` are therefore safe
  when those packages are absent, and the GUID-reference fallback is not needed.

### Accepted limitations

- **`OrderFirst` is not used where a sibling's `UpdateAfter` already encodes the same order.**
  Entities sorts `OrderFirst` members into a separate batch and then drops ordering relations between
  that batch and normal members, with only a warning — so the explicit relation is the one that holds.
  Ordering is unchanged from the agreed tree; only the mechanism differs. `OrderLast` is kept on the
  ECB system, which has no sibling relation to conflict with.
- **No `FixedStepSimulationSystemGroup`.** The server is authoritative and the client integrates
  nothing; a fixed 60 Hz group would re-time server-paced data for no determinism gain. It changes
  when prediction lands, and the rate will derive from the server tick rate.
- **Nothing has been compiled.** The `DotsEndSimulationCommandBufferSystem` singleton boilerplate is
  the highest-risk item — it is unsafe code against an API whose exact shape varies by Entities version.

## [0.3.0] - 2026-08-14

### Added

- **Optional `Shared.GameLogic` seam.** `ISimulationModel` plus the value types it speaks in
  (`SimEntity`, `SimBounds`, `SimConstants`, `SimMoveResult`) live in `Cuvara.DOTS.Runtime` with no
  define guards at all. The package owns the abstraction; `Shared.GameLogic` is one implementation
  behind it and never the interface. Consumer code is byte-identical whether or not
  `com.rpgmmo.shared-gamelogic` is installed.
- **`Cuvara.DOTS.GameLogic`** — optional assembly holding `SharedGameLogicSimulation`, which
  delegates movement to `MovementSystem.TryMove` and combat to `CombatLogic.CalculateDamage` /
  `InRange`, and the `Vec2`↔`float2` / `SimBounds`→`MapBounds` conversions. All conversion lives
  here because it can only live here: `Shared.GameLogic.asmdef` is `noEngineReferences: true` and
  can never learn what a `float2` is. Gated by `versionDefines` + `defineConstraints` on
  `com.rpgmmo.shared-gamelogic`, so it is not compiled at all when the package is absent.
- **`PassiveSimulationModel`** — the absent-dependency path. It applies authoritative state and
  predicts nothing, reporting `IsAuthoritative == false` and `SimMoveResult.Unavailable`.
- `RegisterSimulationModel()` in `Cuvara.DOTS.VContainer`, holding the **single `#if`** that decides
  between the two implementations, driven by the asmdef `versionDefine` rather than a hand-set
  Player Settings define — the `GDK_VCONTAINER` pattern from `com.gdk.core`.
- Tests in a new `Cuvara.DOTS.Tests.GameLogic` assembly, itself constrained on
  `CUVARA_SHARED_GAMELOGIC` so it only compiles when the dependency is present: field-by-field
  parity of `SimConstants` against `GameConstants`, and the shared `movement.json` golden vectors
  replayed **through the seam** and compared bit-for-bit.

### Changed

- `package.json` bumped to 0.3.0. Pinned Unity dependencies unchanged — `com.rpgmmo.shared-gamelogic`
  is an optional integration, not a dependency.

### Accepted limitations

- **`PassiveSimulationModel` refuses rather than approximates.** It returns 0 damage, `false` for
  `InRange` and an unchanged position. It does not re-derive the server's movement rule: that rule
  is not "position += direction * speed * dt" — `MovementSystem.Integrate` splits the multiply into
  separate float locals to deny an FMA contraction, and `Vec2.SqrMagnitude` casts every intermediate
  because C# permits higher-precision evaluation and .NET's RyuJIT and Unity's Mono JIT choose
  differently. A re-implementation would be one ULP wrong and drift silently. Callers must check
  `IsAuthoritative`.
- **`SimConstants.Unavailable` is all zeros**, not plausible defaults. With the shared package absent
  there is no source of truth, and inventing one is the literal-copy trap in disguise.
- **`SimEntity` carries no identity.** `EntityState.Id` / `.Type` are `string` and therefore unusable
  in Burst or an `IComponentData`; identity stays ECS-side as a `FixedString64Bytes`. `SnapshotMerger`
  (netcode owns it) and `ValidationLogic` (managed delegate over string keys, server-shaped) are
  deliberately not exposed.
- **The DI assembly is still named `Cuvara.DOTS.VContainer`**, not `Cuvara.DOTS.DI`, since it already
  shipped in 0.2.0 — its root namespace is `Cuvara.DOTS.DI` and the registration lives there.
- **Nothing here has been compiled.** No Unity Editor was available.

## [0.2.0] - 2026-08-14

### Added

- **Chunk-aware view provisioning.** `IViewAssetProvider` is the one seam the view layer has onto
  asset loading and pooling; `ChunkViewProvisioner` warms and releases whole key sets on behalf of a
  spatial chunk or region, reference-counting keys so two chunks sharing a prefab cannot unload it
  from under each other. A key counts once per chunk regardless of how many times the chunk lists
  it, releasing an unknown or already-released chunk is a no-op, and re-warming an existing chunk
  diffs — a key in both the old and new set never transiently reaches zero.
- **`Cuvara.DOTS.GameFoundation`** — optional adapter implementing `IViewAssetProvider` over the
  GameFoundation / UniT `IAssetsManager` + `IObjectPoolManager` pair. No loader, cache or pool of its
  own: a second pool over the same prefabs would fight the first over recycling.
- **Hybrid entity↔GameObject views.** `EntityViewRequest` → `EntityViewLink` (+ the
  `EntityViewLinkCleanup` cleanup component), a managed `EntityViewRegistry` side-table reached from
  `ISystem` structs through the managed `EntityViewRegistryReference` singleton, and three systems in
  `PresentationSystemGroup`: spawn, despawn and per-frame `LocalTransform` → `Transform` sync.
- **`Cuvara.DOTS.VContainer`** — optional `RegisterDotsViews()` registration extension, mirroring
  `GameFoundationVContainer.RegisterGameFoundation`. The caller supplies the `IViewAssetProvider`.
- **Explicit system group hierarchy.** `CuvaraViewPresentationGroup` (in `PresentationSystemGroup`)
  contains `CuvaraViewLifecycleGroup` (despawn, then spawn) and `CuvaraViewTransformSyncGroup`
  (`UpdateAfter` the lifecycle group). No package system sits in a default group or relies on
  implicit creation order, and consumers order their own systems against the package groups rather
  than against individual systems.
- Tests: reference-count semantics of the chunk provisioner (edit mode), the entity→view
  spawn/despawn/sync lifecycle against an isolated `World` (play mode), and reflection assertions
  on the group attributes (edit mode) — a misplaced `[UpdateInGroup]` never fails a build and shows
  up only as views trailing the simulation by a frame.

### Changed

- `package.json` bumped to 0.2.0. Its four pinned Unity dependencies are unchanged — VContainer,
  UniTask and GameFoundation/UniT are **not** dependencies, only optional integrations.

### Removed

- The `PackageMarker` placeholder in `Runtime/` and both placeholder smoke tests, now that those
  assemblies hold real code. `Editor/PackageMarkerEditor.cs` stays: `Cuvara.DOTS.Editor` still has
  no real code, and an assembly definition over an empty folder produces no assembly.

### Accepted limitations

- **Warm counts only grow.** If chunk A warms 8 instances of a key and chunk B warms 2, releasing A
  leaves 8 warm. Shrinking would destroy pooled instances a live chunk may be about to spawn, which
  is the hitch prewarming exists to avoid. Memory returns when the count reaches zero.
- **The sync split is structural, not a measured win.** The collect half is an `IJobEntity` writing
  blittable samples; the apply half is a flat main-thread loop, because `Transform` cannot be touched
  off the main thread or Bursted. This has not been profiled — at low view counts the scheduling
  overhead may cost more than it saves. The `Complete()` before the drain is a sync point every frame.
- **Cold keys defer rather than load.** An entity whose view prefab has not been warmed stays
  invisible for a few frames instead of forcing a synchronous load. The hitch belongs in the chunk
  prewarm, where it is asynchronous and expected.
- **View handles are never reused.** A recycled id would let a stale `EntityViewLink` address someone
  else's view — a bug that reads as a rendering glitch. Wrap-around at `int.MaxValue` is not defended.
- **Nothing here has been compiled.** No Unity Editor was available; see the 0.2.0 notes in the
  commit message for what the first compile is most likely to catch.

## [0.1.0] - 2026-08-14

### Added

- Initial package scaffold. No runtime gameplay code — assemblies and metadata only.
- `package.json` declaring `com.cuvara.dots` for Unity 6000.3, MIT licensed, depending on
  `com.unity.entities` 1.4.8, `com.unity.burst` 1.8.30, `com.unity.collections` 2.6.8 and
  `com.unity.mathematics` 1.3.2 — the versions already used by the consuming Unity project.
- Four assembly definitions: `Cuvara.DOTS.Runtime`, editor-only `Cuvara.DOTS.Editor`, and the
  Unity Test Framework assemblies `Cuvara.DOTS.Tests.Runtime` (play mode) and
  `Cuvara.DOTS.Tests.Editor` (edit mode), the latter two gated on `UNITY_INCLUDE_TESTS`.
- Placeholder `PackageMarker` types in each assembly folder. Unity produces no assembly for an
  assembly definition whose folder holds no C# file, which would break every reference to it;
  the markers keep the graph resolvable until real code lands.
- Smoke tests in both test assemblies asserting the runtime assembly is referenceable.
- `README.md`, `CHANGELOG.md`, MIT `LICENSE` and a Unity-package `.gitignore`.
