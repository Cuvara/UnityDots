# View Provisioning

The hybrid view layer connects ECS entities to pooled GameObjects. This document
covers the provisioning lifecycle, configuration, the chunk-aware pool, and the
ownership, disposal and cancellation contracts of the shipped providers.

## View lifecycle

```
Entity created
  → AddComponent<EntityViewRequest>("goblin")
  → EntityViewSpawnSystem picks it up
  → IViewAssetProvider.Acquire("goblin") → pooled GameObject
  → EntityViewLink added (entity ↔ GameObject)
  → EntityViewTransformSyncSystem syncs LocalTransform → Transform every frame

Entity destroyed / despawned
  → EntityViewDespawnSystem detects missing EntityViewLink source
  → IViewAssetProvider.ReleaseInstance(instance) → back to pool
  → ViewDespawned message published
```

## ViewConfig

Author a `ViewConfig` ScriptableObject per view kind:

| Field | Purpose |
|-------|---------|
| `Key` | Archetype name matching the server's entity type |
| `Prefab` | GameObject to instantiate (or pool) |
| `PoolSize` | Initial pool count per chunk warm |
| `PositionOffset` | Per-art offset applied to the view instance |
| `ScaleOverride` | Optional scale override |

List configs in a `ViewArchetypeLibrary` and build the catalog at session start:

```csharp
var catalog = new ViewConfigCatalog();
catalog.Build(library);
catalog.Install(world);  // publishes ViewConfigTableReference singleton
```

## Chunk provisioning

`ChunkViewProvisioner` manages view assets per world chunk:

```csharp
// The sink is required: it is what makes ReleaseChunk safe (see below).
var provisioner = new ChunkViewProvisioner(provider, new EntityViewCascade(world, registry));

// Warm assets for a chunk
await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { "goblin", "torch" }, countPerKey: 8);

// Release when chunk unloads — cascade-despawns standing views first
var result = provisioner.ReleaseChunk("chunk-12-4");
// result.KeysReleased, result.ViewsDespawned
```

**Shared keys survive**: if two chunks both use "goblin", releasing one chunk does
not release the key while the other still holds it. A key is counted once per chunk,
never once per occurrence.

**Cascade release**: when a chunk is released, any entity views standing on its
expiring keys are despawned before the assets are returned. A `ChunkCascadeReleased`
message reports how many views were affected. Their entities survive without a view
and are not respawned unless something requests a view again.

### The cascade sink is mandatory

`ChunkViewProvisioner` throws `ArgumentNullException` if constructed without an
`IViewCascadeSink`. Reference counting tracks *chunks*; a live view is held by an
entity the provisioner has never heard of. Without a sink a release destroys pooled
instances that are on screen while the registry keeps their handles and the entities
keep an `EntityViewLink` that can never resolve. That is the ordinary streaming path,
not an edge case, so the configuration is refused up front.

| Context | Sink to pass |
|---------|--------------|
| Streaming world with entity views | `EntityViewCascade` (what `RegisterDotsViews` wires) |
| Warm-only preloader, editor tooling, tests over a recording provider | `NullViewCascadeSink.Instance` |

`NullViewCascadeSink` exists so the decision is a named type a reviewer can grep for,
not a `null` that might have been an accident.

### Chunk states and legal transitions

`ChunkStates[chunkId]` and `OnChunkStateChanged` expose the per-chunk lifecycle. Every
transition happens on the main thread.

| From | To | Trigger |
|------|----|---------|
| untracked | `Warming`, or `Warm` if every key was already warm | `PrewarmChunkAsync` |
| `Warming` | `Warm` | the prewarm that opened this epoch completed |
| `Warming` | `Failed` → untracked | that prewarm faulted or was cancelled; references rolled back |
| `Warming` / `Warm` | `Warming` / `Warm` | re-warm of the same id (a diff; new epoch) |
| any tracked | `Released` → untracked | `ReleaseChunk`, `ReleaseAll`, `ReleaseSessionKeys` |

`Pending` is reserved and never emitted: intake is synchronous, so a chunk is
`Warming` the instant the call returns. `Released` and `Failed` chunks leave the
`ChunkStates` table.

### Epochs: why a stale completion cannot resurrect a chunk

Every `PrewarmChunkAsync` stamps the chunk with a fresh epoch *before* it awaits the
provider. When the await returns, the chunk is marked `Warm` and `ChunkWarmed` is
published only if the chunk is still tracked with that same epoch. A `ReleaseChunk`
or a second `PrewarmChunkAsync` for the same id that landed in the meantime changed or
removed the epoch, so the older completion finds a mismatch and does nothing: no
state change, no event, no counter touched. Releasing a chunk that is still warming
is therefore legal and complete the moment it returns.

