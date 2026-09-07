# Minimap and overlay data feeds

Two per-frame data feeds for a HUD, both produced in `ViewTransformSyncGroup` and both governed
by one rule: **a consumer can never see a stale entry.** The package collects positions; the host
projects and draws.

| | Overlays | Minimap |
|---|---|---|
| Opt-in per entity | `ViewOverlayAnchor` on an entity **with a view** | `MinimapMarker` on any entity with a `LocalToWorld` (a view is not required) |
| Buffer singleton | `ViewOverlayBuffer` (created lazily by `ViewOverlaySystem`, part of the Views module) | `MinimapBuffer` (created by `MinimapBootstrap.Install`) |
| Producer | `ViewOverlaySystem` | `MinimapDataSystem` |
| Entry identity | `ViewOverlayData.Entity` (+ `ViewId`) | `MinimapEntry.Entity` (+ `ViewId`, 0 when no view) |
| Runs when the set is empty | yes — buffer cleared, `Version` bumped | yes — same |
| Host owns | camera, world→screen, UI elements, recycling policy | map projection, icons per category, drawing |
| Contract helpers | `ViewOverlayProjection`, `ViewOverlayReconciler<T>` | none needed; entries are already 2D |
| Sample consumer | `Samples~/HybridViews/HudOverlaysSample.cs` | same file |
| Tests | `Tests/Editor/ViewOverlayConsumerTests.cs` | `Tests/Editor/MinimapModuleTests.cs`, `Tests/Editor.Netcode/NetworkMinimapTests.cs` |

Neither feed influences simulation: both read `LocalToWorld` after the transform sync and write
only their own buffer. Enabling or disabling them changes what a renderer sees and nothing else.

## Minimap

### Producer

`MinimapDataSystem` (internal, `[DisableAutoCreation]`, `ViewTransformSyncGroup` after
`EntityViewTransformSyncSystem`) rebuilds `MinimapBuffer.Entries` every update from every entity
carrying `MinimapMarker` + `LocalToWorld`:

```csharp
struct MinimapMarker : IComponentData { int Category; bool IsLocal; }        // the opt-in
struct MinimapEntry { Entity Entity; int ViewId; int Category; float2 Position; bool IsLocal; float HealthFraction; }
sealed class MinimapBuffer : IComponentData { NativeList<MinimapEntry> Entries; MinimapPlane Plane; uint Version; int Count; }
```

