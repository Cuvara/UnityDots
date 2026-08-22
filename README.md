# Cuvara DOTS

Shared DOTS/ECS building blocks for Cuvara projects — reusable components, systems, jobs and authoring helpers built on Unity Entities, Burst, Collections and Mathematics.

**Status: early.** Ships the hybrid entity↔GameObject view layer and chunk-aware view
provisioning. Not yet compiled against a Unity Editor — see `CHANGELOG.md`.

## Layout

| Path | Assembly | Optional? | Purpose |
|---|---|---|---|
| `Runtime/` | `Cuvara.DOTS.Runtime` | no | View link components, registry, spawn/despawn/sync systems, provisioning interfaces |
| `Runtime.GameFoundation/` | `Cuvara.DOTS.GameFoundation` | yes — UniT + UniTask defines | `IViewAssetProvider` over `IAssetsManager` + `IObjectPoolManager` |
| `Runtime.GameLogic/` | `Cuvara.DOTS.GameLogic` | yes — `CUVARA_SHARED_GAMELOGIC` | `ISimulationModel` over `Shared.GameLogic`, plus all `Vec2`↔`float2` conversion |
| `Runtime.DI/` | `Cuvara.DOTS.DI` | yes — `CUVARA_DOTS_VCONTAINER` | `RegisterDotsViews()`, `RegisterSimulationModel()`, MessagePipe binding |
| `Runtime.Netcode/` | `Cuvara.DOTS.Netcode` | yes — `CUVARA_NETCODE`, netcode >= 0.4.0 | `IEntityView` over ECS: server snapshots become entities and views |
| `Editor/` | `Cuvara.DOTS.Editor` | no | Editor-only tooling and inspectors |
| `Tests/Runtime/` | `Cuvara.DOTS.Tests.Runtime` | no | Play-mode tests |
| `Tests/Editor/` | `Cuvara.DOTS.Tests.Editor` | no | Edit-mode tests |
| `Tests/Editor.GameLogic/` | `Cuvara.DOTS.Tests.GameLogic` | yes — `CUVARA_SHARED_GAMELOGIC` | Constants parity + golden vectors through the seam |
| `Tests/Editor.Netcode/` | `Cuvara.DOTS.Tests.Netcode` | yes — `CUVARA_NETCODE` | The snapshot adapter end to end, driven through the public groups |

The optional assemblies are gated by asmdef `versionDefines` + `defineConstraints`, the same
way `com.gdk.core` gates `GDK_VCONTAINER`. With VContainer absent, `Cuvara.DOTS.DI` is not
compiled and the core still works — you construct `EntityViewRegistry` yourself and call
`DotsViewBootstrap.Install(world, registry)`. With GameFoundation/UniT absent, you implement
`IViewAssetProvider` over whatever pool you do have. **The core assembly references none of them**,
and installs against its four pinned Unity dependencies alone.

The dependency arrow between this package and `com.cuvara.netcode` is **one-way**: DOTS may
reference netcode, netcode never references DOTS. That is what keeps the netcode package usable by a
GameObject client.

## Netcode adapter

With `com.cuvara.netcode` installed, `Cuvara.DOTS.Netcode` supplies a `Cuvara.Netcode.View.IEntityView`
that presents replicated entities as ECS entities driven through this package's own view pipeline.

```csharp
// Which archetype an entity is presented as, keyed on the kind the server sent. Arguments are
// (localArchetype, unknownArchetype, ...rules); a null unknownArchetype means an unmapped kind is
// refused and logged rather than quietly rendered as something else.
var resolver = new TypeArchetypeResolver(
    "player-local",
    null,
    new TypeArchetypeResolver.Rule("player", "player-remote"),
    new TypeArchetypeResolver.Rule("mob", "goblin"));

var view = new DotsEntityView(catalog, resolver, SnapshotSpaceMapping.XZPlane);
DotsNetcodeBootstrap.Install(world, view);          // publishes NetworkEntityViewReference

var binder = new WorldViewBinder(view);             // from com.cuvara.netcode
// ... once per frame, from wherever you consume the socket:
binder.Tick(worldState, networkClient.UserId);
```

