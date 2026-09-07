# Cuvara DOTS

Shared DOTS/ECS building blocks for Cuvara projects — hybrid entity↔GameObject views, chunk-aware
view provisioning, simulation scaffolding, and an adapter that presents `com.cuvara.netcode`'s
replicated entities as ECS entities with interpolation, prediction and lifecycle events. Built on
Unity Entities, Burst, Collections and Mathematics.

**Status: 0.27.1, plumbing complete, gameplay content absent.** The view layer, provisioning,
simulation seam, netcode adapter and prediction driver are implemented, tested in CI and wired into
the client. Several 0.26–0.27 additions are *not* finished features: minimap, physics collision
events and 2D sorting are data contracts with no producer; camera follow and the physics movement
bridge have no installer. **Read `Documentation~/SUPPORT-MATRIX.md` before depending on anything
in the last two groups** — it classifies every feature as implemented / integrated in client /
sample-only / data-contract-only / planned, and records what CI actually tests.

## Layout

| Path | Assembly | Gate | Purpose | Status |
|---|---|---|---|---|
| `Runtime/` | `Cuvara.DOTS.Runtime` | none | View link + registry + systems, view config as data, provisioning seam and `PooledViewAssetProvider`, simulation systems, group tree, messaging seam, overlay anchors, camera follow, minimap types | core; see matrix per feature |
| `Runtime.Netcode/` | `Cuvara.DOTS.Netcode` | `CUVARA_NETCODE` — `com.cuvara.netcode` **≥ 0.31.0** | `IEntityView` over ECS, remote interpolation, network lifecycle events | implemented, in client |
| `Runtime.Netcode.Prediction/` | `Cuvara.DOTS.Netcode.Prediction` | `CUVARA_NETCODE` + `CUVARA_SHARED_GAMELOGIC` | Client-side prediction driver | implemented, in client |
| `Runtime.GameLogic/` | `Cuvara.DOTS.GameLogic` | `CUVARA_SHARED_GAMELOGIC` | `ISimulationModel` over `Shared.GameLogic` | implemented, in client |
| `Runtime.DI/` | `Cuvara.DOTS.DI` | `CUVARA_DOTS_VCONTAINER` (+ optional `CUVARA_DOTS_MESSAGEPIPE`, `CUVARA_NETCODE`) | `RegisterDotsViews`, `RegisterDotsMessaging`, `RegisterSimulationModel`, `RegisterDotsNetworkLifecycle` | compile-checked only |
| `Runtime.GameFoundation/` | `Cuvara.DOTS.GameFoundation` | UniT pooling + resources + UniTask | `IViewAssetProvider` over `IAssetsManager` + `IObjectPoolManager` | compile-checked only |
| `Runtime.Physics/` | `Cuvara.DOTS.Physics` | `CUVARA_DOTS_PHYSICS` — `com.unity.physics` ≥ 1.0.0; not auto-referenced | Body factory, spatial queries, `MoveData` → `PhysicsVelocity` bridge. **No collision/trigger collector.** | untested, not in CI |
| `Editor/` | `Cuvara.DOTS.Editor` | Editor | DOTS View Debug window | untested |
| `Tests/Editor/`, `Tests/Runtime/` | `Cuvara.DOTS.Tests.Editor`, `.Runtime` | tests | Core EditMode / PlayMode tests | CI floors 30 / 29 |
| `Tests/Editor.Netcode/`, `Tests/Editor.Prediction/`, `Tests/Editor.GameLogic/` | `Cuvara.DOTS.Tests.*` | as their runtime assembly | Adapter, prediction, shared-logic parity | CI floors 47 / 19 / 41 |
| `Samples~/` | `Cuvara.DOTS.Samples.*` | per sample | Hybrid Views (scene), Networked Prediction (live backend), Stress Benchmark | sample-only |

Optional assemblies are gated by asmdef `versionDefines` + `defineConstraints`: with the dependency
absent the assembly is not compiled and the core still works. **The core references none of them**
and installs against its four pinned Unity dependencies alone — CI's *no optional packages* row
exists to prove it. The dependency arrow between this package and `com.cuvara.netcode` is
**one-way**: DOTS may reference netcode, netcode never references DOTS.

