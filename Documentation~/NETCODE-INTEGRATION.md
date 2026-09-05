# Netcode Integration

How `com.cuvara.dots` integrates with `com.cuvara.netcode` to present replicated
server entities as ECS entities with interpolation and prediction.

## Prerequisites

- `com.cuvara.netcode` >= 0.19.0 (enforced by asmdef `versionDefines`)
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
| `ReconciliationAnchor` | Newest authoritative position (world space) |
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

## Client-side prediction

Add `PredictedTransform` to the local player entity:

- The adapter writes only `ReconciliationAnchor` (authoritative position)
- `LocalTransform` is left to the prediction system
- Remote interpolation excludes entities with `PredictedTransform` (`WithNone`)
- The prediction system reads `WorldState.AckTick` for the reconciliation anchor tick

## SnapshotSpaceMapping

Controls how server `(x, y)` maps to Unity world coordinates:

| Mapping | Server (x,y) → Unity |
|---------|---------------------|
| `XZPlane` (default) | x → X, y → Z, Y = 0 |
| `XYPlane` | x → X, y → Y, Z = 0 |

Per-art height offset belongs in `ViewConfig.PositionOffset`, not in the mapping.

## Important constraints

1. **No interpolation arithmetic in this package.** All interpolation calls go
   through `Cuvara.Netcode.Interpolation.SnapshotInterpolation` — netcode's core.
2. **IEntityView calls enqueue, not write.** The queue is drained by
   `NetworkViewCommandSystem` in `NetcodeSystemGroup` (InitializationSystemGroup).
3. **Wire HP → `NetworkEntityState`, not `Health`.** `Health` means "destroy at
   zero" in this package. Mirroring server HP into it would let a client-side
   system destroy an entity the server still lists.
