# Networked Prediction

Drives `DotsEntityView` and the prediction driver **end to end against a running backend**. This is
the first thing that exercises either against a real server: their unit tests assert wiring with
hand-built snapshots, which cannot prove that a server's actual entity types resolve, that its
coordinates arrive intact, or that exactly one thing writes the local transform under real traffic.

Distinct from `com.cuvara.netcode`'s own DOTS sample, which uses its own `DOTSEntityView` and its own
combat systems. Nothing here touches that scene.

## Running it

1. Start the backend — gateway on `:8000`, C# game server on `:9000`, map `map_01`.
   Source `rpg-mmo-server/backend/deploy/.env` rather than inventing values: the game server refuses
   to start without `JOIN_TOKEN_SECRET`, deliberately.
2. Empty scene, one GameObject, add `NetworkedPredictionSample`.
3. Set **Join Token Secret** to the server's `JOIN_TOKEN_SECRET`. Leave the rest at defaults for a
   local backend.
4. Press play. Move with WASD / arrows.

Run a second instance with a different **User Id** to see a remote player. The two clients must use
different ids: the id *is* the entity id, and it is what `isLocal` is decided by.

## What the overlay is for

Four things this sample exists to make observable, none of which a unit test can assert against a
real server:

| Overlay line | What it proves |
|---|---|
| `mirror entities` / `pooled views` | the adapter spawned from **real** snapshots, and `TypeArchetypeResolver` resolved the server's actual entity types |
| `server (x, y)` vs `anchor world` | `ReconciliationAnchor.ServerPosition` carries what the server sent, and the real `SnapshotSpaceMapping` placed it |
| `predicted: N` and `writer:` | exactly one entity is predicted, and exactly one thing writes `LocalTransform` this frame |
| `pending` / `replayed` / `corrections` | the predictor is actually reconciling rather than idling |

`writer:` is the one to watch. It must read `predictor` for the local entity with prediction on and
`adapter` with it off — and it must never be ambiguous, because both writing is the failure the
`PredictedTransform` marker exists to prevent and neither writing is a frozen avatar.

## The A/B worth doing

Toggle **Prediction Enabled** and watch `writer:` change. Off is not a broken configuration — it is
the adapter driving alone, which is what every build did before the driver existed. If the avatar
freezes with prediction off, the marker was left claimed; if it stutters with prediction on, both are
writing. Either is visible here in one line.

## Not proven by this sample

It shows prediction *running*. It does not measure **keypress-to-visible**, which is the number the
whole prediction effort is aimed at, and which needs a capture rig rather than an overlay.

## Notes

- No prefabs, no Addressables, no DI. `PrimitiveViewProvider` pools Unity primitives so the sample
  drops into an empty scene — and it pools for real, so the recycle path the view layer relies on is
  exercised rather than hidden behind Instantiate/Destroy.
- The catalog is built in code. A real project authors `ViewConfig` assets and lists them in a
  `ViewArchetypeLibrary`; this is the same data, typed.
- Tick rate and map bounds come from `GameConstants`, not from literals here. A literal copy
  compiles, passes, and then disagrees with the server the moment the shared package moves.
- The per-archetype `lift` is the art's half-height, authored as a `ViewConfig` position offset and
  deliberately **not** folded into the space mapping: the entity stays on the plane the server
  simulates on, and only the visual is raised.
