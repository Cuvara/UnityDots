# Phase B Showcase

Four runnable scenes covering the DOTS improvement plan shipped in v0.28.0 (D01–D14). That release
landed the code and the unit tests but no scenes, so nothing in it could be *watched* working. These
scenes close that gap: they are the acceptance step for the plan.

Everything here runs **offline** — no backend, no gateway, no netcode connection. Every snapshot,
collision and chunk load is a scripted local call, so any of the four scenes can be opened and
played on its own.

The UI is UI Toolkit (UXML + USS + one `PanelSettings` asset). Each scene is a camera, a light and a
single bootstrap GameObject carrying a `UIDocument` and one plain `MonoBehaviour`.

## Import

Package Manager → Cuvara DOTS → Samples → **Phase B Showcase** → Import. The scenes land under
`Assets/Samples/Cuvara DOTS/<version>/Phase B Showcase/Scenes/`.

Two of the four scenes are gated on optional packages and are simply inert without them:

| Scene | Assembly | Requires |
|---|---|---|
| `PoolAndChunks`, `ModulesAndConfig` | `Cuvara.DOTS.Samples.PhaseBShowcase` | nothing beyond the package |
| `LifecycleEventsAndMinimap` | `…PhaseBShowcase.Netcode` | `com.cuvara.netcode` ≥ 0.31.0 (`CUVARA_NETCODE`) |
| `PhysicsEvents` | `…PhaseBShowcase.Physics` | `com.unity.physics` ≥ 1.0.0 (`CUVARA_DOTS_PHYSICS`) |

The gating mirrors `Cuvara.DOTS.Physics` and `Cuvara.DOTS.Netcode` exactly: a `versionDefine` on the
optional package plus a matching `defineConstraint`. In a project without the package the assembly
compiles to nothing and the scene opens with a missing script rather than an error.

---

## 1. `Scenes/PoolAndChunks.unity` — D02 / D03

`PooledViewAssetProvider` and `ChunkViewProvisioner`. Every counter these types gained is on screen,
because in production each of these paths is *silent* — that is exactly why they were given counters.

The pool is built with `maxActivePerKey: 4`, `maxPoolSize: 6`, over three primitive prefabs created
at `Start`.

| Button | What it does | What to expect |
|---|---|---|
| Acquire / Release cube, sphere | One lease in or out | per-key `active` / `pooled` move; released instances go back to the pool, not to `Destroy` |
| Duplicate release | Releases one instance twice | `DuplicateReleaseCount` +1, and `pooled` gains **one** entry, not two |
| Foreign release | Hands the pool an object it never issued | `ForeignReleaseCount` +1; the object stays active and untouched in the scene |
| External destroy | `DestroyImmediate` on a live acquired instance | nothing changes yet — the provider does not know |
| `SweepDestroyed()` | Reconciles | returns the number found; `ExternallyDestroyedCount` catches up and `active` is corrected |
| Exceed maxActivePerKey | Asks for 6 spheres with a cap of 4 | 4 granted, 2 `null` returns, `AdmissionRejectedCount` +2 |
| Dispose: Destroy / Detach | Throwaway provider disposed with one lease outstanding | Destroy → instance is gone; Detach → instance is still alive and now the holder's. **Both log a warning naming the outstanding count — that warning is expected.** |
| Warm A / Warm B | Two chunks sharing the `sphere` key | ref counts rise; `ChunkStates` shows `Warm` |
| Release A / Release B | Releases one chunk | only keys this chunk was the *last* referencer of are released — `sphere` survives A's release while B holds it |
| Release while warming | Starts a 1200 ms warm, releases immediately | the late completion finds its epoch stale: no `ChunkWarmed`, the chunk does not come back |
| Cycle ×5 | Warm and release both chunks five times | `chunks=0 trackedKeys=0 refs(sphere)=0` — back to baseline |

The slow warm is real: `PooledViewAssetProvider.PrewarmAsync` completes synchronously, so
`DelayedViewAssetProvider` wraps it with an artificial delay to stand in for an Addressables-backed
provider. Without it the release-while-warming case is unreachable from a button.

**Prewarm is what creates a pool.** Only `PrewarmAsync` creates a key's pool queue, and
`ReleaseInstance` parks an instance only when a queue exists for its key — otherwise the instance is
destroyed and its lease dropped. A provider whose keys are registered but never prewarmed therefore
behaves like a plain factory: nothing is recycled, and releasing the same instance twice is counted
as a *foreign* release rather than a duplicate one, because the lease is already gone. The scene
prewarms every key at start for exactly this reason.