### Failure and retry

If a provider prewarm faults or is cancelled:

- **Epoch still current** — the chunk's references are rolled back through the ordinary
  release path (cascade included, in case a synchronous `Acquire` fallback spawned
  something mid-load), the keys this attempt tried to warm are marked not-warm so the
  next requester re-issues the load rather than trusting the count table, the chunk
  transitions to `Failed` and leaves the table, `ChunkReleased` is published, and the
  exception propagates to the awaiting caller. Retry is a plain `PrewarmChunkAsync`
  with the same id.
- **Epoch superseded** — the chunk was released or re-warmed while the failed load was
  in flight. Nothing is rolled back, because the newer operation owns the chunk's
  state; the exception still propagates to the caller that awaited the old load.

Reconciliation invariant: after any sequence of warm and release calls whose awaits
have all settled, `TrackedKeyCount` equals the number of keys some tracked chunk
still lists, `ChunkStates` holds exactly the tracked chunks, and reference counts are
never negative.

`PrewarmChunkAsync` checks its `CancellationToken` before touching any state, so a
pre-cancelled call returns a cancelled task and changes nothing.

Accepted limitation: a key already referenced by another chunk is treated as warm
even while its load is still in flight, and a chunk whose keys were all "already warm"
is marked `Warm` immediately. Use `IsChunkLoaded` for "spawns will not be deferred"
and `IViewAssetProvider.IsWarm` for a per-key answer.

### Chunk-owned versus session-owned assets

Keys a whole session needs regardless of where the player stands — the local player,
common projectiles, UI-attached views — must not be dropped by a chunk release. Pin
them:

```csharp
await provisioner.PinSessionKeysAsync(new[] { "player", "arrow" }, countPerKey: 4);
// ... chunks come and go; a chunk that also lists "arrow" can never release it ...
provisioner.ReleaseSessionKeys(); // at session end; cascades like any release
```

A pin is a reference held under the reserved id `ChunkViewProvisioner.SessionId`.
`ChunkCount` excludes it, `PrewarmChunkAsync`/`ReleaseChunk` refuse the reserved id,
and `ReleaseAll()` — meant for scene teardown — leaves it alone unless called as
`ReleaseAll(includeSession: true)`. Views on a pinned key come down through the
cascade before its last reference goes, exactly as for a chunk.

### Thread affinity

Every public member must be called on the thread that constructed the provisioner
(the main thread) and throws `InvalidOperationException` otherwise. Provider loads may
run anywhere; the continuation after the await is where the provisioner touches Unity
state, and Unity's `SynchronizationContext` brings it back to the main thread. The
post-await path asserts this too, so a provider that completes on a worker thread in
an environment without that context fails loudly instead of mutating the pool
off-thread.

## IViewAssetProvider

The interface the spawn system calls. Three implementations ship:

| Implementation | Source | When to use |
|----------------|--------|-------------|
| `PrimitiveViewAssetProvider` | `Runtime/` | Dev/test — creates Unity primitives |
| `PooledViewAssetProvider` | `Runtime/Provisioning/` | Registered prefabs with SetActive pooling; no asset pipeline |
| GameFoundation provider | `Runtime.GameFoundation/` | Production — uses `IAssetsManager` + `IObjectPoolManager` |

Implement your own if you have a different pool or asset system. Prefer one pool owner
per prefab key: a host adapter should wrap an existing pool rather than run a second
one behind this interface.

## PooledViewAssetProvider contracts

### Identity and ownership

Every instance the provider hands out was instantiated by the provider, and only those
are ever deactivated, re-parented or destroyed by it. Identity is a per-instance lease
(key, prefab registration generation, acquired/pooled). `GameObject.name` is set for
the hierarchy view and nothing reads it back — renaming an instance changes nothing.
`IsOwned`, `TryGetKey` and `IsAcquired` answer from the lease table.

| Return of… | Policy | Counter |
|------------|--------|---------|
| an acquired instance | deactivated, parked under the pool root, pooled (or destroyed if the pool is at cap, the key was released, or the prefab was replaced) | — |
| an instance already pooled | ignored; it can never be enqueued twice, so two later acquires can never share it | `DuplicateReleaseCount` |
| an instance the provider did not create | left untouched — not deactivated, not re-parented, not destroyed | `ForeignReleaseCount` |
| an instance destroyed externally | lease dropped, counts reconciled | `ExternallyDestroyedCount` |
| anything after `Dispose` | no-op, so a despawn system that tears down after the pool does not throw | — |

