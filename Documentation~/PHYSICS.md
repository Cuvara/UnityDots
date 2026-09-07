# Physics — bodies, colliders, movement, events

Assembly `Cuvara.DOTS.Physics` (`Runtime.Physics`), compiled only when `com.unity.physics ≥ 1.0.0`
is present. The core never references it. Everything here is **client-side**: bodies collide on
this client for feedback and presentation. Nothing in this assembly is, or claims to be, the
server's collision; the server stays authoritative for anything that matters and this package
does not build a second physics engine — every query, step and event is Unity.Physics'.

## Bodies — `PhysicsBodyFactory`

| Kind | Components added | Moved by | Tag |
|---|---|---|---|
| `AddDynamicBody` | `PhysicsCollider`, `PhysicsVelocity`, `PhysicsMass` (finite mass) | Unity.Physics (forces, contacts, `PhysicsVelocity`) | `PhysicsDrivenMovement` |
| `AddKinematicBody` | `PhysicsCollider`, `PhysicsVelocity`, `PhysicsMass` (infinite) | Unity.Physics from `PhysicsVelocity` only; ignores forces | `PhysicsDrivenMovement` |
| `AddStaticBody` | `PhysicsCollider` | nobody | — |

Every body also gets `LocalTransform`/`LocalToWorld` if missing and **`PhysicsWorldIndex`** —
`BuildPhysicsWorld` selects bodies by that shared component; without it a body is invisible to the
simulation with no error. Earlier versions omitted it.

Validation (`PhysicsBodyValidation`, `ArgumentException` naming the value): size finite;
sphere radius > 0; box extents > 0; capsule radius > 0, height > 0, height ≥ 2·radius; cylinder
radius/height > 0; dynamic mass finite and > 0; filter with `BelongsTo != 0` and
`CollidesWith != 0` (a body that collides with nothing is a bug, not a configuration).
`size` meaning: Sphere `(radius,-,-)`, Box full extents, Capsule `(radius, total height,-)`,
Cylinder `(radius, height,-)`.

## Collider ownership — `ColliderLibrary`

A `BlobAssetReference<Collider>` is a raw allocation Unity.Physics will not free for you (only
baked force-unique colliders get its cleanup system). Two ownership modes, never mixed:

- **Library-owned (recommended):** `factory.Add*Body(em, entity, library, shape, size, …)`. One
  blob per distinct (shape, size, filter, material), shared by every body asking for it; leases
  are counted; `library.Release(blob)` frees at zero; `library.Dispose()` frees all. One library
  per session/world, disposed **after** the bodies or the world. Tests: `ColliderLibraryTests`,
  `RepeatedInstallStepUninstall_LeavesNoModuleAndNoLeases` (leases and blob count return to 0).
- **Caller-owned:** `PhysicsBodyFactory.CreateCollider(...)` then `Add*Body(em, entity, blob, …)`.
  The caller disposes the blob after every body using it is gone.

There is no overload that allocates a blob nobody owns.

## One integrator per entity — `PhysicsDrivenMovement`

The package's direct movers (`MoveBounceSystem`, `MoveTowardSystem`) write `LocalTransform`
directly. `PhysicsMovementBridge` copies `MoveData.Velocity` into `PhysicsVelocity` and lets
Unity.Physics integrate. Both on one entity = double movement with nothing logging. The rule is
enforced by a core tag:

- `PhysicsBodyFactory` adds `PhysicsDrivenMovement` to dynamic and kinematic bodies.
- `MoveBounceSystem`/`MoveTowardSystem` query `WithNone<PhysicsDrivenMovement>`.
- `PhysicsMovementBridge` queries `WithAll<PhysicsDrivenMovement>`.
- `PhysicsBodyValidation.AssertSingleIntegrator(em, entity)` throws when `PhysicsVelocity` and
  the tag disagree. Test: `DirectMovers_SkipAPhysicsDrivenBody_SoItIsIntegratedOnce`.

**Prediction.** The netcode prediction driver writes `LocalTransform` of the predicted entity
directly (`PredictedTransform`). A predicted entity must not be given a physics body; the two
assemblies cannot reference each other, so this is a rule, not a compile error. Give the local
player a static or trigger collider (for queries and triggers) or none at all — never
`PhysicsVelocity`.