**Dispose reclaims on the next frame.** The provider destroys through `Object.Destroy`, which Unity
defers to the end of the frame in a player, so an instance the `Destroy` policy has just reclaimed
still compares non-null on the same frame. Both dispose buttons therefore report their verdict one
frame later — read it sooner and the two policies look identical.

## 2. `Scenes/ModulesAndConfig.unity` — D04 / D05

Two throwaway `World`s, created at `Start` and disposed at `OnDestroy`. The default world is never
touched.

| Button | What to expect |
|---|---|
| Install in A / B | Installs `Minimap` plus a hand-registered module. **Click twice**: `InstallCount` goes 1 → 2 while the module list stays the same length — the registry is idempotent by name |
| UninstallAll A / B | Returns the count removed; the *other* world's list is unchanged. That is the per-world isolation claim, and it is the whole reason module state lives in the world rather than in a static |
| Re-install under another scope | The one re-install that is refused: same name, different `DotsModuleScope` → `InvalidOperationException`, message shown |
| `SystemOrderVerifier.Verify` | Walks the real master update list of world A. An **empty list is the good result** and is spelled out as such |
| Validate valid / broken library | The full `ViewConfigValidationReport`. The broken library carries one defect per issue code: `DuplicateName`, `EmptyName`, `MissingConfig`, `EmptyViewKey`, plus `MissingPrefab` from the prefab probe |
| Build catalog | `TryBuild`; `Version` becomes 1 |
| Issue ref | `CreateRef` through the catalog — the only supported way to make a valid one |
| Rebuild | `Version` bumps, and the held ref is now stale |
| Check held ref | The held ref is refused on version mismatch, and a hand-made `new ViewConfigRef { Index = 0 }` is refused always, because Version 0 is the unstamped value no built table ever has |

A stale ref is **not** an exception at spawn time: `EntityViewSpawnSystem.TryResolveConfig` logs a
warning and falls back to the request's own `ViewKey`, so a rebuild degrades a view instead of
dropping it. The scene shows the comparison the spawn system performs.

## 3. `Scenes/LifecycleEventsAndMinimap.unity` — D06 / D08 / D09

A scripted wire sequence through `DotsEntityView`, driven exactly as `WorldViewBinder.Tick` would
drive it for each wire event — but with no socket anywhere. Press **Autoplay** to watch the whole
sequence, or step it by hand.

| Button | Wire event it stands for | Expected lifecycle event |
|---|---|---|
| Full snapshot | keyframe: `Spawn` + `SetState` per id | one `Spawned` per id |
| Delta | `SetState` only | **none** — a delta must not churn lifecycle |
| AOI exit `uuid-b` | the world stops listing the id | `Despawned(Despawned)` — not a death |
| AOI re-entry `uuid-b` | `Spawn` again | `Spawned`, with a **new** `Entity` for the same id |
| Reconnect + `BeginGeneration` | stamp a new generation, then the new session's keyframe | live mirrors despawn with `SessionReset`, and commands still queued from the old session are dropped rather than applied to the new one |
| Destroy mirror entity | destroys the entity behind the adapter's back, then sends one more state for that id | `Despawned(ExternalDestruction)` — the one reason no wire message produces |
| Teardown | `Uninstall(destroyMirrors: true)` | one `Despawned(Teardown)` per live id |
| Reinstall adapter | fresh `DotsEntityView` + fresh subscriptions | after a teardown the old view still believes its ids are live, so it is replaced, not reused |

**External destruction is detected on the next command, not on the destruction.** The drain has no
callback for an entity disappearing; `ApplySpawn` and `ApplyState` both check `IsLiveMirror`, and
teardown checks it too. So a scene that destroys a mirror and then goes quiet about that id will
never see the reason fire — there is nothing for the drain to notice. This step therefore sends one
more state for the id after destroying it, which is also what a real server does: it has no idea the
client destroyed anything.

`BeginGeneration` is called *before* anything is despawned, deliberately. Despawning every id
first would leave nothing alive by the time the drain reached the reset, so the reset would have
nothing to report and `SessionReset` would never appear — which is the one reason this step exists
to show. A real reconnect has this shape anyway: the session is dropped, then a new keyframe arrives.

Camera follow: **Follow local / Follow uuid-a** (switch), **No target** (the camera holds its last
pose), **Two targets** (under `HoldAndReport` it refuses to pick; toggle to `FollowLowestIndex` to
see it choose), **Teleport local** (a 30-unit server jump, past `TeleportDistance` 15, so the camera
snaps and zeroes its velocity), **ResetSmoothing**.

