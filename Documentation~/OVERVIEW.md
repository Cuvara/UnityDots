# Cuvara DOTS — Overview

Shared DOTS/ECS building blocks for Cuvara projects: hybrid entity-to-GameObject views,
chunk-aware provisioning, simulation scaffolding, and a netcode adapter that presents
replicated server entities as ECS entities.

## Architecture

```
com.cuvara.dots
├── Runtime/              Core: views, provisioning, simulation, system groups
├── Runtime.Netcode/      IEntityView over ECS (opt-in, requires com.cuvara.netcode)
├── Runtime.Netcode.Prediction/  Client-side prediction systems
├── Runtime.GameLogic/    Shared.GameLogic bridge (opt-in)
├── Runtime.GameFoundation/  GDK integration (opt-in)
├── Runtime.DI/           VContainer registration (opt-in)
└── Editor/               Editor tooling
```

All optional assemblies are gated by asmdef `versionDefines` + `defineConstraints`.
The core assembly references none of them and installs against four pinned Unity
dependencies alone (Entities, Burst, Collections, Mathematics).

## Key concepts

### Hybrid views

Entities carry `EntityViewRequest` + `ViewConfigRef`. The spawn system instantiates
pooled GameObjects from `IViewAssetProvider`, links them via `EntityViewLink`, and
syncs transforms every frame. Despawn returns the instance to the pool.

### Chunk provisioning

`ChunkViewProvisioner` warms and releases view assets per world chunk. When a chunk
unloads, views standing on its expiring keys are cascade-despawned first, then the
assets are released. Keys shared with other chunks survive.

### Netcode adapter

With `com.cuvara.netcode` >= 0.19.0 installed, `DotsNetcodeBootstrap.Install` creates
a `DotsEntityView` that implements `IEntityView`. Server snapshots become ECS entities
with `NetworkEntity`, `NetworkEntityState`, `ReconciliationAnchor`, and optionally a
`SnapshotSample` buffer for remote interpolation.

### Simulation model

`ISimulationModel` wraps `Shared.GameLogic` behind an interface the ECS systems call.
With the shared-gamelogic package absent, `PassiveSimulationModel` returns false for
`IsAuthoritative` and movement/combat calls are skipped.

## System groups

See the system group tree in `README.md`. Key ordering:

1. `NetcodeSystemGroup` (InitializationSystemGroup) — snapshot apply
2. `GameplaySystemGroup` (SimulationSystemGroup) — movement, lifecycle
3. `ViewSystemGroup` (PresentationSystemGroup) — interpolation, spawn/despawn, transform sync

Order your own systems against these groups, never against the internal systems.

## Dependencies

| Package | Version | Required? |
|---------|---------|-----------|
| `com.unity.entities` | 1.4.8 | Yes |
| `com.unity.burst` | 1.8.30 | Yes |
| `com.unity.collections` | 2.6.8 | Yes |
| `com.unity.mathematics` | 1.3.2 | Yes |
| `com.cuvara.netcode` | >= 0.19.0 | Optional |
| `com.rpgmmo.shared-gamelogic` | any | Optional |
| VContainer | any | Optional |