`SweepDestroyed()` drops every lease whose GameObject was destroyed externally,
pooled or acquired, and returns how many. Call it after a scene unload that may have
taken views with it. `Acquire` also skips destroyed pooled instances on its own.

### Roots

| `poolRoot` argument | Owner | On `Dispose` |
|---------------------|-------|--------------|
| `null` | provider creates a hidden root (`OwnsPoolRoot == true`) | destroyed |
| a caller's transform | caller (`OwnsPoolRoot == false`) | emptied of the provider's instances; never destroyed |

If a caller-owned root is destroyed while the provider is alive, returned instances
park at the scene root instead; nothing throws.

### Re-registration

`RegisterPrefab(key, prefab)` with the same prefab is a no-op. With a *different*
prefab it replaces the key: pooled instances of the old prefab are destroyed (they
would render the wrong thing), the key is no longer warm until re-prewarmed, and
instances currently acquired keep running but are destroyed rather than pooled when
returned, because the pool now belongs to the new prefab.

`Release(key)` destroys the key's pooled instances and un-warms it; the prefab stays
registered (this provider registers rather than loads, so there is nothing to
unload). Acquired instances of the key are destroyed when returned. Callers that need
live views gone first run them through the cascade — `ChunkViewProvisioner` does.

### Disposal

`Dispose` destroys every pooled instance, reclaims outstanding acquired instances per
`OutstandingLeasePolicy`, destroys the root if the provider owns it, and clears all
state. It is idempotent.

| `OutstandingLeasePolicy` | Outstanding acquired instances at dispose |
|--------------------------|-------------------------------------------|
| `Destroy` (default) | destroyed — a disposed provider leaves no GameObject of its own behind |
| `Detach` | left alive and forgotten; the holder now owns them |

Either way the count of outstanding leases is logged as a warning: an open lease at
dispose is a despawn the host forgot, and it is never lost silently.

After `Dispose`, `RegisterPrefab`, `PrewarmAsync`, `Acquire` and `AcquireAsync` throw
`ObjectDisposedException`; `ReleaseInstance` and `Release` are no-ops; `IsWarm` is
false and every count is zero.

### Cancellation

`PrewarmAsync` returns a cancelled task before instantiating anything if the token is
already cancelled, and checks the token between instantiations. Instances created
before a mid-loop cancel are tracked and pooled — nothing leaks — but the key is not
marked warm. `AcquireAsync` likewise returns a cancelled task before doing any work.

### Pool cap versus admission budget

These are two different limits and only one of them bounds memory:

| Constructor argument | Bounds | On overflow |
|----------------------|--------|-------------|
| `maxPoolSize` | *inactive* instances kept per key | a returned instance is destroyed instead of pooled |
| `maxActivePerKey` | *acquired* instances per key (0 = unlimited, the default) | `Acquire` returns `null`, `AdmissionRejectedCount` increments, nothing is instantiated |

Total instances per key = acquired + pooled (`TotalInstanceCount` overall). With the
default unlimited budget the provider instantiates as many views as callers ask for,
which is correct for a server-authoritative client that must show every entity in its
area of interest. A host that wants a hard ceiling sets `maxActivePerKey` and treats
`null` from `Acquire` as "not admitted" — the same path an unregistered key already
takes, so the spawn system needs no new handling.

### Lifecycle hook

Pass an `IPooledViewLifecycle` to reset reusable state without the provider knowing
what a view contains. `OnAcquired(key, instance)` runs after the instance is
parented, placed and activated; `OnReleased(key, instance)` runs when an acquired
instance is returned, *before* it is deactivated — and also when it is about to be
destroyed rather than pooled, so a hook sees every acquired instance exactly once more.
A hook must not acquire or release other instances.

| `PooledViewAssetProvider` | `Runtime/Provisioning/` | Package-owned SetActive pool over prefabs you register. Identity/ownership fixes in progress on `feat/pool-chunk` (D02). |
| `PrimitiveViewAssetProvider` | `Samples~/HybridViews/` (**sample-only**; a second copy, `PrimitiveViewProvider`, in `Samples~/NetworkedPrediction/`) | Dev/test — creates Unity primitives |
| `GameFoundationViewAssetProvider` | `Runtime.GameFoundation/` | UniT `IAssetsManager` + `IObjectPoolManager`. Compile-checked only; no test exercises it. |

Implement your own if you have a different pool or asset system — the client project does.

## Sorting keys

`ViewSortingKey` is **carried, not applied**. `EntityViewSpawnSystem` copies a config's
sorting layer/order onto the entity and nothing in the package writes it to a
`SpriteRenderer` — the 2D branch is a planned item. Treat the component as authoring data
until `SUPPORT-MATRIX.md` says otherwise.