Each replicated id becomes an entity carrying `NetworkEntity` (the wire id, the server's entity kind,
and `IsLocal`), `NetworkEntityState` (the newest authoritative hp), `ReconciliationAnchor` (the newest
authoritative position), a `SnapshotSample` buffer and an `InterpolationState` (what remote
interpolation has to work with, and what it last drew), a `LocalTransform`, and the
`EntityViewRequest` + `ViewConfigRef` pair the spawn path already understands. Nothing about the
presentation is hardcoded: the prefab, pool size, scale and offsets all come from the `ViewConfig` the
resolver named.

Four things worth knowing before wiring it up:

- **Requires `com.cuvara.netcode` 0.19.0 or newer**, enforced by the asmdef's `versionDefines`
  expression rather than by a `package.json` dependency. With an older netcode installed the define
  is never set and `Cuvara.DOTS.Netcode` simply does not compile into the project — the adapter is
  absent instead of broken. The floor was 0.4.0, the release that added the entity type to
  `IEntityView.Spawn`; it is 0.19.0 from this version because remote interpolation reads
  `Cuvara.Netcode.Interpolation`, which does not exist before then. Left at 0.4.0 the define would
  be set against a netcode that has no such namespace, and the adapter would fail to compile with a
  missing-type error that names a type rather than a version.
- **Kind comes from the wire, never from the id.** `TypeArchetypeResolver` maps the server's entity
  type (`"player"`, `"mob"`, …) to an archetype name, exactly and ordinally. Inferring kind from an
  id prefix is what `PrefixArchetypeResolver` did before 0.9.0, and it is gone.

- **`IEntityView` calls enqueue; they do not write components.** The queue is drained by an internal
  system in `NetcodeSystemGroup`, which is in `InitializationSystemGroup` — before this frame's
  transforms and long before this frame's `ViewSystemGroup`. A snapshot applied before initialization
  is a positioned view in the same frame. Calling `binder.Tick` from the socket thread is therefore
  safe, which it would not be if the adapter touched `EntityManager` directly.
- **Server `(x, y)` → world placement is `SnapshotSpaceMapping`, a constructor argument**, not a
  constant and not a per-archetype setting. `XZPlane` (the default) puts the server plane on Unity's
  ground plane with no lift; the per-art half-height lift belongs in `ViewConfig.PositionOffset`,
  which is applied to the view instance rather than to the entity.
- **Wire hp lands on `NetworkEntityState`, not on `Health`.** `Health` means "destroy at zero" in
  this package, so mirroring server hp into it lets a client-side system destroy an entity the
  server still lists. Pass `writeHealth: true` if you want that anyway.
- **Remote entities can be interpolated in ECS, and it is opt-in because the tick is.**
  `IEntityView.SetState` carries no server tick, and without one there is no timeline to place a
  state on — so a state arriving through the interface is applied exactly as it always was, written
  straight to the transform. `DotsEntityView.SetStateAtTick(id, x, y, hp, maxHp, tick, receiveTime)`
  is the adapter's own entry point for a caller that *does* have the tick, typically a snapshot
  handler reading `WorldState.Tick`. A state with a tick is appended to the entity's `SnapshotSample`
  buffer and rendered every frame by `RemoteInterpolationSystem`, which evaluates
  `Cuvara.Netcode.Interpolation.SnapshotInterpolation` — netcode's own core, the same one
  `WorldViewBinder` uses, in a Bursted `IJobEntity` over chunk memory. There is no interpolation
  arithmetic in this package and there must never be.

  **Do not feed the same entity both ways.** `WorldViewBinder.Tick` interpolates on the netcode side
  and hands the *result* to `SetState`, so buffering those and interpolating again would stack a
  second `TargetDelay` on top of the first — remote entities twice as far behind, smoothly, with
  nothing to notice. The drain enforces the split per entity rather than trusting it: a ticked state
  is buffered and the transform is left alone; an unticked one is written and the buffer stays empty,
  so the job passes over the entity.

  Tuning is `DotsNetcodeBootstrap.Install(world, view, interpolation)` — an `InterpolationConfig`,
  netcode's own blittable struct, published as the `InterpolationSettings` singleton because a
  `[BurstCompile]` job cannot read a `ScriptableObject`. `default` means netcode's defaults: 100 ms
  of render delay and 8 retained samples. The local player pays none of it — a predicted entity
  carries `PredictedTransform` and is excluded.