Also live: the `MinimapBootstrap` buffer rendered as a UI Toolkit minimap centred on the local
player, and `ViewOverlayReconciler` name plates tracking each entity.

Targeting is data-driven — there is no `SetTarget` call. The `CameraFollowTarget` tag is added to
and removed from the mirror entity, and teleport handling is `CameraFollowConfig.TeleportDistance`,
not an API call.

## 4. `Scenes/PhysicsEvents.unity` — D07

Runs in the **default world**, because that is the world with a real physics pipeline; a hand-made
world has no `PhysicsSystemGroup` and the collector would idle forever. Both bootstraps are
installed with `requirePhysicsPipeline: true` so a missing pipeline throws instead of warning.

| Button | What to expect |
|---|---|
| Install / Uninstall scope | `PhysicsEvents` and `PhysicsMovement` through `DotsModules`; `UninstallScope(Session)` returns the count |
| Cycle ×5 to baseline | five install/uninstall cycles with the bodies torn down → `blobs=0 leases=0`, the check that the modules really let go |
| Build stage | a static box and a static trigger zone. A trigger only fires against a dynamic or kinematic body — two statics never do |
| Drop ball on box | `Enter` → `Stay` … → `Exit`, with the canonical pair order (`A` is the lower index), `swapped`, and the **aggregated** `ContactCount` — how many Unity.Physics events folded into one |
| Drop into trigger zone | trigger `Enter` / `Exit` |
| Destroy mid-contact | `Exit` with `AnyEntityDestroyed=true`, then a replacement claims the freed index with a **higher version** — a different entity, so a fresh pair and a fresh `Enter` |
| Physics-driven mover | kinematic body: `PhysicsVelocity` + `PhysicsDrivenMovement`. Unity.Physics integrates it, the package's own movers skip it |
| Direct mover | `MoveData` and no body: the package's movement systems own the transform |
| Check integrators | `AssertSingleIntegrator` over both — each has exactly one |
| Break the guard | strips the tag off a physics body, leaving a velocity Unity.Physics integrates and nothing to keep the package's movers off it. The guard throws and names the entity; the tag is restored immediately |

Collider leases are returned by reading `PhysicsCollider` back off the entity before destroying it.
The library counts leases; it does not watch entities, so skipping that leaks a blob.

---

## Headless self-test (`-showcaseAutorun`)

A headless run can build a scene and see its panel, but it cannot click — so the scenarios behind
the buttons, the part actually worth proving, would go unexercised. Every scene therefore drives its
own buttons on demand.

```bash
<player> -batchmode -showcaseAutorun -showcaseAutorunDelay 1
```

- `-showcaseAutorun` runs the scene's buttons in a scripted order after start, asserts the outcomes
  documented above, then quits: **exit code 0** if every assertion held, **1** otherwise.
- `-showcaseAutorunDelay <seconds>` sets the pause between steps (default `1`). Parsed with the
  invariant culture, so a build agent with a comma decimal separator still accepts `0.5`.
- Without the flag nothing changes: the scenes behave exactly as they do interactively.
- No backend, in either mode.

The log lines are a CI contract — fixed and greppable. Per step:

```
[PhaseB] <Scene> step=<step> expected=<label>:<value> actual=<value> PASS|FAIL
```

and one summary line per scene, which is the line to assert on:

```
[PhaseB] PoolAndChunks: 22 passed, 0 failed
[PhaseB] ModulesAndConfig: 27 passed, 0 failed
[PhaseB] LifecycleEventsAndMinimap: 16 passed, 0 failed
[PhaseB] PhysicsEvents: 17 passed, 0 failed
```

`<Scene>` is exactly the scene file name without its extension. Failing steps are logged through
`Debug.LogError`, so the same line is still emitted — grep the line, not the log level. Do not
reword these strings without updating whatever asserts on them.

Waits that depend on the physics pipeline are bounded (10 s): a world that never steps fails the
run rather than hanging it.

---

## Notes

- Bootstraps are plain `MonoBehaviour`s so Play mode is all that is needed. Nothing requires DI,
  VContainer, GameFoundation or Addressables.
- Lifecycle events are raised from inside the drain system's update, so the scene queues the
  structural changes it wants and applies them from `LateUpdate` — adding a component from the
  callback would be a structural change mid-update.
- Counters are cumulative and deliberately not cleared by **Reset all**; the point is to watch them
  climb.
