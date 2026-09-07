# Network entity lifecycle events

`NetworkEntitySpawned` and `NetworkEntityDespawned` report when a replicated id becomes **present**
in, and **absent** from, one ECS world. They are published by the netcode adapter's drain system and
delivered through `DotsEntityView.Lifecycle`, a `NetworkEntityLifecycle`.

This document is the contract. `Tests/Editor.Netcode/NetworkLifecycleEventTests.cs` asserts every
sequence below; `Samples~/NetworkedPrediction/NetworkLifecycleLog.cs` is the reference consumer.

## Two lifecycles, not one

| | Network presence | Visual presence |
|---|---|---|
| Event | `NetworkEntitySpawned` / `NetworkEntityDespawned` (`Cuvara.DOTS.Netcode`) | `ViewSpawned` / `ViewDespawned` (`Cuvara.DOTS.Messaging`) |
| Means | a mirror entity carrying `NetworkEntity` exists for the id | a pooled GameObject is linked to *some* entity via `EntityViewLink` |
| Produced by | the drain (`SnapshotApplyGroup`, in `InitializationSystemGroup`) | `EntityViewRegistry` (`ViewLifecycleGroup`, in `PresentationSystemGroup`) |
| Identity | wire id (string) + `Entity` with version | `ViewId` (int handle) + view key |
| Cardinality | one spawn and one despawn per **life** of an id | one per acquire/release of a GameObject — a chunk release, an external `Destroy`, or a warm-later key can each add pairs while the entity stays present |

**AOI exit is not death.** The wire does not distinguish an area-of-interest exit from a server-side
removal: `WorldViewBinder` derives despawn from absence and calls `IEntityView.Despawn` in both
cases. `NetworkEntityDespawned.Reason` therefore says what the *adapter* knows (wire despawn, external
destruction, teardown) and never whether the entity died. Read `NetworkEntityState.Hp` before the
despawn, or wait for an explicit server event, if death matters.

**View culling is not entity removal.** A view may be released (chunk cascade, sweep of an externally
destroyed GameObject) and re-acquired while the network entity stays present. No network event fires
for that.

## Payload

```csharp
readonly struct NetworkEntitySpawned   { string EntityId; string EntityType; bool IsLocal; Entity Entity; }
readonly struct NetworkEntityDespawned { string EntityId; string EntityType; bool IsLocal; Entity Entity; NetworkDespawnReason Reason; }
enum NetworkDespawnReason : byte { Despawned, ExternalDestruction, Teardown }
```

- `EntityId` is the wire id verbatim (the 61-byte `FixedString64Bytes` limit is enforced at spawn).
- `EntityType` is read back from `NetworkEntity.Type` and is clipped at 29 bytes exactly as that
  field is; archetype resolution ran on the full string before the entity existed.
- `Entity` carries index **and version**. The despawn's `Entity` equals the spawn's for the same
  life, so a cached handle can be matched even after the index is reused by a later entity.

## When, exactly

All events are published **synchronously inside the drain**, on the thread that updates
`NetcodeSystemGroup` (the main thread in every supported configuration), one command at a time:

| Event | Published… | Entity state during handlers |
|---|---|---|
| `Spawned` | after every adapter-owned component is on the entity and the drain's map records it | exists, complete, queryable by `NetworkEntity.Id` |
| `Despawned(Despawned)` | when a wire `Despawn` is applied, **before** `DestroyEntity` | exists — last `LocalTransform`, `NetworkEntityState` readable; destroyed as soon as the last handler returns |
| `Despawned(Teardown)` | from `DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true)`, before the destroy | exists, as above |
| `Despawned(ExternalDestruction)` | on the next command for an id whose entity something else destroyed (a `State`, a `Despawn` is *not* needed), or during teardown | **not a mirror any more**: gone, or a shell stripped to cleanup components with no `NetworkEntity`; check `HasComponent<NetworkEntity>` in a handler that serves all reasons |

The drain does no work while a handler runs, so the world a handler sees is exactly the world the
drain left. `LocalToWorld` is seeded at spawn, so a spawn handler reading position gets the mapped
origin (the first `State` has not been applied yet when a spawn is published, even if it is queued
behind it — that is command order).

**How "destroyed" is detected.** A mirror with a view carries `EntityViewLinkCleanup`, so
`DestroyEntity` on it strips every other component and keeps the shell until
`EntityViewDespawnSystem` (presentation) removes the cleanup. The drain runs in initialization, so it
must not ask `Exists`; it asks `Exists && HasComponent<NetworkEntity>` — the component it added
itself. A shell is treated as destroyed: a spawn for the id proceeds, a state for it is dropped and
reported.

## Ordering

- Events follow **command order**: the FIFO the view enqueues into. Spawn precedes its first state,
  despawn follows its last, and two ids interleave exactly as the caller called them.
- Handlers run in **subscription order**, then the forwarding publisher (MessagePipe, when wired).
- `Teardown` is the one exception: it iterates the drain's map, whose order is not spawn order. A
  consumer that needs ordered teardown ticks `WorldViewBinder.Reset` through a frame first.

## Exactly once

The drain's id → entity map is the single source of truth. A spawn is published only when an entity
is created and mapped; a despawn only when a mapped id is removed. Consequences, each covered by a
test:

