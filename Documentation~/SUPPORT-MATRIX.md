# Support matrix

What is in `com.cuvara.dots` **as of the source on this branch (0.27.1 + `feat/matrix-events`)**,
classified by how far each feature actually goes. Written from the tree, cross-checked against the
consuming client (`IndieRPGMMOAdventure`, Unity 6000.3.9f1, manifest pin
`https://github.com/Cuvara/UnityDots.git#v0.27.1`) and the CI workflow. Where an earlier README,
ROADMAP or CHANGELOG line says more than the source does, **the source wins and the line is
corrected here**.

Two things this file separates on purpose:

- **Source presence vs. runtime acceptance.** A type existing in `Runtime/` is not the same as a
  system that is installed, ticked and observed; a test compiling is not the same as a test running
  in a given configuration. Each row says which.
- **Network presence vs. visual presence.** A replicated entity being *present* (a mirror entity
  exists) and its view being *acquired* (a GameObject is linked) are different lifecycles with
  different events. See `NETWORK-LIFECYCLE.md`.

## 1. Classification key

| Class | Meaning |
|---|---|
| **implemented** | Code exists, is installed by a documented entry point, and has at least one test or integration example that exercises it at runtime |
| **integrated in client** | The client project has a live call site (`Assets/Scripts/DI/Dots/*`) — not merely the assembly compiled |
| **sample-only** | Exists only under `Samples~/`; copied into a consumer, never part of the installed package assemblies |
| **data-contract-only** | A struct/component/singleton type with **no producer or no consumer in the package** — safe to reference, does nothing on its own |
| **planned** | Named in a roadmap or changelog; no code in the tree |
| **in progress** | Being changed concurrently on a sibling branch; the description here is the 0.27.1 behaviour |

## 2. Feature matrix

