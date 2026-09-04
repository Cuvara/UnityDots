# Stress Benchmark

Two benchmark modes measuring cuvara.dots simulation + Unity.Physics at scale:

| Mode | What | View layer |
|------|------|------------|
| **Pure DOTS** | Raw ECS throughput | None — no GameObjects, no rendering |
| **Hybrid** | Simulation + view sync | Pooled primitives per entity (capped) |

## Tiers

Default ramp: 100 → 1K → 10K → 1M → 10M → 100M entities.

Each tier spawns 70% simulation-only + 30% Unity.Physics bodies (configurable).
Tiers that would exceed 60% system RAM are auto-skipped.

## Usage

Launch any player with command-line flags:

```bash
# Pure DOTS — raw simulation throughput
./IndieRPGMMOAdventure.exe -stress-pure

# Hybrid — simulation + GameObjects
./IndieRPGMMOAdventure.exe -stress-hybrid

# Quick run (100 → 1M only)
./IndieRPGMMOAdventure.exe -stress-pure -stress-quick

# No physics
./IndieRPGMMOAdventure.exe -stress-pure -stress-no-physics
```

## Output

Results logged as `[STRESS-BENCH]` lines:

```
[STRESS-BENCH] tier=10K entities=10000 sim=7000 phys=3000 frames=16000 wall=15.00s fps=1066.7 mean=0.937ms median=0.880ms p95=1.300ms p99=1.750ms max=5.000ms min=0.650ms
```

## Architecture

`StressBenchmarkBase` handles the shared logic:
- Tier ramp and timing
- Batch entity creation (64K per batch to avoid large single allocations)
- Physics setup (SphereCollider + dynamic mass)
- Memory guard (skips tiers that would OOM)
- Measurement and reporting

Subclasses override `OnTierSpawned()` to add view-layer work (or not).