**Fixed-step timing.** `PhysicsSystemGroup` lives in `FixedStepSimulationSystemGroup`.
`PhysicsMovementBridge` runs in `MovementSystemGroup` (variable step) and writes a velocity; the
next fixed step integrates whatever was written last. Writing twice in one fixed step is not
applied twice. The event buffer (below) is rebuilt per **fixed** step: a render frame with no
physics step sees the previous step's events; a render frame with two steps sees only the last.

## Events — `PhysicsEventsBootstrap`, `PhysicsEventCollectorSystem`, `PhysicsContactTracker`

```csharp
PhysicsEventsBootstrap.Install(world);                       // Session-scoped module
var buffer = PhysicsEventsBootstrap.Buffer(world);           // read after PhysicsSystemGroup
foreach (var hit in buffer.Collisions) { /* hit.Phase, hit.EntityA/B, hit.Normal, hit.Impulse */ }
foreach (var t in buffer.Triggers)     { /* t.Phase, t.EntityA/B */ }
```

Order: the collector is `[UpdateInGroup(PhysicsSystemGroup)] [UpdateAfter(PhysicsSimulationGroup)]`
— the slot Unity's samples read events in; the streams belong to the step that just ran. Install
warns (or throws with `requirePhysicsPipeline: true`) when the world has no
`PhysicsSimulationGroup`.

To raise events a collider needs the matching material: `PhysicsBodyFactory.TriggerMaterial()`
(`RaiseTriggerEvents`) or `CollisionEventMaterial()` (`CollideRaiseCollisionEvents`). Unity.Physics
raises trigger events only for pairs where at least one body is dynamic/kinematic.

### Semantics

| | |
|---|---|
| **Identity** | Full `Entity` — index **and** version. A destroyed entity whose index is reused is a different entity: its old pairs `Exit`, the newcomer's pairs `Enter`. Keying on index alone would report a `Stay` between a corpse and its successor. |
| **Pair ordering** | `EntityA` is the lower index (then lower version); `PhysicsPairKey` normalises whichever order Unity reported. `Normal` points from A toward B (Unity's raw normal points B→A and is flipped). |
| **Aggregation** | Unity raises one event per collider pair per step; compound colliders raise several for one entity pair. They fold into one `EntityCollision`: `ContactCount`, summed `Impulse`, normalised mean `Normal`, mean `Position`. Triggers fold with no data. |
| **Enter / Stay / Exit** | Per step: reported now, not last step → `Enter`; both → `Stay`; last step only → `Exit`. `Exit` is reported exactly once, with zero normal/impulse/count and the last known position. |
| **Destroyed entities** | A destroyed entity stops being reported, so its pairs `Exit` on the next step with `AnyEntityDestroyed = true`. Do not read components of either entity on such an exit without `EntityManager.Exists`. |
| **Missing colliders** | Removing `PhysicsCollider` (or `PhysicsWorldIndex`) behaves like destruction for pairing purposes: the pair exits, unflagged (the entity still exists). |
| **Output order** | Exits, then enters, then stays; each sorted by pair key. Deterministic for a given step's contact set. |
| **Reset** | `collector.Tracker.Clear()` forgets every pair without emitting exits — for a reconnect or world reset where the consumer already dropped its per-pair state. |

### Ownership and teardown

`PhysicsEventsBootstrap.Install` records the module Session-scoped in `DotsModules`;
`Uninstall` (or `DotsModules.UninstallScope(world, Session)`) destroys the buffer singleton and
the collector, whose `OnDestroy` completes its jobs and frees its two native lists. Install twice
is one installation; uninstall twice is safe; two worlds are independent
(`PhysicsEventsBootstrapTests`).

## Spatial queries — `SpatialQuery`

Thin wrappers over `CollisionWorld.CalculateDistance`/`CastRay` writing into caller-provided
lists. Read `CollisionWorld` from `SystemAPI.GetSingleton<PhysicsWorldSingleton>()` after
`PhysicsInitializeGroup` in the same fixed step, or from the previous step outside it.

## Testing

`Tests/Editor.Physics` is gated on `com.unity.physics` exactly like the assembly. The event tests
step the real pipeline in EditMode: every `Unity.Physics*` system type is added to a test world
with `DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups`, gravity is zeroed through a
`PhysicsStep` singleton, and `PhysicsSystemGroup.Update()` is driven with `World.SetTime`. No CI
row currently installs `com.unity.physics`; these tests run in a consumer project that has it.
