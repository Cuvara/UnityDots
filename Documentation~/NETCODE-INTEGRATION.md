# Netcode Integration

How `com.cuvara.dots` integrates with `com.cuvara.netcode` to present replicated
server entities as ECS entities with interpolation and prediction.

## Prerequisites

- `com.cuvara.netcode` >= 0.31.0 (enforced by asmdef `versionDefines`; 0.19.0 through 0.27.1)
- `com.cuvara.dots` installed

When netcode is absent, the `Cuvara.DOTS.Netcode` assembly is not compiled and the
rest of the package works unchanged.

## Setup

```csharp
// 1. Build the archetype resolver — maps server entity types to view archetypes
var resolver = new TypeArchetypeResolver(
    localArchetype: "player-local",
    unknownArchetype: null,  // null = refuse unmapped kinds
    new TypeArchetypeResolver.Rule("player", "player-remote"),
    new TypeArchetypeResolver.Rule("mob", "goblin"));

// 2. Create the ECS entity view
var view = new DotsEntityView(catalog, resolver, SnapshotSpaceMapping.XZPlane);

// 3. Install into the world
DotsNetcodeBootstrap.Install(world, view);

// 4. Wire to the socket consumer
var binder = new WorldViewBinder(view);
// Per frame:
binder.Tick(worldState, networkClient.UserId);
```

## Entity components

Each replicated entity carries:

| Component | Purpose |
|-----------|---------|
| `NetworkEntity` | Wire id, server entity kind, `IsLocal` flag |
| `NetworkEntityState` | Newest authoritative HP from server |
| `ReconciliationAnchor` | Newest authoritative position (world + server space), `Sequence` of states written, stated `Tick` or 0 |
| `SnapshotSample` (buffer) | Buffered positions for remote interpolation |
| `InterpolationState` | What interpolation last drew |
| `LocalTransform` | Current rendered position |
| `EntityViewRequest` + `ViewConfigRef` | View spawn trigger |

## Remote interpolation

Opt-in per entity, controlled by whether a server tick accompanies the state:

- **With tick** (`SetStateAtTick`): state is buffered in `SnapshotSample`;
  `RemoteInterpolationSystem` evaluates `SnapshotInterpolation.Evaluate` from
  netcode's core in a Bursted `IJobEntity`. Remote entities render ~100ms behind
  the newest tick (`TargetDelay`).

- **Without tick** (`SetState`): state is written directly to `LocalTransform`.
  No interpolation, same behaviour as pre-0.24.0.

**The two paths are mutually exclusive per entity.** A ticked state is buffered;
an unticked one is written directly. Never feed both to the same entity.

