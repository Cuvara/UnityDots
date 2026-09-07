# Module lifecycle — install, uninstall, ownership

Every optional piece of this package is a **module** with one bootstrap class, one
`Install`, one `Uninstall`, and one recorded owner. Nothing is created by Unity's default
bootstrap; every system is `[DisableAutoCreation]` and exists only because a consumer
installed it into a specific `World`.

| Module | Bootstrap | Default scope | Installs | Uninstall does |
|---|---|---|---|---|
| Views | `DotsViewBootstrap.Install(world, registry[, scope])` | Root | `EntityViewRegistryReference` singleton + the full group tree + spawn/despawn/sync/overlay systems | recycles every view, hands each linked entity its `EntityViewRequest` back, removes the singleton; **systems stay, idle** |
| Simulation | `DotsSimulationBootstrap.InstallSimulationSystems(world[, scope])` | Root | move-toward, bounce, spin, health-death, time-to-live under the gameplay groups | destroys the five systems; groups stay |
| Camera follow | `CameraFollowBootstrap.Install(world, config[, scope])` | Session | `CameraFollowConfig` singleton + `CameraFollowSystem` after `ViewTransformSyncGroup` | destroys the singleton and the system |
| Physics movement (`Runtime.Physics`) | `PhysicsMovementBootstrap.Install(world[, scope, requirePhysicsPipeline])` | Session | `PhysicsMovementBridge` in `MovementSystemGroup` | destroys the bridge; groups stay |
| Physics events (`Runtime.Physics`) | `PhysicsEventsBootstrap.Install(world[, scope, publishers, requirePhysicsPipeline])` | Session | `PhysicsEventBuffer` singleton + `PhysicsEventCollectorSystem` after `PhysicsSimulationGroup` | destroys singleton and collector (its `OnDestroy` completes jobs, frees its lists) |
| View config catalog | `catalog.Build(...)` then `catalog.Install(world)` | Session | `ViewConfigTableReference` singleton | `catalog.Uninstall(world)` removes the singleton; `catalog.Dispose()` also frees the blob |
| Minimap | `MinimapBootstrap.Install(world[, plane, capacity, scope])` | Session | `MinimapBuffer` singleton (owns a `NativeList<MinimapEntry>`) + `MinimapDataSystem` after `EntityViewTransformSyncSystem` | releases the list (idempotent with the system's `OnDestroy`), destroys the singleton and the system; `MinimapMarker`s stay |
| Netcode adapter / prediction | `DotsNetcodeBootstrap` / `DotsPredictionBootstrap` | Session | see `NETCODE-INTEGRATION.md` | unchanged in this release; not yet recorded in `DotsModules` |

Overlays are part of the Views module (`ViewOverlaySystem` is created by `DotsViewBootstrap`); the
consumer contract for both feeds is `MINIMAP-OVERLAY.md`.

## Contract, one rule per line

- **Install twice is one installation.** Singletons are replaced, not duplicated; systems are
  `GetOrCreate`d; the module's `InstallCount` increments so a double install is visible.
- **Uninstall twice is safe**, and so is uninstalling a world that never had the module, or a
  world that is already disposed (`IsCreated == false`).
- **Install with a new instance replaces the old one.** For views: the old registry's views are
  recycled and their entities re-request, so they reappear under the new registry on the next
  lifecycle tick — never two views for one entity, never a link into a registry the world does
  not present through. For camera: the new `CameraFollowConfig` instance is the one referenced.
- **Validation runs at install, with an actionable error.** `CameraFollowBootstrap` rejects
  NaN/infinite offsets, negative `SmoothTime`, non-positive `MaxSpeed` (`ArgumentException`
  naming the field). `PhysicsMovementBootstrap` warns — or throws with
  `requirePhysicsPipeline: true` — when the world has no `PhysicsSystemGroup` to integrate the
  velocities it writes. Nothing is left half-installed on failure.
- **Ordering is verified, not assumed.** After any manual install call
  `SystemOrderVerifier.Verify(world)` (or `.Assert(world)`): it walks the three Unity root
  groups recursively and reports every `[UpdateInGroup]`/`[UpdateAfter]`/`[UpdateBefore]` the
  actual master update list violates — a system added to the wrong group, a group left unsorted.
  `MembersInUpdateOrder(world, group)` gives the exact sequence for a test.
- **Structural teardown happens between frames.** `Uninstall` uses `EntityManager` directly.
  Call it from scene teardown, a DI scope disposal or a test — never from inside a system.

## Ownership scopes

`DotsModuleScope.Root` — owned by the composition root, survives scene loads, torn down with the
world. `DotsModuleScope.Session` — owned by one scene or one connection, torn down on scene
unload / disconnect.

The scope is recorded **in the world**, on a `DotsModuleRecord` entity, together with the
module's uninstaller. That is what makes the next session safe: when the world is disposed the
records go with it, so there is no static table anywhere holding a stale `World`. Two worlds
side by side keep separate records, and a teardown in one cannot reach the other.

```csharp
// Scene reload: take down exactly the session-owned half.
DotsModules.UninstallScope(world, DotsModuleScope.Session);

// Permanent teardown: recycle managed views while a registry still exists, then dispose.
DotsModules.UninstallAll(world);
world.Dispose();

// Introspection
DotsModules.IsInstalled(world, CameraFollowBootstrap.ModuleName);
DotsModules.TryGetScope(world, DotsViewBootstrap.ModuleName, out var scope);
DotsModules.Installed(world); // names
```

`UninstallScope`/`UninstallAll` run uninstallers in reverse install order (dependents before
dependencies) and destroy any record an uninstaller left behind. Registering a module under a
different scope than it already has throws — one owner per module per world.

## Temporary disable vs. World disposal

| | Temporary (`X.Uninstall(world)`) | Permanent (`DotsModules.UninstallAll` + `world.Dispose()`) |
|---|---|---|
| Singletons | removed | removed |
| Managed views | recycled to the pool | recycled to the pool (by the uninstall, before disposal) |
| Systems | Views: stay created, idle. Camera / simulation / physics: destroyed | all destroyed by the world |
| Native containers | Views: untouched (the overlay list belongs to `ViewOverlaySystem`). Minimap: `Uninstall` releases its list | released in each system's `OnDestroy`, after `CompleteDependency()` (`ViewOverlaySystem`, `MinimapDataSystem`) |
| Re-install | resumes; entities re-request their views | new world, new install |

The order for permanent teardown matters and is the client's existing order: prediction, then
netcode adapter, then the session's mirrored entities, then `catalog.Dispose()` — the catalog
last because the systems that read its blob must be gone first. `DotsModules.UninstallAll`
before `world.Dispose()` covers the recorded modules; the catalog is disposed by whoever built it.

## DI (VContainer) ownership

`builder.RegisterDotsViews(viewRoot, world)` installs the view module at container build and
registers a `DotsViewsLifetime` singleton; **disposing that container uninstalls the module**
(`DotsViewBootstrap.Uninstall`: views recycled, requests handed back, record removed). Whichever
of the container and the world is disposed first, the other side is a safe no-op.
`Cuvara.DOTS.Tests.DI` proves resolution, root-scope ownership and the disposal path.

## Where the client wires this

Root scope (`RegisterDots` → `RegisterDotsViews`): registry, pools, provisioner,
`DotsViewBootstrap.Install` (Root). Session scope (`DotsWorldBridge`): catalog, simulation systems,
netcode adapter, prediction, and — when adopted — `CameraFollowBootstrap.Install(world, config)`
with the default Session scope, so `OnDestroy` can become one
`DotsModules.UninstallScope(world, DotsModuleScope.Session)` plus `catalog.Dispose()`.