- **A predictor takes the transform by adding `PredictedTransform`.** The adapter then writes only
  `ReconciliationAnchor` — the last authoritative position, in world space — and leaves
  `LocalTransform` alone, so each component has exactly one writer. Without the tag nothing changes:
  the adapter positions every entity, which is what every build with no predictor needs. The anchor's
  *tick* is not here and cannot be: `IEntityView.SetState` does not carry one. A predictor reads
  `WorldState.AckTick` from netcode, which is documented as exactly that anchor.

## View configuration

Author a `ViewConfig` per kind of view and list them in a `ViewArchetypeLibrary` under the names the
server uses. At session start, build the catalog and publish it:

```csharp
var catalog = new ViewConfigCatalog();
catalog.Build(library);
catalog.Install(world);              // publishes ViewConfigTableReference

// Warm what the catalog needs, using its own pool sizes:
foreach (var (key, size) in catalog.PoolSizesByKey())
    await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { key }, countPerKey: size);

// Spawn by archetype name — resolve once, carry the index:
entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
entityManager.AddComponentData(entity, new ViewConfigRef { Index = catalog.IndexOf("goblin") });
```

The bare-key path still works exactly as before: an entity with only `EntityViewRequest` and no
`ViewConfigRef` behaves as it always did. `catalog.Dispose()` releases the blob.

## System groups

Every package system is `[DisableAutoCreation]` and created by `DotsViewBootstrap.Install(world, registry)`.
Groups are `public` and are the ordering contract; the systems inside them are `internal` and will change.

```
InitializationSystemGroup                     [Unity]
├── NetcodeSystemGroup                        snapshot apply (Cuvara.DOTS.Netcode, when installed)
└── ProvisioningSystemGroup                   UpdateAfter(NetcodeSystemGroup); empty
SimulationSystemGroup                         [Unity]
├── GameplaySystemGroup                       UpdateBefore(TransformSystemGroup)
│   ├── MovementSystemGroup                   (empty)
│   ├── LifecycleSystemGroup                  UpdateAfter(MovementSystemGroup); empty
│   └── DotsEndSimulationCommandBufferSystem  OrderLast
└── TransformSystemGroup                      [Unity]
PresentationSystemGroup                       [Unity]
└── ViewSystemGroup
    ├── ViewInterpolationGroup                UpdateBefore(ViewLifecycleGroup); empty without netcode
    │   ├── InterpolationClockSystem          advances the render clock once per frame
    │   └── RemoteInterpolationSystem         UpdateAfter(InterpolationClockSystem)
    ├── ViewLifecycleGroup                    structural: views appear/disappear
    │   ├── EntityViewDespawnSystem           first — freed instances reusable this frame
    │   └── EntityViewSpawnSystem             UpdateAfter(EntityViewDespawnSystem)
    └── ViewTransformSyncGroup                UpdateAfter(ViewLifecycleGroup)
        └── EntityViewTransformSyncSystem
```

Order your own systems against the groups: `[UpdateAfter(typeof(ViewSystemGroup))]`.

## Usage

```csharp
// DI (VContainer + GameFoundation present), after RegisterGameFoundation:
builder.RegisterGameFoundationViewProvisioning();
builder.RegisterDotsViews(viewRoot);

// Warm everything a chunk needs, then drop it when the chunk unloads:
await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { "goblin", "torch" }, countPerKey: 8);

// Views still standing on the chunk's expiring keys are despawned first, then the assets go.
// The entities survive without views; a ChunkCascadeReleased message reports how many.
var result = provisioner.ReleaseChunk("chunk-12-4");   // keys another chunk still lists survive
Debug.Log($"released {result.KeysReleased} keys, cascaded {result.ViewsDespawned} views");

// Simulation seam — identical call sites with or without com.rpgmmo.shared-gamelogic:
builder.RegisterSimulationModel();
if (model.IsAuthoritative)          // false => no shared logic; do NOT predict
    model.TryMove(in entity, input, dt, in bounds, out var predicted);

// Give an entity a view:
entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
```

