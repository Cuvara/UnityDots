# Configuration validation and catalog versioning

## Why runtime validation

`ViewConfig` and `ViewArchetypeLibrary` are ScriptableObjects, but this package's consumers build
catalogs at runtime from server vocabulary — today entirely in code via `ViewConfig.Configure`.
An inspector `OnValidate` never runs on that path. So the check lives where the catalog is built
and runs before the first spawn: a session refuses to start with a wrong config instead of
rendering nothing ten minutes in.

## The API

```csharp
// Report, do not throw:
ViewConfigValidationReport report = ViewConfigValidator.ValidateLibrary(library, prefabExists: pool.Has);
if (!report.IsValid) report.Log();          // one console line per issue, at its own severity

// Or gate the catalog on it:
var catalog = new ViewConfigCatalog();
if (!catalog.TryBuild(library, out report, prefabExists))   // refuses; previous table untouched
    report.ThrowIfInvalid();
catalog.BuildOrThrow(library);               // same, throws ViewConfigValidationException listing everything

// Server kind -> archetype name mappings, before the connection opens:
ViewConfigValidator.ValidateMappings(library, new[] { KeyValuePair.Create("mob", "goblin") });

// Archetype presets:
ViewConfigValidator.ValidatePreset(preset, library);
```

Every issue carries a stable `Code` (constants on `ViewConfigIssue`), the `Subject` (archetype
name, asset name or entity type) and a message that says what to do. One pass reports every
issue, not the first.

| Code | Severity | Fires when |
|---|---|---|
| `EmptyName` | Error | library entry has no archetype name |
| `DuplicateName` | Error | two entries share a name |
| `MissingConfig` | Error | entry's `ViewConfig` is null (or the library/preset itself is null) |
| `EmptyViewKey` | Error | config's key is empty |
| `ViewKeyTooLong` | Error | key exceeds 61 UTF-8 **bytes** (`FixedString64Bytes`) — would be truncated and never match the pool |
| `MissingPrefab` | Error | `prefabExists(key)` returned false (only when a lookup is supplied — the core cannot ask an `IViewAssetProvider`) |
| `NegativePoolSize` | Error | pool size below 0 |
| `InvalidScale` | Error | scale NaN, infinite or ≤ 0 |
| `InvalidOffset` | Error | any offset component NaN or infinite |
| `UnknownArchetype` | Error | a mapping names an archetype the library lacks |
| `EmptyEntityType` / `DuplicateEntityType` | Error | mapping or preset has no / a repeated entity type |
| `UnknownViewKey` | Error | preset's view key is used by no config in the library |
| `InvalidHealth` | Error | preset hp not in `0 ≤ hp ≤ max`, or `max ≤ 0` |
| `InvalidTimeToLive` | Error | preset TTL negative or non-finite |
| `IneffectiveTimeToLive` | Warning | preset enables TTL with value 0 — `ArchetypeFactory` adds no component for 0 |

`ViewConfigCatalog.Build(library)` (no validation) keeps its lenient pre-0.28 behaviour: broken
rows are skipped with a warning. Use `TryBuild`/`BuildOrThrow` at session start; `Build` is for
code that has already validated.

## Catalog versioning — the immutability decision

The catalog is **versioned, not immutable**. Each `Build` increments `catalog.Version`, writes it
into the blob (`ViewConfigTable.Version`), and re-publishes the new table into every world the
catalog is installed in, so no `ViewConfigTableReference` singleton is ever left pointing at the
freed previous blob.

A `ViewConfigRef` carries the version it was issued at. The spawn path refuses a ref whose version
differs from the installed table's and falls back to the entity's own `EntityViewRequest.ViewKey`,
with a warning that names the rebuild. **An old index therefore cannot resolve to a different view.**
It resolves to nothing, loudly.

```csharp
// Issue refs from the catalog — never with `new`:
entityManager.AddComponentData(entity, catalog.CreateRef(catalog.IndexOf("goblin")));
catalog.TryCreateRef("goblin", out var configRef);
```

A `new ViewConfigRef { Index = i }` has `Version == 0`. No built table has version 0, so an
unstamped ref is always refused. That is deliberate; it is the silent-swap bug waiting to happen.

Rebuild rules, unchanged: only between frames, never while a system that reads the table is
mid-update or a Bursted job holds the blob. After a rebuild, re-issue refs for entities that must
keep their configured view; entities left with stale refs fall back to their request key. The
netcode adapter (`DotsEntityView`) stamps the version into every spawn command and drops its
name→index cache when the version changes, so a rebuild between enqueue and drain is refused rather
than resolved to the wrong record.

`catalog.Install(world)` may be called for several worlds; `catalog.Uninstall(world)` removes one
world's singleton; `catalog.Dispose()` removes every installed singleton and frees the blob.
Disposed worlds are pruned, not held.

## Prefab replacement — the contract

The catalog side is complete; the provider side is a contract for `PooledViewAssetProvider` /
`ChunkViewProvisioner` (owned by a separate change) and for host adapters.

Replacing the prefab behind an **existing key** `K`:

1. Despawn active instances first: `IViewCascadeSink.CascadeDespawn(new[] { K })` (the
   `EntityViewCascade` recycles them through the ordinary path and strips the links). Entities do
   **not** re-request automatically after a cascade; re-add `EntityViewRequest` where a respawn is
   wanted.
2. Provider: `Release(K)` drops pooled instances and the old prefab; then `PrewarmAsync(K, n)`
   loads the new one. A provider must never hand an instance of the old prefab out of the pool
   after `Release(K)` returned — pooled instances are part of what `Release` discards.
3. The catalog is untouched: the key is the same, `Version` is the same, existing refs stay valid.

Replacing the prefab by **changing the key** (`K` → `K'`) in a `ViewConfig`:

1. `catalog.Build(library)` → `Version` bumps; refs are stale; every world is re-published.
2. Diff `catalog.ViewKeys()` before and after: keys that disappeared may be `Release`d by the
   provider once their live views are cascaded; new keys are warmed via `PoolSizesByKey()`.
3. Re-issue refs with `CreateRef`; entities whose ref was stale fall back to their request key
   until then.

The provider is expected to expose "is this key registered" for `prefabExists` — the pooled
provider's registered-prefab set is the natural source. That method is not on `IViewAssetProvider`
so that the interface stays the thin seam it is; pass a delegate.