- `Position` is `LocalToWorld.Position` projected onto `MinimapBuffer.Plane` (`XZ` or `XY`, chosen
  at install to match the world's `SnapshotSpaceMapping`). Map-space projection — scale, centring on
  the local player, rotation — is the host's.
- `HealthFraction` is `Health.Current / Health.Max` (0–1) when the core simulation `Health` is
  present with `Max > 0`, else -1. Replicated hp lives on `NetworkEntityState`, which the core cannot
  name; a networked host reads it by `Entity`.
- `ViewId` is 0 for an entity without a view. **Presence on the map does not wait for the view**: an
  entity whose key is still warming is on the map at its true position.
- The system requires only the `MinimapBuffer` singleton, never a non-empty query, so the frame the
  last marked entity disappears is the frame `Count` reads 0. `Version` increments on every rebuild
  including empty ones.

### Only what the server replicated

The minimap lists *marked* entities, and nothing in the package marks an entity on its own. On the
netcode path the marker is placed by the drain, at spawn, when the view was constructed with an
`IMinimapCategoryResolver`:

```csharp
var view = new DotsEntityView(catalog, archetypes, SnapshotSpaceMapping.XZPlane,
    minimap: new TypeMinimapCategoryResolver(
        localCategory: 0,                                     // the local player, whatever its kind
        new TypeMinimapCategoryResolver.Rule("player", 1),
        new TypeMinimapCategoryResolver.Rule("mob", 2)));      // "npc", "item"… stay off the map
```

A mirror entity exists exactly while the server lists the id, so a marker on a mirror exists exactly
that long: an entity the area of interest omitted has no mirror, therefore no marker, therefore no
entry — and no host code can change that through this interface. The host decides *which replicated
kinds* show and as *what category*; it cannot widen the set beyond what was replicated. A host that
marks its own local-only entities (a quest marker, a waypoint) is making a deliberate choice about
its own data, not leaking the server's.

### Install / uninstall (module: `Minimap`, default scope `Session`)

```csharp
MinimapBootstrap.Install(world, MinimapPlane.XZ);            // buffer + producer; idempotent
var minimap = MinimapBootstrap.InstalledBuffer(world);       // read Entries in LateUpdate
MinimapBootstrap.Uninstall(world);                           // releases the NativeList, destroys singleton + system
```

- Install twice: one buffer, same allocation (a consumer's reference stays valid), plane follows the
  newest call, `InstallCount` = 2.
- Uninstall: completes the producer's dependency, disposes `Entries`, sets it to `default` so a held
  `MinimapBuffer` reads `Count == 0`, removes the singleton, removes and destroys the system, drops
  the module record. Safe twice; safe on a world that never had it. `MinimapMarker` components stay
  on their entities — they are the marker's owner's.
- World disposed without an uninstall: `MinimapDataSystem.OnDestroy` disposes the list. Both paths
  go through `MinimapBootstrap.ReleaseEntries`, which is idempotent, so either order is fine.
- `DotsModules.UninstallScope(world, Session)` takes it down with the rest of the session;
  `DotsModules.UninstallAll(world)` before `world.Dispose()` is the permanent path.

### Consumer cadence

Read `MinimapBuffer.Entries` after `PresentationSystemGroup` (`LateUpdate`, or `OnGUI`) and before
the next frame's `InitializationSystemGroup`. Do not hold an enumerator across frames; do not dispose
or resize the list. Compare `Version` to skip redraws when nothing was rebuilt.

## Overlays

### Producer

`ViewOverlaySystem` (Views module, `ViewTransformSyncGroup` after the sync) rebuilds
`ViewOverlayBuffer.Entries` from every entity carrying `EntityViewLink` + `LocalToWorld` +
`ViewOverlayAnchor`:

```csharp
struct ViewOverlayAnchor : IComponentData { float3 WorldOffset; }            // the opt-in; e.g. (0, 2, 0)
struct ViewOverlayData { Entity Entity; int ViewId; float3 WorldPosition; float HealthFraction; }
sealed class ViewOverlayBuffer : IComponentData { NativeList<ViewOverlayData> Entries; uint Version; int Count; }
```

`WorldPosition = LocalToWorld.Position + rotate(LocalToWorld.Rotation, WorldOffset)`.
`HealthFraction` is always -1 here (the host fills it from its own source). **Fixed in this
release:** the system used to require a non-empty anchored query, so the last entry stayed in the
buffer after its entity despawned; it now requires only the registry singleton and clears (and bumps
`Version`) on the frame the set empties.

The buffer's `NativeList` is owned by `ViewOverlaySystem` and released in its `OnDestroy` (world
disposal). `DotsViewBootstrap.Uninstall` leaves it allocated and the system idle — a re-install
resumes writing into the same list.

### Consumer contract

| Question | Answer |
|---|---|
| **Who converts world → screen?** | The host, with **its** camera — the package does not know which camera draws a world or whether there is one. The rule lives in `ViewOverlayProjection.Project(camera, worldPosition, maxDistance)` so every consumer applies it identically. |
| **Behind the camera?** | Hidden, never mirrored. A point at or behind the near plane projects to a spot in front of the player that nothing stands on; `Project` returns `ViewOverlayVisibility.BehindCamera`. |
| **Distance filtering?** | `maxDistance` in world units, measured from the camera position; `TooFar` when exceeded; `≤ 0` disables. Size/alpha falloff uses `ViewOverlayPlacement.Distance`. |
| **Off the screen edge?** | Still `Visible` with out-of-range screen coordinates, so a name plate slides off rather than pops; clip in the UI. |
| **No camera?** | `NoCamera`; the reconciler hides everything and throws nothing. |
| **Refresh cadence?** | The buffer is rebuilt once per frame in `ViewTransformSyncGroup`. Read it after `PresentationSystemGroup` (`LateUpdate`). `ViewOverlayReconciler.Sync` is gated on `Version`, so calling it from `OnGUI` (several times per frame) does the work once. |
| **UI instance recycling?** | One element per **entity** (`ViewOverlayData.Entity`, version included), not per `ViewId` — a recycled-and-reacquired view keeps its plate; a despawn and respawn into the same entity index are two plates. Acquired on first sight, kept while hidden, released the frame the entity leaves the buffer or the buffer is released. |
| **Which UI library?** | Any. `IViewOverlayPresenter<TElement>` is four methods over a type the host chooses; `Runtime` names no UI package. |

```csharp
sealed class PlatePresenter : IViewOverlayPresenter<VisualElement> { /* Acquire / Place / Hide / Release */ }

var plates = new ViewOverlayReconciler<VisualElement>(new PlatePresenter(root));
void LateUpdate() => plates.Sync(overlayBuffer, Camera.main, maxDistance: 60f);
void OnDestroy() => plates.Clear();
```

`Sync` returns true when it did work. It acquires for new entries, calls `Place` for visible ones
and `Hide` for hidden ones, releases what left, and reports `VisibleCount` / `HiddenCount` /
`ElementCount`. Passing a null or released buffer releases every element.

## 2D sorting — decision

`ViewSortingKey` / `ViewConfig.SortingLayerId` / `SortingOrder` remain **carried and unsupported**;
no `SpriteRenderer` is written. Rationale: no consumer of the package renders sprites (the client is
a 3D XZ world; every sample uses primitives), a correct implementation needs a per-view renderer
lookup or cache in the managed sync pass and a decision about root vs. child renderers vs. sorting
groups that only a real 2D prefab can answer, and the value is static per config — a one-line write
at spawn once a consumer exists. Implementing and testing it against nothing is the speculative
work the improvement plan (§8, E07) rules out. Status is recorded in `SUPPORT-MATRIX.md` and in the
component's own remarks; the fields stay so the config asset format does not have to reopen later.

## What is not here

- Map projection, icon atlases, fog of war, rotation with the camera — host.
- Health on the minimap for replicated entities — read `NetworkEntityState` by `Entity`.
- Overlay culling beyond behind-camera and distance (occlusion, screen-density limits) — host, or
  a later E01 item.
- Any dependency on UI Toolkit, UGUI or TextMeshPro in `Runtime`.