## Netcode adapter

With `com.cuvara.netcode` ≥ 0.31.0 installed, `Cuvara.DOTS.Netcode` supplies a
`Cuvara.Netcode.View.IEntityView` that presents replicated entities as ECS entities driven through
this package's own view pipeline.

```csharp
var resolver = new TypeArchetypeResolver(
    "player-local", null,                                    // local archetype; no catch-all for unknown kinds
    new TypeArchetypeResolver.Rule("player", "player-remote"),
    new TypeArchetypeResolver.Rule("mob", "goblin"));

var view = new DotsEntityView(catalog, resolver, SnapshotSpaceMapping.XZPlane);
DotsNetcodeBootstrap.Install(world, view);                   // after DotsViewBootstrap.Install

var spawned = view.Lifecycle.Subscribe((NetworkEntitySpawned e) => Debug.Log($"+ {e.EntityId} {e.Entity}"));

var binder = new WorldViewBinder(view);                      // from com.cuvara.netcode
binder.Tick(worldState, networkClient.UserId);               // once per frame, from the socket consumer
```

Each replicated id becomes an entity carrying `NetworkEntity` (wire id, kind, `IsLocal`),
`NetworkEntityState` (newest authoritative hp), `ReconciliationAnchor` (newest authoritative
position), a `SnapshotSample` buffer + `InterpolationState`, a `LocalTransform`, and the
`EntityViewRequest` + `ViewConfigRef` pair the spawn path already understands.

What to know before wiring it up — each expanded in `Documentation~/NETCODE-INTEGRATION.md`:

- **`IEntityView` calls enqueue; they do not write components.** The queue is drained by an internal
  system in `SnapshotApplyGroup` (Initialization), so `binder.Tick` may run on the socket thread and
  a snapshot applied before initialization is a positioned view in the same frame.
- **Kind comes from the wire, never from the id.** `TypeArchetypeResolver` maps the server's entity
  type to an archetype name; an unmapped kind is refused and logged once.
- **Server `(x, y)` → world placement is `SnapshotSpaceMapping`**, a constructor argument. Per-art
  height lift belongs in `ViewConfig.PositionOffset`.
- **Wire hp lands on `NetworkEntityState`, not on `Health`.** `Health` means "destroy at zero" here.
  `writeHealth: true` opts in.