| Feature | Class | Where | Tests | Client |
|---|---|---|---|---|
| Entity↔view link, spawn/despawn, transform sync (`EntityViewRequest` → `EntityViewLink`, registry, 3 systems) | implemented, integrated in client | `Runtime/Views` | PlayMode `EntityViewLifecycleTests`, `ViewConfigSpawnTests`; EditMode `SweepDestroyedTests`, `ViewSystemGroupLayoutTests` | `RegisterDotsViews` → `DotsViewBootstrap.Install` at root scope |
| View configuration as data (`ViewConfig`, `ViewArchetypeLibrary`, `ViewConfigCatalog`, `ViewConfigRef`, blob table) | implemented, integrated in client | `Runtime/Configuration` | `ViewConfigCatalogTests`, `ViewConfigSpawnTests` | catalog built in code in `DotsWorldBridge` |
| `ViewSortingKey` (2D sorting layer/order) | **data-contract-only** | `Runtime/Configuration/ViewSortingKey.cs` | none | not used | 
| Chunk-aware provisioning (`ChunkViewProvisioner`, refcounted keys, cascade release, `ChunkState`) | implemented; **in progress on `feat/pool-chunk` (D03)** | `Runtime/Provisioning` | `ChunkViewProvisionerTests`, `ChunkReleaseSafetyTests`, `ChunkStateTrackingTests`, PlayMode `EntityViewCascadeTests` | registered by `RegisterDotsViews`; **no client call site** for `PrewarmChunkAsync`/`ReleaseChunk` |
| `PooledViewAssetProvider` (SetActive pooling) | implemented; **in progress on `feat/pool-chunk` (D02)** | `Runtime/Provisioning/PooledViewAssetProvider.cs` | `PooledViewAssetProviderTests` (12) | not used — client ships its own `PrimitiveViewAssetProvider` |
| `IViewAssetProvider` seam | implemented, integrated in client | `Runtime/Provisioning/IViewAssetProvider.cs` | every view test via a fake | client implements it (`Assets/Scripts/DI/Dots/PrimitiveViewAssetProvider.cs`) |
| GameFoundation provider (`GameFoundationViewAssetProvider`, `RegisterGameFoundationViewProvisioning`) | implemented, **compile-checked only** | `Runtime.GameFoundation` | none — no test assembly references it | not used (client chose primitives; see its provider's remarks) |
| Simulation components/systems (`TimeToLive`, `Health`, `MoveToward`, `MoveData`+bounce, `SpinSpeed`) | implemented, integrated in client | `Runtime/Simulation` | PlayMode `SimulationSystemsTests`, `ParallelSchedulingBenchmark` | `DotsSimulationBootstrap.InstallSimulationSystems` in `DotsWorldBridge`; `Health`/`TimeToLive` referenced |
| `ISimulationModel` seam over `Shared.GameLogic` | implemented, integrated in client | `Runtime/Simulation/ISimulationModel.cs`, `Runtime.GameLogic` | `SimConstantsParityTests`, `SimulationSeamGoldenVectorTests` (41) | `RegisterSimulationModel` |
| `EntityArchetypePreset` + `ArchetypeFactory` | implemented; **in progress on `feat/bootstrap-config` (D05)** | `Runtime/Configuration` | `ArchetypeFactoryTests` (8) | not used |
| Messaging seam (`IDotsPublisher`/`IDotsSubscriber`, `ViewSpawned`, `ViewDespawned`, `ChunkWarmed`, `ChunkReleased`, `ChunkCascadeReleased`) | implemented | `Runtime/Messaging` | `RecordingViewAssetProvider`-based tests observe `ViewSpawned`/`ViewDespawned` | client registers MessagePipe brokers for all five |
| MessagePipe adapters (`RegisterDotsMessaging`) | implemented, **compile-checked only** | `Runtime.DI` | none (no test assembly references `Cuvara.DOTS.DI`) | `RegisterDotsMessaging` called |
| Netcode adapter (`DotsEntityView`, drain system, `NetworkEntity*` components, `TypeArchetypeResolver`) | implemented, integrated in client | `Runtime.Netcode` | `NetworkEntityViewTests` (24), `TypeArchetypeResolverTests`, `SnapshotSpaceMappingTests`, `NetcodeSystemLayoutTests` | `DotsNetcodeBootstrap.Install` + `WorldViewBinder` in `DotsWorldBridge` |
| ECS remote interpolation (`SetStateAtTick`, `SnapshotSample`, `RemoteInterpolationSystem`, clock) | implemented, integrated in client | `Runtime.Netcode` | `RemoteInterpolationTests` (8) | `SetStateAtTick` called from the snapshot handler |
| Client-side prediction driver (`LocalPredictionSystem`, `PredictedTransform`, `ReconciliationAnchor`) | implemented, integrated in client | `Runtime.Netcode.Prediction` | `LocalPredictionSystemTests`, `PredictionGroupLayoutTests` (19) | `DotsPredictionBootstrap.Install` |
| **Network lifecycle events** (`NetworkEntitySpawned`/`Despawned`, `NetworkEntityLifecycle`, `Uninstall(destroyMirrors)`) | **implemented on this branch** — was data-contract-only in 0.27.1 (declared, never published) | `Runtime.Netcode/EntityLifecycleEvents.cs`, `NetworkEntityLifecycle.cs`, drain | `NetworkLifecycleEventTests` (15), `NetworkEntityLifecycleTests` (9) | not yet — consumer is `Samples~/NetworkedPrediction/NetworkLifecycleLog.cs`; DI wiring `RegisterDotsNetworkLifecycle` |
| View overlay anchors (`ViewOverlayAnchor`, `ViewOverlayBuffer`, `ViewOverlaySystem`) | implemented **producer**, no package consumer; host owns world-to-screen | `Runtime/Views` | `ViewOverlayAnchorTests` (data only); system untested | not used |
| Minimap (`MinimapEntry`, `MinimapBuffer`) | **data-contract-only** — no producer; the `MinimapDataSystem` named in earlier docs does not exist | `Runtime/Views/Minimap*.cs` | none | not used |
| Camera follow (`CameraFollowSystem`, `CameraFollowConfig`, `CameraFollowTarget`) | implemented system, **no installer** (`[DisableAutoCreation]`, not created by any bootstrap); `Camera.main` only; **in progress on `feat/bootstrap-config` (D04/D09)** | `Runtime/Views/CameraFollow*.cs` | none | not used |
| Physics helpers (`PhysicsBodyFactory`, `SpatialQuery`) | implemented, **untested, not in CI** (no CI row installs `com.unity.physics`) | `Runtime.Physics` | none | compiles in client (physics 1.4.7 present); no call site |
| `PhysicsMovementBridge` | implemented system, **no installer**; **in progress on `feat/bootstrap-config` (D04)** | `Runtime.Physics` | none | not used |
| Collision/trigger events (`EntityCollision`, `EntityTriggerEvent`) | **data-contract-only** — the `CollisionEventSystem` the 0.26.0 changelog lists **does not exist in the source**; identity is `Entity.Index` only (no version) | `Runtime.Physics` | none | not used |
| Editor debug window (`EntityViewDebugWindow`) | implemented, untested | `Editor` | none | available |
| Hybrid Views sample (scene, `OrbitMotionSystem`, sample `PrimitiveViewAssetProvider`) | sample-only; **compiled in every CI row**, not run | `Samples~/HybridViews` | compile gate only | — |
| Networked Prediction sample (+ lifecycle log) | sample-only; compiled in the netcode CI row, runs only against a live backend | `Samples~/NetworkedPrediction` | compile gate only | — |
| Stress Benchmark sample | sample-only; **excluded from CI import**; the "100 → 100M entities" ramp is a *configured tier list*, not a measured result | `Samples~/StressBenchmark` | none | — |
| 2D tile data / LOS queries; `SpriteRenderer` sorting application | planned | — | — | — |
| Minimap producer; collision/trigger collector; camera/physics installers; culling/LOD; spawn budget | planned (D04, D07, D08, D09, E01, E02) | — | — | — |

## 3. Module matrix

For every assembly: what it needs, how it is installed, where it runs, which singletons it requires
or publishes, who owns what, and how it is torn down.

### `Cuvara.DOTS.Runtime` — core

| | |
|---|---|
| Dependencies | `com.unity.entities` 1.4.8, `com.unity.burst` 1.8.30, `com.unity.collections` 2.6.8, `com.unity.mathematics` 1.3.2 (+ `Unity.Transforms` from Entities). Nothing else, and CI's `no optional packages` row proves it. |
| Installation | `DotsViewBootstrap.Install(world, registry)` — publishes `EntityViewRegistryReference`, creates the whole group tree and the view systems. `DotsSimulationBootstrap.InstallSimulationSystems(world)` — simulation systems, independent of views. `ViewConfigCatalog.Build(library)` then `catalog.Install(world)` — publishes `ViewConfigTableReference`. All three are idempotent. |
| Update groups | `NetcodeSystemGroup` › `SnapshotApplyGroup`, `PredictionSystemGroup`; `ProvisioningSystemGroup` (empty) — in `InitializationSystemGroup`. `GameplaySystemGroup` › `MovementSystemGroup`, `LifecycleSystemGroup`, `DotsEndSimulationCommandBufferSystem` — in `SimulationSystemGroup`, before `TransformSystemGroup`. `ViewSystemGroup` › `ViewInterpolationGroup`, `ViewLifecycleGroup` (despawn → spawn), `ViewTransformSyncGroup` (sync → overlay) — in `PresentationSystemGroup`. Every group and system is `[DisableAutoCreation]`; the groups are the public ordering contract, the systems are `internal`. |
| Singletons | Requires: `EntityViewRegistryReference` (managed; every view system `RequireForUpdate`s it). Optional: `ViewConfigTableReference` (bare-key path works without it). Published lazily: `ViewOverlayBuffer` (managed, created by `ViewOverlaySystem` on first update; native list disposed in its `OnDestroy`). Consumer-published: `CameraFollowConfig` (managed) — read only if the consumer also creates `CameraFollowSystem`, which no bootstrap does. Nobody publishes `MinimapBuffer`. |
| Ownership | `EntityViewRegistry` is owned by whoever constructed it (the DI root scope in the client); it owns the handle → GameObject table, never the pool. `IViewAssetProvider` owns instances and prefabs. `ChunkViewProvisioner` owns per-chunk key refcounts and must be given an `IViewCascadeSink` (`EntityViewCascade`) or it will release assets under live views. Mirror/simulation entities belong to the world. |
| Teardown | `DotsViewBootstrap.Uninstall(world)` → `registry.Clear()` (recycles every live view, publishes `ViewDespawned` per view) and destroys the singleton; **systems stay in the world** and idle. `catalog.Dispose()` releases the blob. `world.Dispose()` runs `ViewOverlaySystem.OnDestroy`, which disposes the overlay list. There is no uninstall for the simulation systems; dispose the world. |

### `Cuvara.DOTS.Netcode` — snapshot adapter

| | |
|---|---|
| Dependencies | core + `com.cuvara.netcode` **≥ 0.31.0** (`versionDefines` → `CUVARA_NETCODE`; absent → assembly compiled out). Floor was 0.19.0 through 0.27.1; raised here because 0.31.0 is the release the client is adopting and the one CI pins. |
| Installation | `DotsNetcodeBootstrap.Install(world, view, interpolation = default)` after `DotsViewBootstrap.Install`. Publishes `NetworkEntityViewReference` (managed), `InterpolationSettings` + `InterpolationTimeline` (blittable, one entity); creates the drain in `SnapshotApplyGroup` and the two interpolation systems in `ViewInterpolationGroup`. Idempotent: a second call replaces the referenced view and re-seeds settings; the timeline is never reset. |
| Update groups | Drain: `SnapshotApplyGroup` (Initialization). Interpolation clock + evaluator: `ViewInterpolationGroup` (Presentation, before `ViewLifecycleGroup`). |
| Singletons | Requires `NetworkEntityViewReference` (drain), `InterpolationSettings`/`InterpolationTimeline` (interpolation). |
| Ownership | `DotsEntityView` is the consumer's (session-scoped in the client). The drain owns the id → entity map — the single source of truth for network presence — and the mirror entities it creates. `DotsEntityView.Lifecycle` (`NetworkEntityLifecycle`) is owned by the view; pass a container-owned one to the constructor when using DI. |
| Teardown | `DotsNetcodeBootstrap.Uninstall(world)` removes the three singletons and **leaves mirror entities and the drain's map in place** (0.27.1 behaviour). `Uninstall(world, destroyMirrors: true)` additionally publishes one `NetworkEntityDespawned(Teardown)` per present id, destroys every `NetworkEntity` entity, and empties the map. Contract: `NETWORK-LIFECYCLE.md`. |

### `Cuvara.DOTS.Netcode.Prediction` — prediction driver

| | |
|---|---|
| Dependencies | core + `Cuvara.DOTS.Netcode` + `com.cuvara.netcode` ≥ 0.31.0 + `com.rpgmmo.shared-gamelogic` (both defines required). |
| Installation | `DotsPredictionBootstrap.Install(world, predictor, worldState)` after the adapter. Publishes `LocalPredictionReference` (managed); creates `LocalPredictionSystem` in `PredictionSystemGroup`. |
| Update groups | `PredictionSystemGroup`, after `SnapshotApplyGroup`, inside `NetcodeSystemGroup`. |
| Singletons | Requires `LocalPredictionReference`. Claims the local entity's `LocalTransform` by adding `PredictedTransform`. |
| Ownership | The `LocalMovePredictor` instance is the composition root's — the same object input calls `RecordInput` on. |
| Teardown | `DotsPredictionBootstrap.Uninstall(world)` removes `PredictedTransform` from every entity (hands the transform back to the adapter) and destroys the singleton. Call it **before** `DotsNetcodeBootstrap.Uninstall`. |

### `Cuvara.DOTS.GameLogic` — `ISimulationModel` over `Shared.GameLogic`

| | |
|---|---|
| Dependencies | core + `com.rpgmmo.shared-gamelogic` (`CUVARA_SHARED_GAMELOGIC`). |
| Installation | Construct `SharedGameLogicSimulation`, or `RegisterSimulationModel()` with DI. No systems, no singletons, no teardown. |

### `Cuvara.DOTS.DI` — VContainer registrations

| | |
|---|---|
| Dependencies | core + `jp.hadashikick.vcontainer` (`CUVARA_DOTS_VCONTAINER`). Optional, ignored when compiled out: `Cuvara.DOTS.GameLogic`, `Cuvara.DOTS.Netcode`, `MessagePipe`(+`.VContainer`, `CUVARA_DOTS_MESSAGEPIPE`), `UniTask`. |
| API | `RegisterDotsMessaging()` — publishers/subscribers for the five view/chunk messages (MessagePipe adapters or `NullDotsPublisher`). `RegisterDotsViews(viewRoot, world)` — registry, cascade sink, provisioner, and a build callback that runs `DotsViewBootstrap.Install`. `RegisterSimulationModel()`. **New:** `RegisterDotsNetworkLifecycle()` — singleton `NetworkEntityLifecycle`, forwarding into MessagePipe when present, itself registered as `IDotsSubscriber<>` when not; requires `CUVARA_NETCODE`. |
| Ownership / teardown | Root scope owns the registry and provisioner (they outlive scenes); the world defaults to `World.DefaultGameObjectInjectionWorld`. Nothing here disposes the world. Test coverage: **none** — compile-checked in the client only. |

### `Cuvara.DOTS.GameFoundation` — UniT-backed provider

| | |
|---|---|
| Dependencies | core + `com.frostbun.unit.pooling`, `com.frostbun.unit.resourcemanagement`, `com.cysharp.unitask`; the VContainer extension additionally needs `jp.hadashikick.vcontainer`. |
| API | `GameFoundationViewAssetProvider : IViewAssetProvider`; `RegisterGameFoundationViewProvisioning()` (call after `RegisterGameFoundation`). Does **not** register a provisioner — `RegisterDotsViews` owns that. |
| Status | Compile-checked only; no test assembly and no CI row installs UniT. The client deliberately uses primitives instead. |

### `Cuvara.DOTS.Physics` — optional Unity.Physics helpers

| | |
|---|---|
| Dependencies | core + `com.unity.physics` ≥ 1.0.0 (`CUVARA_DOTS_PHYSICS`); `autoReferenced: false`, so a consumer asmdef must reference it explicitly. |
| Present | `PhysicsBodyFactory` (dynamic/static/kinematic bodies from `ColliderShape`; collider blobs are created per call and **never disposed by the package**), `SpatialQuery` (overlap/raycast/closest over `CollisionWorld`), `PhysicsMovementBridge` (`MoveData.Velocity` → `PhysicsVelocity.Linear`, `[DisableAutoCreation]`, **no installer**). |
| Absent | Any collision/trigger collector. `EntityCollision`/`EntityTriggerEvent` are unproduced structs carrying `Entity.Index` without `Version`. The 0.26.0 changelog's "CollisionEventSystem" is corrected below. |
| Tests / CI | None. No CI row installs `com.unity.physics`. Runtime acceptance: **none recorded**. |

### `Cuvara.DOTS.Editor`

`EntityViewDebugWindow` (Window › Cuvara › DOTS View Debug). Reads the default world's registry.
Untested; the chunk-provisioner panel is a placeholder that tells you to expose the provisioner yourself.

## 4. Tested configurations

Source of truth: `.github/workflows/ci.yml` and `.github/scripts/assert_test_floors.py`. Every row
runs Unity **6000.3.9f1** on `ubuntu-latest` via `game-ci/unity-test-runner`, `testMode: all`
(EditMode + PlayMode), Mono scripting backend, and asserts a **test-count floor per assembly** —
a green run over zero tests fails.

| CI row | Manifest | Adapter assemblies | Floors asserted |
|---|---|---|---|
| **no optional packages** | four Unity pins only | `Cuvara.DOTS.Netcode`, `.Netcode.Prediction`, `.GameLogic` must be **absent** | Editor ≥ 30, Runtime ≥ 29, GameLogic == 0, Netcode == 0, Prediction == 0 |
| **netcode absent** | + `com.rpgmmo.shared-gamelogic` `sgl-v0.3.0` | `Cuvara.DOTS.Netcode` must be absent | Editor ≥ 30, Runtime ≥ 29, GameLogic ≥ 41, Netcode == 0, Prediction == 0 |
| **netcode present** | + `com.cuvara.netcode` **`v0.31.0`** + `sgl-v0.3.0` + OpenUPM scope (UniTask, VContainer, NuGet for netcode's own needs) | `Cuvara.DOTS.Netcode` must be present | Editor ≥ 30, Runtime ≥ 29, GameLogic ≥ 41, Netcode ≥ 47, Prediction ≥ 19 |

Samples: `HybridViews` must compile in every row; `NetworkedPrediction` must compile in the
netcode row and must be **absent** in the other two (its `defineConstraints` are under test);
`StressBenchmark` is not imported by CI at all.

**Not tested anywhere in CI:** `Cuvara.DOTS.DI` (no VContainer row), `Cuvara.DOTS.GameFoundation`
(no UniT row), `Cuvara.DOTS.Physics` (no `com.unity.physics` row), `Cuvara.DOTS.Editor`,
`MessagePipe` forwarding. These compile in the client project, which has VContainer, MessagePipe,
UniT and `com.unity.physics` 1.4.7 installed — that is compile evidence, not runtime acceptance.

## 5. Platforms

| Platform | Evidence | Status |
|---|---|---|
| Editor (Linux, Mono) | CI rows above | tests pass at the floors listed |
| Editor (Windows, Mono) | the client project's Editor | runs; no recorded test artefact |
| Standalone Windows / Linux (Mono2x, no stripping) | client CI builds | compiles; exercises neither IL2CPP nor the stripper — see `ROADMAP.md › Measurement caveat` |
| Android (IL2CPP) | none | **not verified**; required before any AOT/Burst/performance claim |
| WebGL | none | **not verified**; blocked upstream on netcode's browser transport regardless |

## 6. Performance claims

There are **no measured performance figures in this package's documentation**, and the following
must not be read as such:

- `Samples~/StressBenchmark/README.md`'s "100 → 1K → 10K → 1M → 10M → 100M entities" is the
  benchmark's *tier configuration*. The `[STRESS-BENCH]` line under it is a format example. No result
  from any device is recorded in this repository.
- `ParallelSchedulingBenchmark` (PlayMode) is a per-system crossover measurement that tunes
  `ParallelScheduling` thresholds; it is not a frame budget.
- The "~100 ms behind" figure in `NETCODE-INTEGRATION.md` is netcode's default `TargetDelay`
  configuration, not a measurement.

Capacity and budget numbers will appear here only with device, OS, Unity/package commits, backend
image, workload and p50/p95/p99 attached — the record format in the improvement plan, §10.

## 7. Corrections to earlier statements

| Where | Said | Actually |
|---|---|---|
| `CHANGELOG.md` 0.26.0 | Runtime.Physics ships a `CollisionEventSystem` | No such type exists in the source at 0.26.0, 0.27.1 or on this branch. `EntityCollision`/`EntityTriggerEvent` have no producer. |
| `Runtime/Views/MinimapBuffer.cs` (≤ 0.27.1) | "Populated by `MinimapDataSystem`" | No such system exists. The comment now says so. |
| `CHANGELOG.md` 0.27.0 | "NetworkEntitySpawned/Despawned lifecycle events" (Added) | 0.27.0 added the two structs and nothing published them. Publishing arrives with this branch. |
| `README.md` (≤ 0.27.1) | install URL `com.cuvara.dots.git#v0.6.2`; "Not yet compiled against a Unity Editor" | The repository is `https://github.com/Cuvara/UnityDots.git`, the current tag is `v0.27.1`, and it has been compiled and tested in CI since 0.10.0. |
| `README.md` / `Documentation~` (≤ 0.27.1) | netcode ≥ 0.19.0 | ≥ 0.31.0 from this branch. |
| `ROADMAP.md` (≤ 0.27.1) | "In progress: nothing"; planned item 1 is the netcode adapter | The adapter shipped in 0.9–0.24; the roadmap had not been updated since 0.7.0. Rewritten. |
| `Documentation~/VIEW-PROVISIONING.md` | `PrimitiveViewAssetProvider` ships in `Runtime/` | It ships in `Samples~/HybridViews` (and a second copy, `PrimitiveViewProvider`, in `Samples~/NetworkedPrediction`). The package's runtime provider is `PooledViewAssetProvider`. |
| `Documentation~/VIEW-PROVISIONING.md` | "Sorting keys control draw order … stable across pool recycles" | `ViewSortingKey` is carried and **not applied** to any renderer; its own source comment says so. |
