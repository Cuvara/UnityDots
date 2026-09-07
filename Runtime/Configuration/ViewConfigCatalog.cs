using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Builds the <see cref="ViewConfigTable"/> blob from authoring assets, installs it into a world,
    /// and owns its lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime half of "authoring, not baking": construction happens when a session starts,
    /// against whatever library the consumer hands over, rather than at conversion time against a
    /// subscene that does not exist.
    /// </para>
    /// <para>
    /// Managed and DI-agnostic — a plain constructor and a <see cref="World"/> argument, matching
    /// <c>DotsViewBootstrap</c>. Disposing it releases the blob; the entity holding
    /// <see cref="ViewConfigTableReference"/> is destroyed with it, so nothing is left pointing at
    /// freed memory.
    /// </para>
    /// <para>
    /// <b>Versioned, not immutable</b> (see <c>Documentation~/CONFIG-VALIDATION.md</c>). Every
    /// <see cref="Build"/> increments <see cref="Version"/>, stamps it into the blob, and re-publishes
    /// the new table into every world the catalog is installed in — so no singleton is ever left
    /// pointing at the freed previous blob. A <see cref="ViewConfigRef"/> carries the version it was
    /// issued at, and the spawn path refuses one from any other version. An old index therefore
    /// cannot resolve to a different view: it resolves to nothing and the entity falls back to its
    /// own request key, with a warning naming the rebuild.
    /// </para>
    /// </remarks>
    public sealed class ViewConfigCatalog : IDisposable
    {
        private readonly Dictionary<string, int> _indexByName = new Dictionary<string, int>();
        private readonly List<World> _installedWorlds = new List<World>();
        private BlobAssetReference<ViewConfigTable> _table;
        private ViewConfigRecord[] _records = Array.Empty<ViewConfigRecord>();

        /// <summary>Number of archetypes in the catalog.</summary>
        public int Count => _records.Length;

        /// <summary>The built blob. Not valid before <see cref="Build"/>.</summary>
        public BlobAssetReference<ViewConfigTable> Table => _table;

        /// <summary>
        /// Incremented by every <see cref="Build"/>; 0 before the first. The value a
        /// <see cref="ViewConfigRef"/> must carry to resolve against the current table.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>Worlds this catalog is currently published into. Disposed worlds are pruned lazily.</summary>
        public int InstalledWorldCount
        {
            get
            {
                PruneDisposedWorlds();
                return _installedWorlds.Count;
            }
        }

        /// <summary>
        /// Builds the blob from a library. A later call replaces the previous blob and disposes it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only safe to call between frames, and never while a tick is in flight.</b> Rebuilding
        /// frees the previous blob immediately. Entities hold a <see cref="ViewConfigRef"/> index into
        /// that blob, and a system reading the table mid-frame — or a Bursted job holding it — would
        /// then be reading freed memory. That is not a clean exception: a Bursted read of a disposed
        /// blob is undefined behaviour, so it can look like corrupt data or nothing at all rather than
        /// a crash pointing at this line. The caller owns that sequencing; the package cannot detect it.
        /// </para>
        /// <para>
        /// <b>A rebuild also invalidates every ref handed out before it</b> — deliberately and
        /// detectably. <see cref="Version"/> increments, the new table carries it, and a
        /// <see cref="ViewConfigRef"/> stamped with the old version is refused by the spawn path,
        /// which falls back to the request's own key and warns. Re-issue refs with
        /// <see cref="CreateRef(int)"/> after rebuilding. Worlds the catalog is installed in are
        /// re-published automatically, so the singleton never points at the freed blob.
        /// </para>
        /// <para>
        /// Entries with no config, no name, or a duplicate name are skipped with a warning rather
        /// than throwing: one broken row in an asset should not stop a session from starting, and the
        /// warning names the row so it can be found. A duplicate name would otherwise make which
        /// config wins depend on list order.
        /// </para>
        /// </remarks>
        public void Build(ViewArchetypeLibrary library)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));

            _indexByName.Clear();
            var records = new List<ViewConfigRecord>(library.Entries.Count);

            foreach (var entry in library.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.Config == null)
                {
                    Debug.LogWarning($"[Cuvara.DOTS] '{library.name}' has an entry with no name or no config; skipped.");
                    continue;
                }

                if (_indexByName.ContainsKey(entry.Name))
                {
                    Debug.LogWarning($"[Cuvara.DOTS] '{library.name}' defines archetype '{entry.Name}' twice; the later one is skipped.");
                    continue;
                }

                _indexByName.Add(entry.Name, records.Count);
                records.Add(entry.Config.ToRecord(ViewArchetypeLibrary.HashName(entry.Name)));
            }

            _records = records.ToArray();
            Version++;
            Rebuild();
            Republish();
        }

        /// <summary>
        /// Validates the library first and builds only when the report has no errors. On failure the
        /// catalog is left exactly as it was — a previously built table stays installed and valid.
        /// </summary>
        /// <param name="prefabExists">See <see cref="ViewConfigValidator.ValidateLibrary(ViewArchetypeLibrary, Func{string, bool})"/>.</param>
        /// <returns>True when the catalog was (re)built.</returns>
        public bool TryBuild(ViewArchetypeLibrary library, out ViewConfigValidationReport report, Func<string, bool> prefabExists = null)
        {
            report = ViewConfigValidator.ValidateLibrary(library, prefabExists);
            if (report.HasErrors) return false;

            Build(library);
            return true;
        }

        /// <summary>
        /// <see cref="TryBuild"/> that throws a <see cref="ViewConfigValidationException"/> listing
        /// every issue when the library is invalid. Warnings are logged and do not throw.
        /// </summary>
        public ViewConfigValidationReport BuildOrThrow(ViewArchetypeLibrary library, Func<string, bool> prefabExists = null)
        {
            if (!TryBuild(library, out var report, prefabExists)) throw new ViewConfigValidationException(report);
            if (report.WarningCount > 0) report.Log();
            return report;
        }

        /// <summary>
        /// A <see cref="ViewConfigRef"/> for the record at <paramref name="index"/>, stamped with the
        /// current <see cref="Version"/>. The only supported way to make one.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The index is not in the built table.</exception>
        /// <exception cref="InvalidOperationException">The catalog has not been built.</exception>
        public ViewConfigRef CreateRef(int index)
        {
            if (Version == 0) throw new InvalidOperationException("[Cuvara.DOTS] Build must be called before CreateRef.");
            if (index < 0 || index >= _records.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    $"[Cuvara.DOTS] The catalog has {_records.Length} record(s); index {index} names none of them.");
            }

            return new ViewConfigRef { Index = index, Version = Version };
        }

        /// <summary>A stamped ref for a named archetype. False when the name is not in the catalog.</summary>
        public bool TryCreateRef(string archetypeName, out ViewConfigRef configRef)
        {
            var index = IndexOf(archetypeName);
            if (index < 0 || Version == 0)
            {
                configRef = default;
                return false;
            }

            configRef = CreateRef(index);
            return true;
        }

        /// <summary>Every distinct view key in the catalog.</summary>
        /// <remarks>
        /// Diff two of these across a rebuild to learn which prefabs the provider may drop and which
        /// it must keep — the catalog side of the prefab-replacement contract in
        /// <c>Documentation~/CONFIG-VALIDATION.md</c>.
        /// </remarks>
        public HashSet<string> ViewKeys()
        {
            var result = new HashSet<string>();
            foreach (var record in _records)
            {
                var key = record.ViewKey.ToString();
                if (!string.IsNullOrEmpty(key)) result.Add(key);
            }

            return result;
        }

        /// <summary>Index of a named archetype, or -1. Resolve once and carry the index.</summary>
        public int IndexOf(string archetypeName)
        {
            return archetypeName != null && _indexByName.TryGetValue(archetypeName, out var index) ? index : -1;
        }

        /// <summary>The record at an index; throws for an out-of-range index rather than returning junk.</summary>
        public ViewConfigRecord this[int index] => _records[index];

        /// <summary>
        /// Every distinct view key in the catalog, paired with the largest pool size any archetype
        /// asks for.
        /// </summary>
        /// <remarks>
        /// This is what a consumer feeds to <c>ChunkViewProvisioner.PrewarmChunkAsync</c>. Two
        /// archetypes sharing a prefab must not each add a reference for the same key here — the
        /// provisioner already de-duplicates on intake, and the larger pool size is the one that
        /// matters, matching its grow-only warm count.
        /// </remarks>
        public IReadOnlyDictionary<string, int> PoolSizesByKey()
        {
            var result = new Dictionary<string, int>();
            foreach (var record in _records)
            {
                var key = record.ViewKey.ToString();
                if (string.IsNullOrEmpty(key)) continue;

                result[key] = result.TryGetValue(key, out var existing) && existing > record.PoolSize
                    ? existing
                    : record.PoolSize;
            }

            return result;
        }

        /// <summary>
        /// Publishes the table into <paramref name="world"/> as a singleton. Idempotent — a second
        /// call updates the existing singleton rather than creating a second one, which would make
        /// every <c>GetSingleton</c> throw.
        /// </summary>
        public Entity Install(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (!_table.IsCreated) throw new InvalidOperationException("Build must be called before Install.");

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ViewConfigTableReference>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentData(entity, new ViewConfigTableReference { Table = _table });
#if UNITY_EDITOR
                entityManager.SetName(entity, "ViewConfigTable");
#endif
            }
            else
            {
                entity = query.GetSingletonEntity();
                entityManager.SetComponentData(entity, new ViewConfigTableReference { Table = _table });
            }

            if (!_installedWorlds.Contains(world)) _installedWorlds.Add(world);
            return entity;
        }

        /// <summary>
        /// Removes the table singleton from <paramref name="world"/> and forgets the world. Safe when
        /// never installed there, and safe twice. Does not dispose the blob — other worlds may still
        /// be reading it.
        /// </summary>
        public void Uninstall(World world)
        {
            _installedWorlds.Remove(world);
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ViewConfigTableReference>());
            if (!query.IsEmpty) entityManager.DestroyEntity(query);
        }

        /// <summary>Whether this catalog's table is published in <paramref name="world"/>.</summary>
        public bool IsInstalled(World world)
        {
            PruneDisposedWorlds();
            return world != null && world.IsCreated && _installedWorlds.Contains(world);
        }

        /// <summary>
        /// Frees the blob and removes the singleton from every world it was published into, so no
        /// world is left holding a reference to freed memory. Idempotent.
        /// </summary>
        /// <remarks>
        /// Only safe between frames, for the reason <see cref="Build"/> documents: a system reading
        /// the table while it is freed is undefined behaviour. The client wiring disposes the catalog
        /// after uninstalling the systems that read it, and that order is the contract.
        /// </remarks>
        public void Dispose()
        {
            for (var i = _installedWorlds.Count - 1; i >= 0; i--)
            {
                var world = _installedWorlds[i];
                if (world == null || !world.IsCreated) continue;

                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ViewConfigTableReference>());
                if (!query.IsEmpty) world.EntityManager.DestroyEntity(query);
            }

            _installedWorlds.Clear();

            if (_table.IsCreated) _table.Dispose();
            _table = default;
            _records = Array.Empty<ViewConfigRecord>();
            _indexByName.Clear();
        }

        /// <summary>
        /// Points every installed world's singleton at the freshly built blob. Called from
        /// <see cref="Build"/>, after the old blob is gone — which is why it has to happen inside the
        /// same call rather than being left to the consumer.
        /// </summary>
        private void Republish()
        {
            PruneDisposedWorlds();
            for (var i = _installedWorlds.Count - 1; i >= 0; i--)
            {
                var world = _installedWorlds[i];
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ViewConfigTableReference>());
                if (query.IsEmpty)
                {
                    // The consumer destroyed the singleton themselves; treat as uninstalled.
                    _installedWorlds.RemoveAt(i);
                    continue;
                }

                world.EntityManager.SetComponentData(query.GetSingletonEntity(), new ViewConfigTableReference { Table = _table });
            }
        }

        /// <summary>
        /// Drops worlds that were disposed without <see cref="Uninstall"/>. A disposed
        /// <see cref="World"/> is a managed object with <c>IsCreated == false</c>; holding it is
        /// harmless but pointless, and pruning keeps <see cref="InstalledWorldCount"/> honest.
        /// </summary>
        private void PruneDisposedWorlds()
        {
            for (var i = _installedWorlds.Count - 1; i >= 0; i--)
            {
                if (_installedWorlds[i] == null || !_installedWorlds[i].IsCreated) _installedWorlds.RemoveAt(i);
            }
        }

        /// <remarks>
        /// The previous blob is freed here. <c>Dispose</c> nulls the pointer in <see cref="_table"/>
        /// itself, but any copy of that reference taken by a caller keeps its own now-dangling
        /// pointer — which is why <see cref="Build"/> documents when it is safe to call rather than
        /// trying to detect misuse.
        /// </remarks>
        private void Rebuild()
        {
            if (_table.IsCreated) _table.Dispose();

            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<ViewConfigTable>();
            root.Version = Version;
            var array = builder.Allocate(ref root.Records, _records.Length);
            for (var i = 0; i < _records.Length; i++) array[i] = _records[i];

            _table = builder.CreateBlobAssetReference<ViewConfigTable>(Allocator.Persistent);
        }
    }
}