## Installation

### Git URL

Add to your project's `Packages/manifest.json`:

```json
"com.cuvara.dots": "https://github.com/Cuvara/com.cuvara.dots.git#v0.6.2"
```

Or via **Window > Package Manager > + > Add package from git URL**:

```
https://github.com/Cuvara/com.cuvara.dots.git#v0.6.2
```

### Embedded

Clone into your project's `Packages/com.cuvara.dots/` folder for local development.

### Running this package's tests in your project

A git-URL install lands in `Library/PackageCache`, and **Unity does not compile a package's test
assemblies unless the project asks for them**. Nothing warns you: the tests do not fail, they are
simply absent — no `Cuvara.DOTS.Tests.*` assembly in `Library/ScriptAssemblies`, and the Test Runner
filtered to `Cuvara.DOTS` reports *no tests found*, which reads exactly like a package with no tests.

Add the package to `testables` in your project's `Packages/manifest.json`, as a sibling of
`dependencies`:

```json
{
  "dependencies": { "com.cuvara.dots": "https://github.com/Cuvara/com.cuvara.dots.git#v0.6.2" },
  "testables": [ "com.cuvara.dots" ]
}
```

The `testables` entry this package declares in its own `package.json` does **not** substitute for
that: the consuming project's manifest is what makes the Test Runner build the assemblies.

Editing the manifest is not always enough on its own — an Editor that has already resolved the
package keeps its resolution cached, so the assemblies stay missing until the Editor is restarted.
Verify by checking that `Library/ScriptAssemblies/Cuvara.DOTS.Tests.Editor.dll` exists, not by
trusting the manifest edit.

## Releasing

Tagging is manual and deliberate — `npm publish` cannot be undone, so a bad version can only be
superseded, never withdrawn.

```bash
# 1. bump package.json, add the matching "## [X.Y.Z]" CHANGELOG section, merge to main
# 2. wait for CI to be green on the commit you are about to tag
git tag vX.Y.Z && git push origin vX.Y.Z
```

The tag triggers `release.yml`, which refuses to proceed unless `package.json` says exactly what the
tag says, extracts the release notes from that CHANGELOG heading, creates the GitHub Release, and
then publishes `@cuvara/dots@X.Y.Z` to GitHub Packages. `release-reminder.yml` warns on every push to
`main` while the version in `package.json` has no tag.

CI asserts a **test-count floor per assembly** rather than an exit code, in two configurations — with
`com.cuvara.netcode` installed and without it. The reasoning, and why an exit code is not enough for a
package whose test assemblies are gated on optional dependencies, is in `CHANGELOG.md` under 0.11.0.

## Requirements

- Unity 6000.3 or newer

Resolved automatically via `package.json`:

| Package | Version |
|---|---|
| `com.unity.entities` | 1.4.8 |
| `com.unity.burst` | 1.8.30 |
| `com.unity.collections` | 2.6.8 |
| `com.unity.mathematics` | 1.3.2 |

Optional, and resolved by your project rather than by this package:

| Package | Enables | Define |
|---|---|---|
| `com.cuvara.netcode` **>= 0.19.0** | `Cuvara.DOTS.Netcode` — `IEntityView` over ECS | `CUVARA_NETCODE` |
| `com.rpgmmo.shared-gamelogic` | `Cuvara.DOTS.GameLogic` | `CUVARA_SHARED_GAMELOGIC` |
| VContainer | `Cuvara.DOTS.DI` | `CUVARA_DOTS_VCONTAINER` |

## Conventions

- `com.unity.jobs` is deprecated — it is merged into `com.unity.collections`.
- Use `IJobEntity` or `SystemAPI.Query` instead of the obsolete `Entities.ForEach`.
- Prefer unmanaged `ISystem` over managed `SystemBase`.

## License

MIT — see [LICENSE](LICENSE).
