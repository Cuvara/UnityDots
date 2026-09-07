# Roadmap

What is in `com.cuvara.dots` today, what is being changed, and what is planned. **Written from the
tree, not from a plan** — an item is Done only if the code exists here, and "Done" says which of
*implemented / integrated in client / sample-only / data-contract-only* it is. The full
per-feature and per-module classification, with tested configurations and platforms, is
`Documentation~/SUPPORT-MATRIX.md`; this file is the summary and the order of work.

**Version labels:** shipped work carries the version it shipped in, matching `package.json` and
`CHANGELOG.md`. Unshipped work carries no version label, only an order.

## Scope

**Hybrid** building blocks: simulation runs in ECS, visuals are GameObject/MonoBehaviour. The
package does not render entities. Consumers may wire it through **VContainer**, but the core has no
DI dependency.

Two rules constrain everything below.

- **Standalone install.** The core resolves and compiles against its four pinned dependencies alone
  — `com.unity.entities`, `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`.
  Anything needing more lives in a separate assembly gated by `versionDefines` +
  `defineConstraints`, and is absent rather than broken when its dependency is. Verified by CI's
  *no optional packages* row and by `Samples~/HybridViews`.
- **Dependency direction.** `com.cuvara.dots` may depend on `com.cuvara.netcode` (≥ 0.31.0). The
  reverse is forbidden, in every release. Netcode's `IEntityView` stays three methods; the adapter
  adds its own entry points (`SetStateAtTick`, `Lifecycle`) beside it rather than widening it.

## Done

| Feature | Shipped in | Class | Contents |
|---|---|---|---|
| Entity↔view link and transform sync | 0.2.0, reworked 0.4.0, sweep 0.26.0 | implemented, in client | `EntityViewRequest` → `EntityViewLink` (+ cleanup), managed `EntityViewRegistry`, spawn/despawn/sync systems, external-destroy sweep. Groups are the ordering contract; systems are `internal`. |
| Chunk-aware view provisioning | 0.2.0, 0.5.0, 0.6.0, 0.26.0 | implemented (registered in client, not exercised) | `IViewAssetProvider` seam, `ChunkViewProvisioner` refcounting keys per chunk, cascade release through the ordinary despawn path, `ChunkState` tracking. |
| Package-owned system group tree | 0.4.0, 0.6.1, 0.13.0, 0.24.0 | implemented, in client | `NetcodeSystemGroup` › `SnapshotApplyGroup`/`PredictionSystemGroup`; `ProvisioningSystemGroup`; `GameplaySystemGroup` › movement/lifecycle/ECB; `ViewSystemGroup` › interpolation/lifecycle/sync. |
| Optional `Shared.GameLogic` seam | 0.3.0 | implemented, in client | `ISimulationModel`, `PassiveSimulationModel`, `SharedGameLogicSimulation`; constants-parity and golden-vector tests. |
| Messaging without a MessagePipe dependency | 0.5.0 | implemented (adapters compile-checked only) | `IDotsPublisher`/`IDotsSubscriber`, five view/chunk messages, MessagePipe adapters in `Cuvara.DOTS.DI`. |
| GameFoundation asset provider | 0.2.0 | implemented, compile-checked only | `IViewAssetProvider` over UniT. Not used by the client. |
| Hybrid Views sample + scene | 0.6.2 | sample-only | Bootstrap, primitive provider, orbiting entities, narrated chunk warm/release. Compiled in every CI row. |
| View configuration as data | 0.7.0 | implemented, in client | `ViewConfig`, `ViewArchetypeLibrary`, `ViewConfigCatalog` blob table, `ViewConfigRef`. |
| Simulation components and systems | 0.7.0, parallel 0.17.0 | implemented, in client | `TimeToLive`, `Health`, `MoveToward`, `MoveData`, `SpinSpeed`; `DotsSimulationBootstrap`. |
| Netcode `IEntityView` adapter | 0.9.0 – 0.13.0 | implemented, in client | `DotsEntityView` (enqueue-only, any thread), drain in `SnapshotApplyGroup`, `NetworkEntity`/`NetworkEntityState`/`ReconciliationAnchor`, `TypeArchetypeResolver`, `SnapshotSpaceMapping`. |
| Client-side prediction driver | 0.13.0 – 0.23.0 | implemented, in client | `LocalPredictionSystem`, `PredictedTransform`, `DotsPredictionBootstrap`. |
| Remote interpolation in ECS | 0.24.0 | implemented, in client | `SetStateAtTick`, `SnapshotSample`, `InterpolationClockSystem`, `RemoteInterpolationSystem` calling netcode's `SnapshotInterpolation`. |
| Networked Prediction sample | 0.14.0 | sample-only | End-to-end against a live backend; overlay of server vs drawn position. |
| Stress Benchmark sample | 0.25.0 | sample-only, **excluded from CI**; no results recorded | Tier ramp is configuration, not a measurement. |
| Physics helpers | 0.26.0 / 0.26.1 | implemented, untested, not in CI | `PhysicsBodyFactory`, `SpatialQuery`, `PhysicsMovementBridge` (no installer). **No collision/trigger collector** — `EntityCollision`/`EntityTriggerEvent` are data-contract-only. |
| View overlay anchors | 0.26.0 | implemented producer, no consumer | `ViewOverlayAnchor` → `ViewOverlayBuffer` via `ViewOverlaySystem`; host owns projection and UI. |
| Editor debug window | 0.26.0 | implemented, untested | Window › Cuvara › DOTS View Debug. |
| `PooledViewAssetProvider` | 0.27.0 | implemented (12 tests); not in client | SetActive pool. Identity/ownership/disposal fixes in progress (D02). |
| `EntityArchetypePreset` + `ArchetypeFactory` | 0.27.0 | implemented (8 tests); not in client | Component presets. Validation in progress (D05). |
| `CameraFollowSystem` | 0.27.0 | implemented system, **no installer**, untested | `Camera.main` only; needs `CameraFollowConfig` + one `CameraFollowTarget`. |
| `MinimapEntry` / `MinimapBuffer` | 0.27.0 | **data-contract-only** | No producer exists. |
| Network lifecycle events | structs 0.27.0; **published on `feat/matrix-events`** | implemented (24 tests), sample consumer, DI wiring; not yet in client | `NetworkEntitySpawned`/`Despawned` with `Entity`+version and `NetworkDespawnReason`; `NetworkEntityLifecycle`; `Uninstall(destroyMirrors)`. Contract: `Documentation~/NETWORK-LIFECYCLE.md`. |

