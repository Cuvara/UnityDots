# Animation and Events

No server, no network. A scripted `DotsEntityView` is driven with the same
`IEntityView` / `IEntityPoseView` calls the binder makes against a live connection, and
everything downstream is the production path: the drain, `EntityPose`, the
`NetworkGameEvent` buffer, and `EntityPoseViewSystem`.

Two capsules. The attacker swings on a cadence; the victim loses health and respawns.

## Stop sending action_seq

The attacker keeps attacking. The swing counter **freezes**, and the readout still says
`action=Attacking` on every frame.

That is the whole problem the counter exists for. `action` is level-triggered: two swings in a
row are the same value, so there is no edge in it to detect and no amount of client-side
cleverness recovers one. Only the server knows an action was re-entered.

## Stop sending events

The victim's health keeps falling and the damage log **stops**.

HP is state and arrives either way. A hit is an occurrence, and it is not derivable from two
HP values — a heal and a hit in the same tick net out, and an entity that leaves the AOI stops
reporting entirely.

## The consumer side is `SwingFlash`, and it is nine lines

```csharp
public void OnAction(SimAction action, bool retriggered)
{
    if (action != SimAction.Attacking) return;
    Swings++;
    _flashUntil = Time.time + 0.15f;
}
```

It does not compare counters, does not remember what it last drew, and does not know that the
counter wraps at 2³² and resets on respawn. All three are the package's job:

- **The comparison is inequality, not greater-than.** A `>` test stops retriggering for four
  billion actions after one wrap, and nothing reports an error.
- **The memory is per ENTITY, not per view.** Views are pooled: a recycled one would compare a
  new entity's first swing against the previous entity's counter and decide it was not new.

Hand a view script the raw number and it gets one of those wrong. That is why
`IEntityAnimationReceiver` takes a `bool` and not a `uint`.

## Running it

Open `Scenes/AnimationAndEvents.unity` and press Play. Nothing to configure, no backend.

Inspector knobs: `secondsPerSwing`, `damagePerHit`.

## What it does not prove

The event participants here always resolve, because both entities are always spawned. The
cases that matter — a participant outside the AOI, one spawned in the same drain, a queue
overflowing — are covered in `Tests/Editor.Netcode/EntityPoseAndEventTests.cs`, because none
of them is visible on screen when it is working.
