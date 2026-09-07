# Camera follow — behaviour and lifecycle

Module: `CameraFollowBootstrap` (install/uninstall, see `MODULE-LIFECYCLE.md`), system
`CameraFollowSystem`, config `CameraFollowConfig`, math `CameraFollowMath` (pure, tested without a
camera).

## Where it runs

`ViewSystemGroup`, `[UpdateAfter(ViewTransformSyncGroup)]` — after `ViewInterpolationGroup`
(remote entities are at their interpolated pose), after `ViewLifecycleGroup` (this frame's views
exist) and after the sync (GameObjects hold what was rendered). The local player's predicted
transform is written in `PredictionSystemGroup` (initialization) and so is final long before.
`CameraFollowBootstrapTests.Camera_RunsAfterInterpolationLifecycleAndSync` asserts the actual order.

## Which camera

`CameraFollowConfig.Camera`. Null → `Camera.main`, resolved every frame. A supplied camera that is
destroyed compares equal to null (Unity fake-null) and the system idles until another is supplied.
Split-screen: one world per view is the supported shape; one config per world names its camera.

## Target policy

| Situation | Behaviour |
|---|---|
| No entity tagged `CameraFollowTarget` | system does not update; camera holds; values stay finite |
| Exactly one | followed |
| Several, `MultipleTargets = HoldAndReport` (default) | camera holds; one `LogError` naming the count and the fix; resumes when one remains |
| Several, `MultipleTargets = FollowLowestIndex` | follows the lowest entity index; the entity followed last frame keeps priority while still tagged, so a new tag does not yank the camera |
| Target entity changes, `TargetSwitch = Snap` (default) | snap to the new target, spring reset |
| Target entity changes, `TargetSwitch = Smooth` | damp from the current pose toward the new target |
| First frame after install or `ResetSmoothing` | always snaps — a camera at the scene origin does not glide to the player on load |
| Target's `LocalToWorld` non-finite | hold this frame |

## Teleport and reconnect

`TeleportDistance > 0`: when the desired camera position is further than this from the current
one, snap and reset the spring instead of gliding across the map. `0` disables. A server teleport
that moves the player by more than this is therefore explicit and instant.

`CameraFollowBootstrap.ResetSmoothing(world)` (or `system.ResetSmoothing()`) drops the spring so
the next frame snaps regardless of distance — call it from the reconnect path, where the player
may reappear anywhere, and from scene load.

## Damping

`CameraFollowMath.SmoothDamp` is a port of `Vector3.SmoothDamp` (critically damped spring,
Unity's polynomial exponential, Unity's overshoot guard) to `float3`.
`CameraFollowMathTests.SmoothDamp_MatchesUnitysVector3SmoothDamp_StepForStep` holds it to 1e-4
against the real thing over 120 frames, so the package is not maintaining custom damping math.

What differs from Unity, deliberately: **`MaxSpeed` is a hard per-frame limit.** Unity's
`maxSpeed` clamps the distance the spring sees to `maxSpeed * smoothTime`, which lets the camera
exceed `maxSpeed * dt` in a frame while the spring is loaded. `CameraFollowMath.Step` clamps the
displacement to `MaxSpeed * dt` after the spring step and scales the velocity by the same factor.
The tooltip's meaning — "never faster than this, in units per second" — is exactly what is
enforced, at any distance and any frame rate.

`SmoothTime = 0` snaps. `dt <= 0` (paused editor, zero-length frame) leaves position, rotation and
velocity untouched — a paused frame is never a jump. Frame-rate variation (30/60/144 Hz) lands
within 0.15 units after one simulated second; random 0–250 ms deltas never exceed `MaxSpeed*dt`
and never go non-finite.

## Validation (at install)

`Offset`/`LookAtOffset` finite, `SmoothTime >= 0`, `MaxSpeed > 0`, `TeleportDistance >= 0`.
`ArgumentException` naming the field; nothing is created on failure. `Camera` is not validated —
null is legal.
