# Production Hardening Sample

Demonstrates three production-hardening features added to com.cuvara.dots:

## What it shows

| Step | Feature | What happens |
|------|---------|-------------|
| 0 | Chunk metrics | Warm a chunk, spawn 4 entities, show `OnChunkStateChanged` events |
| 1 | Diagnostics | Read `TotalViews`, `TotalKeys`, `LiveCountsByKey` from the registry |
| 2 | Robust despawn | Destroy 2 view GameObjects externally via `Object.Destroy` |
| 3 | Sweep verify | `SweepDestroyed` detects and cleans up stale entries automatically |
| 4 | Overlay anchors | Spawn 3 entities with `ViewOverlayAnchor` (2m above head) |
| 5 | Overlay buffer | Read `ViewOverlayBuffer` — world positions for health bars |
| 6 | Cleanup | Release chunk, show cascade despawn and final state |

## Usage

1. Import the sample from Package Manager
2. Open `Scenes/ProductionHardening.unity`
3. Press Play
4. Watch the Console — each step is logged with `═══` headers

## Dependencies

Only `Cuvara.DOTS.Runtime` and the four pinned Unity DOTS packages. No VContainer,
no GameFoundation, no netcode.