## In progress

Concurrent branches off `main` (v0.27.1), one review unit each — the suggested PR boundaries in the
improvement plan §9:

| Branch | Items | What changes |
|---|---|---|
| `feat/pool-chunk` | D02, D03 | `PooledViewAssetProvider` identity/ownership/disposal; `ChunkViewProvisioner` cancellation and state transitions. Everything under `Runtime/Provisioning/`. |
| `feat/bootstrap-config` | D04, D05 (D09 camera behaviour, D07 physics installer as they land) | Explicit install/uninstall for camera, physics and other optional systems; `ViewConfig`/`ArchetypeFactory` validation. |
| `feat/matrix-events` | D01, D06 | This file, `SUPPORT-MATRIX.md`, netcode floor 0.31.0, lifecycle event publishing. |

Descriptions of provisioning, camera and physics elsewhere in this document are **0.27.1
behaviour**; they will be updated when those branches merge.

## Planned, in order

1. **Finish advertised modules before adding new ones** (plan §5): minimap producer (D08),
   collision/trigger collector with versioned entity identity (D07, only if physics is a release
   requirement), camera no-target/multi-target behaviour (D09), either apply `ViewSortingKey` in a
   dedicated 2D path or keep it marked unsupported (D08).
2. **Ingestion and transform ownership** (D10): instrument the command queue, per-session ingestion
   ownership so late data cannot respawn old entities, one interpolation path per entity.
3. **Measured performance** (D11): profiler markers, allocation and queue metrics, then targeted
   optimisation against a recorded baseline. No capacity claim before this.
4. **Real assets in the host** (D12): production provider choice (GameFoundation pool vs package
   pool), Addressables leases, at least one animated and one effect prefab.
5. **Verification matrix and release process** (D13, D14): CI rows for VContainer/MessagePipe and
   `com.unity.physics`; Android IL2CPP boot; result artefacts per configuration.
6. **Conditional expansion** (E01–E07): culling/LOD, spawn budget, animation and VFX bridges,
   authoring tools, chunk streaming controller, 2D tiles — each only on demonstrated gameplay need.

## Known debts

- **The test suite is measured by CI floors, not headcounts**: Editor ≥ 30, Runtime ≥ 29, GameLogic
  ≥ 41, Netcode ≥ 47, Prediction ≥ 19. Raise a floor when it can no longer fail.
- **`testables` is load-bearing** in a consuming project: without it the package's test assemblies
  are silently not built. See `README.md › Running this package's tests`.
- **`Cuvara.DOTS.DI`, `Cuvara.DOTS.GameFoundation`, `Cuvara.DOTS.Physics` and `Cuvara.DOTS.Editor`
  have no tests and no CI row.** They are compile-checked by the client project only.
- **`.meta` files are load-bearing.** A git-URL install lands in `Library/PackageCache`, which Unity
  treats as immutable; a new file without a `.meta` is silently ignored. CI checks this.
- **Two `PrimitiveViewAssetProvider` copies** exist under `Samples~/` plus a third in the client.
  Deliberate (samples must be self-contained) but worth knowing when one is fixed.

## Out of scope

- **Entity rendering wrappers** over Entities.Graphics. Visuals are GameObjects.
- **A new asset loader, cache, or GameObject pool** beyond `PooledViewAssetProvider`'s SetActive
  pool. GameFoundation owns loading; a second pool over the same prefabs contends with the first.
- **Wrappers over `SystemAPI` singleton access.**
- **Scene bootstrap** — cameras, lights, ground planes belong in a sample.
- **2D collision.** No DOTS 2D physics; the tile blob (planned) is a broadphase, not a physics engine.
- **Snapshot merge, interpolation arithmetic, transport, codec, entity-handle interning, reconnect
  policy.** `com.cuvara.netcode` owns them. `RemoteInterpolationSystem` *calls* netcode's
  `SnapshotInterpolation`; a lerp appearing in this package is the divergence this line forbids.
- **Death semantics.** The wire does not distinguish an AOI exit from a removal, and this package
  will not guess; `NetworkDespawnReason` reports only what the adapter knows.
- **Nakama economy/auth/storage.** Backend and client concerns.

## Measurement caveat

Standalone Windows and Linux builds use Mono2x with managed stripping disabled, so a green result
there exercises neither IL2CPP nor the stripper and cannot validate AOT behaviour, `link.xml`
preservation, or Burst codegen. Any performance or AOT claim must be backed by an Android or WebGL
build, with stripping raised above the default Minimal, and recorded with device, OS, package
commits, backend image, workload and p50/p95/p99 — the format in `SUPPORT-MATRIX.md › Performance
claims`. **No such record exists yet.**