- **Remote interpolation in ECS is opt-in via `SetStateAtTick`.** A state with a tick is buffered and
  rendered by `RemoteInterpolationSystem` (netcode's `SnapshotInterpolation`, in a Burst job); a
  state without one is written straight to the transform. Never feed one entity both ways.
- **A predictor claims the transform by adding `PredictedTransform`**; the adapter then writes only
  `ReconciliationAnchor`. `DotsPredictionBootstrap.Install(world, predictor, worldState)` installs
  the driver.
- **Network presence has its own events.** `NetworkEntitySpawned` / `NetworkEntityDespawned` on
  `view.Lifecycle` fire exactly once per life of an id, carry `Entity` with version and a
  `NetworkDespawnReason` that is never "died" — the wire does not distinguish an AOI exit from a
  removal. They are separate from the visual `ViewSpawned`/`ViewDespawned`. Teardown:
  `DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true)`. Contract and scripted sequences:
  `Documentation~/NETWORK-LIFECYCLE.md`.

## View configuration

Author a `ViewConfig` per kind of view and list them in a `ViewArchetypeLibrary` under the names the
server uses. At session start, build the catalog and publish it:

```csharp
var catalog = new ViewConfigCatalog();
catalog.BuildOrThrow(library);       // validates first — see Documentation~/CONFIG-VALIDATION.md
catalog.Install(world);              // publishes ViewConfigTableReference

foreach (var (key, size) in catalog.PoolSizesByKey())
    await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { key }, countPerKey: size);



// Spawn by archetype name — resolve once, carry a versioned ref issued by the catalog:
entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
entityManager.AddComponentData(entity, catalog.CreateRef(catalog.IndexOf("goblin")));
```

The bare-key path still works: an entity with only `EntityViewRequest` behaves as before configs
existed. A rebuild invalidates every index handed out before it. `catalog.Dispose()` releases the
blob. `ViewSortingKey` is copied from the config and **not applied** to any renderer.

The bare-key path still works exactly as before: an entity with only `EntityViewRequest` and no
`ViewConfigRef` behaves as it always did. `catalog.Dispose()` releases the blob and removes the
singleton from every world it was installed in.

A `ViewConfigRef` is **versioned**: every `catalog.Build` bumps `catalog.Version`, and a ref from
an earlier version is refused by the spawn path (falling back to the request's own key) rather than
resolving to whatever now sits at that index. Never construct one with `new` — it carries version 0
and is always refused. Validation (`ViewConfigValidator`, `catalog.TryBuild`) reports empty,
duplicate or overlong keys, missing prefabs, non-finite values and unknown entity-type mappings
before gameplay; `Documentation~/CONFIG-VALIDATION.md` has the full contract.

## System groups

Every package system is `[DisableAutoCreation]` and created by an explicit bootstrap:
`DotsViewBootstrap.Install(world, registry)` (view tree), `DotsSimulationBootstrap.InstallSimulationSystems(world)`,
`DotsNetcodeBootstrap.Install(world, view)`, `DotsPredictionBootstrap.Install(world, predictor, worldState)`.
Groups are `public` and are the ordering contract; the systems inside them are `internal`.

```
InitializationSystemGroup                     [Unity]
├── NetcodeSystemGroup
│   ├── SnapshotApplyGroup                    NetworkViewCommandSystem (netcode)
│   └── PredictionSystemGroup                 UpdateAfter(SnapshotApplyGroup); LocalPredictionSystem (prediction)
└── ProvisioningSystemGroup                   UpdateAfter(NetcodeSystemGroup); empty
SimulationSystemGroup                         [Unity]
├── GameplaySystemGroup                       UpdateBefore(TransformSystemGroup)
│   ├── MovementSystemGroup                   MoveToward → MoveBounce → Spin; PhysicsMovementBridge declares itself here but nothing installs it
│   ├── LifecycleSystemGroup                  UpdateAfter(MovementSystemGroup); HealthDeath → TimeToLive
│   └── DotsEndSimulationCommandBufferSystem  OrderLast
└── TransformSystemGroup                      [Unity]
PresentationSystemGroup                       [Unity]
└── ViewSystemGroup
    ├── ViewInterpolationGroup                UpdateBefore(ViewLifecycleGroup); empty without netcode
    │   ├── InterpolationClockSystem
    │   └── RemoteInterpolationSystem         UpdateAfter(InterpolationClockSystem)
    ├── ViewLifecycleGroup
    │   ├── EntityViewDespawnSystem           first — freed instances reusable this frame
    │   └── EntityViewSpawnSystem             UpdateAfter(EntityViewDespawnSystem)
    └── ViewTransformSyncGroup                UpdateAfter(ViewLifecycleGroup)
        ├── EntityViewTransformSyncSystem
        └── ViewOverlaySystem                 UpdateAfter(EntityViewTransformSyncSystem)
    (CameraFollowSystem declares UpdateAfter(ViewTransformSyncGroup) but no bootstrap creates it)

    ├── ViewTransformSyncGroup                UpdateAfter(ViewLifecycleGroup)
    │   ├── EntityViewTransformSyncSystem
    │   └── ViewOverlaySystem                 UpdateAfter(EntityViewTransformSyncSystem)
    └── CameraFollowSystem                    UpdateAfter(ViewTransformSyncGroup); only with CameraFollowBootstrap
```

Order your own systems against the groups: `[UpdateAfter(typeof(ViewSystemGroup))]`. After any
manual install, `SystemOrderVerifier.Verify(world)` returns every declared relation the actual
update order violates — a system added to the wrong group, a group left unsorted — recursively
through the subgroups.

## Modules: install, uninstall, ownership

Each optional piece is a module with a bootstrap, an idempotent `Install`, a safe-twice
`Uninstall`, and a recorded owner (`DotsModuleScope.Root` or `Session`) kept **in the world**, so a
disposed session leaves no stale `World` reference behind:

```csharp
DotsViewBootstrap.Install(world, registry);                       // Root by default
DotsSimulationBootstrap.InstallSimulationSystems(world);          // Root by default
CameraFollowBootstrap.Install(world, new CameraFollowConfig());   // Session; validates the config
PhysicsMovementBootstrap.Install(world);                          // Session; Runtime.Physics only

DotsModules.UninstallScope(world, DotsModuleScope.Session);       // scene reload
DotsModules.UninstallAll(world); world.Dispose();                 // permanent teardown
```

`Documentation~/MODULE-LIFECYCLE.md` states the contract line by line: install twice, uninstall
twice, registry replacement, two worlds, temporary disable versus world disposal.

## Usage

```csharp
// DI (VContainer present), after MessagePipe's RegisterMessagePipe()/RegisterMessageBroker<T>() calls:
builder.Register<IViewAssetProvider>(_ => new PooledViewAssetProvider(...), Lifetime.Singleton);   // or your own
builder.RegisterDotsViews(viewRoot);                 // registry, cascade sink, provisioner, DotsViewBootstrap.Install
builder.RegisterSimulationModel();                   // SharedGameLogicSimulation or PassiveSimulationModel
builder.RegisterDotsNetworkLifecycle();              // NetworkEntityLifecycle (+ MessagePipe forwarding when present)

// Warm everything a chunk needs, then drop it when the chunk unloads:
await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { "goblin", "torch" }, countPerKey: 8);
var result = provisioner.ReleaseChunk("chunk-12-4");   // views on expiring keys cascade-despawn first

// Simulation seam — identical call sites with or without com.rpgmmo.shared-gamelogic:
if (model.IsAuthoritative)          // false => no shared logic; do NOT predict
    model.TryMove(in entity, input, dt, in bounds, out var predicted);

// Give an entity a view:
entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
```

Without DI: construct `EntityViewRegistry` over your `IViewAssetProvider`, call
`DotsViewBootstrap.Install(world, registry)`, and hand a `ChunkViewProvisioner` an
`EntityViewCascade` as its sink — the `HybridViews` sample is exactly this.

## Installation

### Git URL

```json
"com.cuvara.dots": "https://github.com/Cuvara/UnityDots.git#v0.27.1"
```

Or **Window › Package Manager › + › Add package from git URL**:
`https://github.com/Cuvara/UnityDots.git#v0.27.1`.

Optional packages are resolved by *your* manifest, not by this package's `package.json`:
`com.cuvara.netcode` (`https://github.com/Cuvara/Netcode.git#v0.31.0`),
`com.rpgmmo.shared-gamelogic` (`https://github.com/Cuvara/rpg-mmo-server.git?path=/backend/gameserver-dotnet/Shared.GameLogic#sgl-v0.3.0`),
VContainer, MessagePipe, UniT, `com.unity.physics`.

### Embedded

Clone into your project's `Packages/com.cuvara.dots/` folder for local development.

### Running this package's tests in your project

A git-URL install lands in `Library/PackageCache`, and **Unity does not compile a package's test
assemblies unless the project asks for them**. Nothing warns you: the tests are simply absent.

```json
{
  "dependencies": { "com.cuvara.dots": "https://github.com/Cuvara/UnityDots.git#v0.27.1" },
  "testables": [ "com.cuvara.dots" ]
}
```

The `testables` entry in this package's own `package.json` does **not** substitute for that. An
Editor that already resolved the package keeps its resolution cached until restart; verify by the
presence of `Library/ScriptAssemblies/Cuvara.DOTS.Tests.Editor.dll`, not by the manifest edit.

## Tested configurations

Unity **6000.3.9f1**, Linux, Mono, EditMode + PlayMode, via `game-ci/unity-test-runner`. Three rows,
each asserting a **test-count floor per assembly** (a green run over zero tests fails):

| Row | Extra packages | Netcode/Prediction/GameLogic test assemblies |
|---|---|---|
| no optional packages | — | all three must be absent |
| netcode absent | `sgl-v0.3.0` | GameLogic ≥ 41; Netcode/Prediction absent |
| netcode present | `com.cuvara.netcode#v0.31.0`, `sgl-v0.3.0`, OpenUPM scope | Netcode ≥ 47, Prediction ≥ 19, GameLogic ≥ 41 |

Not covered by any row: `Cuvara.DOTS.DI`, `Cuvara.DOTS.GameFoundation`, `Cuvara.DOTS.Physics`,
`Cuvara.DOTS.Editor`, Android/IL2CPP, WebGL. There are **no measured performance figures** in this
repository; the Stress Benchmark sample's tier list is configuration, not a result. Details and
platform table: `Documentation~/SUPPORT-MATRIX.md`.

## Releasing

Tagging is manual and deliberate — `npm publish` cannot be undone.

```bash
# 1. bump package.json, add the matching "## [X.Y.Z]" CHANGELOG section, merge to main
# 2. wait for CI to be green on the commit you are about to tag
git tag vX.Y.Z && git push origin vX.Y.Z
```

The tag triggers `release.yml`, which refuses to proceed unless `package.json` says exactly what the
tag says, extracts the release notes from that CHANGELOG heading, creates the GitHub Release, and
publishes `@cuvara/dots@X.Y.Z` to GitHub Packages. `release-reminder.yml` warns on every push to
`main` while the version in `package.json` has no tag. Consumers adopt a release by updating the
`#vX.Y.Z` in their manifest; never by editing `Library/PackageCache`.

## Requirements

- Unity 6000.3 or newer

Resolved automatically via `package.json`:

| Package | Version |
|---|---|
| `com.unity.entities` | 1.4.8 |
| `com.unity.burst` | 1.8.30 |
| `com.unity.collections` | 2.6.8 |
| `com.unity.mathematics` | 1.3.2 |

Optional, resolved by your project:

| Package | Enables | Define |
|---|---|---|
| `com.cuvara.netcode` **≥ 0.31.0** | `Cuvara.DOTS.Netcode` (+ `.Prediction` with shared-gamelogic) | `CUVARA_NETCODE` |
| `com.rpgmmo.shared-gamelogic` | `Cuvara.DOTS.GameLogic`, `Cuvara.DOTS.Netcode.Prediction` | `CUVARA_SHARED_GAMELOGIC` |
| `jp.hadashikick.vcontainer` | `Cuvara.DOTS.DI` | `CUVARA_DOTS_VCONTAINER` |
| `com.cysharp.messagepipe` | MessagePipe forwarding inside `Cuvara.DOTS.DI` | `CUVARA_DOTS_MESSAGEPIPE` |
| UniT pooling + resources, UniTask | `Cuvara.DOTS.GameFoundation` | `CUVARA_DOTS_UNIT_*`, `CUVARA_DOTS_UNITASK` |
| `com.unity.physics` ≥ 1.0.0 | `Cuvara.DOTS.Physics` | `CUVARA_DOTS_PHYSICS` |

## Documentation

| File | Contents |
|---|---|
| `Documentation~/SUPPORT-MATRIX.md` | Feature and module classification, tested configurations, platforms, corrections to earlier claims |
| `Documentation~/OVERVIEW.md` | Architecture and key concepts |
| `Documentation~/VIEW-PROVISIONING.md` | View lifecycle, `ViewConfig`, chunk provisioning, providers |
| `Documentation~/NETCODE-INTEGRATION.md` | Adapter setup, components, interpolation, prediction |
| `Documentation~/NETWORK-LIFECYCLE.md` | `NetworkEntitySpawned`/`Despawned` contract |
| `ROADMAP.md` | Done / in progress / planned, with classes |
| `CHANGELOG.md` | Per-release detail |

## Conventions

- `com.unity.jobs` is deprecated — it is merged into `com.unity.collections`.
- Use `IJobEntity` or `SystemAPI.Query` instead of the obsolete `Entities.ForEach`.
- Prefer unmanaged `ISystem` over managed `SystemBase`.
- Every new Unity-visible file ships with its `.meta`; CI checks.

## License

MIT — see [LICENSE](LICENSE).
