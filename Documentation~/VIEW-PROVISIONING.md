# View Provisioning

The hybrid view layer connects ECS entities to pooled GameObjects. This document
covers the provisioning lifecycle, configuration, and the chunk-aware pool.

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
  → IViewAssetProvider.Release(instance) → back to pool
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
// Warm assets for a chunk
await provisioner.PrewarmChunkAsync("chunk-12-4", new[] { "goblin", "torch" }, countPerKey: 8);

// Release when chunk unloads — cascade-despawns standing views first
var result = provisioner.ReleaseChunk("chunk-12-4");
// result.KeysReleased, result.ViewsDespawned
```

**Shared keys survive**: if two chunks both use "goblin", releasing one chunk does
not release the key while the other still holds it.

**Cascade release**: when a chunk is released, any entity views standing on its
expiring keys are despawned before the assets are returned. A `ChunkCascadeReleased`
message reports how many views were affected.

## IViewAssetProvider

The interface the spawn system calls. Two implementations ship:

| Implementation | Source | When to use |
|----------------|--------|-------------|
| `PrimitiveViewAssetProvider` | `Runtime/` | Dev/test — creates Unity primitives |
| GameFoundation provider | `Runtime.GameFoundation/` | Production — uses `IAssetsManager` + `IObjectPoolManager` |

Implement your own if you have a different pool or asset system.

## Sorting keys

`ViewSortingKey` controls draw order within a chunk. Entities with a lower sorting
key are spawned first and rendered underneath. The key is stable across pool
recycles — a returned-and-reacquired instance keeps its draw position.