**Enforced, not just documented.** An untimed `SetState` that reaches an entity already
holding buffered samples writes the anchor and hp but leaves `LocalTransform` to the
interpolation job; the collision is counted in `view.Metrics.MixedPathStates`. A timed
state the ring refuses (duplicate or reordered tick — `InterpolationRing.Accepts` is
netcode's rule) is counted in `RejectedSamples` and never falls back to a direct write.

## Transform ownership

`LocalTransform` has exactly one writer per entity at any moment:

| Entity | Writer | How it is decided |
|--------|--------|-------------------|
| Remote, untimed states | `NetworkViewCommandSystem` (direct write) | no samples buffered, no `PredictedTransform` |
| Remote, timed states | `RemoteInterpolationSystem` | samples buffered; the drain stops writing the transform |
| Local, predicted | `LocalPredictionSystem` | it adds `PredictedTransform`; the drain and the job (`WithNone`) both yield |
| Local, no predictor installed | `NetworkViewCommandSystem` | `PredictedTransform` absent |

`ReconciliationAnchor` is written on every path, from the command only — its sources are
the wire and nothing else. Nothing predicted or rendered ever reaches it.

## Client-side prediction

`DotsPredictionBootstrap.Install(world, predictor, worldState)` drives netcode's
`LocalMovePredictor` from ECS:

- The adapter writes only `ReconciliationAnchor` (authoritative position); the
  prediction system claims `LocalTransform` by adding `PredictedTransform`.
- Remote interpolation excludes entities with `PredictedTransform` (`WithNone`).
- **Reconciliation inputs** (once per merged snapshot, i.e. whenever `WorldState.Tick`
  advanced): the local entity's `WorldState` entry (`X`, `Y`, and under the 3D motor `Z` and
  `VelZ` - the wire's own coordinates, the same floats `anchor.ServerPosition` holds),
  `WorldState.Tick`, `WorldState.AckTick` and `WorldState.AckAppliedTick`, all from that one
  snapshot. Order: `SetServerSpeed`, then the clock (`PredictionClockSteering.SampleTickRate` +
  `OnSnapshot`: seed, measure, feed a corroborated rate forward, steer the phase), then
  `Reconcile(position, ack, tick, ackAppliedTick)`. The predictor compares against its history at
  the entry the snapshot describes (netcode `Documentation~/PREDICTION.md`, *ack_applied_tick*);
  a protocol 2 server sends no `ack_applied_tick` and the comparison is at the snapshot tick, as
  before. Before 0.31.0 this system reconciled through the two-argument overload and never steered
  the clock, which ran 22-23 ticks ahead of a 0.7%-slow server over 55 s.
- **Clock steering**: `LocalPredictionReference.Steering` (created by `Install`). Report each input
  send with `DotsPredictionBootstrap.NoteInputSent(world, inputTick)` beside `RecordInput`, or the
  acknowledgement floor is never measured; `TryGetClockSteering` exposes it to set `RoundTripMs`.
  Time is the DOTS world's `Time.ElapsedTime`, the clock `Advance` is fed from.
- **Fallback**: when the local entity is not in `WorldState` (a view fed by something other than
  the binder), the previous rule applies - reconcile through `Reconcile(anchor.ServerPosition,
  AckTick)` only when `AckTick` advanced *and* `anchor.Sequence` changed since the last reconcile
  and is non-zero, so an ack that outran the drain waits one frame and the spawn placeholder
  (`Sequence == 0`) is never rewound to.
- **Reconnect**: the system's ack/anchor/world-tick memory and the clock measurements reset when
  `DotsEntityView.Generation` changes or `WorldState.Tick` goes backwards, so a new server whose
  ticks are lower still reconciles. Resetting the
  predictor itself (`LocalMovePredictor.Reset()`) remains the host's call, alongside
  `WorldState.Reset()` and `view.BeginGeneration()`.
- Toggling: `DotsPredictionBootstrap.Uninstall` removes `PredictedTransform`, so the
  adapter's next state drives the transform again; a re-install claims it before its
  first write. There is no frame with zero writers in either direction.

## Thread affinity

| Side | Thread | Rule |
|------|--------|------|
| Producer — `Spawn`/`Despawn`/`SetState`/`SetStateAtTick`/`BeginGeneration` | whichever thread ticks `WorldViewBinder` | one thread per generation; latched by the first call, others throw `InvalidOperationException` |
| Consumer — `NetworkViewCommandSystem` | the world's main thread | the only thing that dequeues |

The `ConcurrentQueue` is the only structure that crosses threads and the only one that
has to; the view's `HashSet`/`Dictionary` bookkeeping is protected by the latch, not by
locks. `BeginGeneration` re-opens the latch so a reconnect may hand the view to a new
consumer thread. `view.ProducerThreadId` shows the current latch (0 = unlatched).

## Session generations

```csharp
// On reconnect / map transfer, on the producer thread, before the first new snapshot:
worldState.Reset();
predictor?.Reset();
view.BeginGeneration();
```

`BeginGeneration` forgets every live id, bumps `view.Generation` and enqueues one
`Reset` command. Every command is stamped with the generation it was enqueued under.
The drain:

1. drops any command older than the view's current generation, unapplied
   (`Metrics.StaleCommandsDropped`) — queued old-session states and spawns never touch
   the world, and a stale producer still holding the old binder cannot resurrect anything;
2. on the `Reset`, tears down every mirror of the previous generation with
   `NetworkDespawnReason.SessionReset` (one `NetworkEntityDespawned` per id);
3. applies the new generation's commands.

Old entities therefore never flicker back for a frame, which a burst of despawns from
`WorldViewBinder.Reset` alone cannot promise: those are applied in order, and a state
queued behind them would still resurrect the id until its own despawn arrived.

## Ingestion metrics

`view.Metrics` (`NetworkIngestionMetrics`) is what a host reads into its diagnostics
overlay, and what decides whether the queue ever needs bounding:

| Counter | Meaning |
|---------|---------|
| `PendingCommands` (on the view) | enqueued and not yet drained; zero every frame in health |
| `PendingHighWatermark` | highest pending count ever observed at an enqueue |
| `Drains`, `LastDrainCount`, `Drained` | non-empty drains, size of the last, cumulative |
| `LastDrainSeconds`, `MaxDrainSeconds` | wall time of the drain (also the `Cuvara.DOTS.NetworkViewCommandSystem.Drain` profiler marker) |
| `LastOldestCommandAgeSeconds`, `MaxOldestCommandAgeSeconds` | how long the head of the queue waited for a frame |
| `StaleCommandsDropped` | commands from a reset generation, dropped unapplied |
| `RejectedSamples` | timed states the ring refused (duplicate/reordered tick) |
| `MixedPathStates` | untimed states on an interpolation-owned entity; non-zero means a consumer feeds one id through both methods |
| `GenerationResets` | resets the drain applied |

The queue is unbounded and drained whole every frame, on purpose: a cap would be a
frame-rate-dependent way of losing state (a spawn its state never reached is an entity
at the origin), and the backlog after a keyframe is bounded by the AOI. The
`backlogWarningThreshold` constructor argument (default 4096) logs once per generation
when the pending count reaches it and drops nothing. If these numbers ever show a real
cost on a device, the change is to coalesce **movement/state only** — never lifecycle or
one-shot actions — while preserving spawn → state → despawn order and enough
timestamped samples for interpolation. Not before.

## SnapshotSpaceMapping

Controls how server `(x, y)` maps to Unity world coordinates:

| Mapping | Server (x,y) → Unity |
|---------|---------------------|
| `XZPlane` (default) | x → X, y → Z, Y = 0 |
| `XYPlane` | x → X, y → Y, Z = 0 |

Per-art height offset belongs in `ViewConfig.PositionOffset`, not in the mapping.

## Lifecycle events

Each replicated id publishes `NetworkEntitySpawned` when its mirror entity is created and exactly
one `NetworkEntityDespawned` when it stops being present — wire despawn, external destruction or
teardown, never "died". Subscribe on `view.Lifecycle`; `DotsNetcodeBootstrap.Uninstall(world,
destroyMirrors: true)` ends every remaining life at session end. Full contract, ordering and the
scripted sequences: `NETWORK-LIFECYCLE.md`.

## Minimap

Pass an `IMinimapCategoryResolver` to `DotsEntityView` (`minimap:` argument) and the drain puts a
`MinimapMarker` on every mirror the resolver says yes to; install `MinimapBootstrap` to collect them.
Because only mirrors are marked, the map can show only entities the server replicated. See
`MINIMAP-OVERLAY.md`.

## Important constraints

1. **No interpolation arithmetic in this package.** All interpolation calls go
   through `Cuvara.Netcode.Interpolation.SnapshotInterpolation` — netcode's core.
2. **IEntityView calls enqueue, not write.** The queue is drained by
   `NetworkViewCommandSystem` in `NetcodeSystemGroup` (InitializationSystemGroup).
3. **Wire HP → `NetworkEntityState`, not `Health`.** `Health` means "destroy at
   zero" in this package. Mirroring server HP into it would let a client-side
   system destroy an entity the server still lists. `DotsEntityView(writeHealth: true)`
   is the explicit opt-in, and `Health` is then added only once a real hp value exists.
4. **A despawn is not a death.** The wire does not distinguish an AOI exit from a
   removal; neither does `NetworkEntityDespawned.Reason`.