| Scenario | Events |
|---|---|
| Keyframe: `Spawn a, State a, Spawn b, State b` | `S a, S b` |
| Delta: `State a` | nothing |
| Repeated keyframe from a caller bypassing the binder: `Spawn a` for a present id | nothing (view refuses; drain would refuse too) |
| AOI exit then re-entry: `Despawn a … Spawn a` | `D a (Despawned), S a` — with a **different** `Entity` |
| Session reset (`WorldViewBinder.Reset`): `Despawn` per live id | one `D (Despawned)` per id, in the binder's order |
| Reset + new keyframe in one frame | `D a, D me, S a, S me` |
| Consumer destroys the mirror, wire keeps sending state | one `D a (ExternalDestruction)` on the next `State`; the eventual wire `Despawn` is **silent** |
| Consumer destroys the mirror, a second view spawns the id | `D a (ExternalDestruction), S a` — first life closed before the second opens |
| `Uninstall(destroyMirrors: true)` with a, b present and `Despawn a` still queued | `D a (Teardown), D b (Teardown)`; the queued despawn is never applied (the singleton is gone) |
| `Uninstall(destroyMirrors: true)` after a drained reset | nothing |
| `Uninstall(destroyMirrors: true)` when a was destroyed externally and never noticed | `D a (ExternalDestruction), D b (Teardown)` |
| `Uninstall(world)` — default | nothing; mirrors and map are left (0.27.1 behaviour) |

## Subscription semantics

- **Not retroactive.** Events are ephemeral. A late subscriber hears nothing about entities already
  present; query `NetworkEntity` for the current set.
- **Re-entrancy.** Subscribing or disposing during dispatch takes effect from the next event.
- **Error isolation.** A throwing handler is logged with `Debug.LogException`; the remaining handlers,
  the forwarding publisher and the drain continue. The entity is still created/destroyed. Exceptions
  are not rethrown.
- **Disposal.** `Subscribe` returns an `IDisposable`; disposing twice is a no-op. `Clear()` drops all
  handlers without publishing.
- **Observers gate allocation.** Each event carries two managed strings. The drain builds an event
  only while `NetworkEntityLifecycle.HasObservers` is true, so a session nobody subscribed to pays
  nothing and its `SpawnedCount`/`DespawnedCount` read zero (they count *deliveries*).
- **Threading.** Publish, subscribe and dispose all happen on the drain's thread. The hub is not
  synchronised; the command queue is the only cross-thread structure in the adapter and remains so.

## Teardown

Ending a session has two routes, and they compose:

1. **Binder reset, then a frame.** `WorldViewBinder.Reset()` enqueues a `Despawn` per live id; the
   next `NetcodeSystemGroup` update applies them → `Despawned(Despawned)` in order.
2. **`DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true)`.** Whatever is still mapped gets
   `Despawned(Teardown)` (or `ExternalDestruction` if already gone), the mirror entities are destroyed,
   the map is emptied, then the singletons are removed.

Do both and the second is a no-op. Do neither — the default `Uninstall(world)` — and no events fire,
mirrors stay, and the consumer disposes the world (what `DotsWorldBridge` in the client does today).
`NetworkEntityLifecycle` itself is owned by the view and needs no disposal; call `Clear()` if handlers
must be dropped before the view is.

## Wiring

**Core (no DI, no MessagePipe):**

```csharp
var view = new DotsEntityView(catalog, resolver, SnapshotSpaceMapping.XZPlane);
DotsNetcodeBootstrap.Install(world, view);

var spawned   = view.Lifecycle.Subscribe((NetworkEntitySpawned e)   => minimap.Add(e.EntityId, e.Entity));
var despawned = view.Lifecycle.Subscribe((NetworkEntityDespawned e) => minimap.Remove(e.EntityId));
// ... at session end:
spawned.Dispose(); despawned.Dispose();
DotsNetcodeBootstrap.Uninstall(world, destroyMirrors: true);
```

**VContainer (`Cuvara.DOTS.DI`, requires `CUVARA_NETCODE`):**

```csharp
// with MessagePipe: after RegisterMessagePipe() and
//   RegisterMessageBroker<NetworkEntitySpawned>(options); RegisterMessageBroker<NetworkEntityDespawned>(options);
builder.RegisterDotsNetworkLifecycle();
// ...
var view = new DotsEntityView(catalog, resolver, mapping, writeHealth: false,
                              lifecycle: container.Resolve<NetworkEntityLifecycle>());
```

With MessagePipe present, consumers may subscribe through `ISubscriber<NetworkEntitySpawned>`
(MessagePipe) or `IDotsSubscriber<NetworkEntitySpawned>` (the package's adapter); both receive every
event after the hub's direct handlers. Without MessagePipe, `IDotsSubscriber<>` resolves to the hub
itself. **The core never requires MessagePipe**: `Cuvara.DOTS.Netcode` references neither it nor
VContainer.

## Not in scope

- A "died" reason — the wire does not carry one.
- Replay/backlog for late subscribers — query the world.
- Cross-thread subscription — the drain is single-threaded by design.
- Events for the visual lifecycle — those are `ViewSpawned`/`ViewDespawned` and unchanged.
