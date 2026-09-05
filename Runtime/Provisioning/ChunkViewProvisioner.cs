using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Messaging;
using UnityEngine;

namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// Warms and releases whole sets of view prefabs on behalf of a spatial chunk or region,
    /// reference-counting keys so chunks that share a prefab do not unload it from under each
    /// other.
    /// </summary>
    /// <remarks>
    /// <para><b>Reference-count semantics — the part that is easy to get wrong:</b></para>
    /// <list type="bullet">
    /// <item>A key is counted <b>once per chunk</b>, never once per occurrence. The key list is
    /// de-duplicated on intake, so a chunk that lists <c>goblin</c> three times still contributes
    /// exactly one reference and one release. Counting occurrences would leak: the release path
    /// walks the stored set, and the stored set holds each key once.</item>
    /// <item>The count reaching zero is the <i>only</i> trigger for
    /// <see cref="IViewAssetProvider.Release"/>. Releasing chunk A while chunk B still lists the
    /// same key decrements and nothing else.</item>
    /// <item>Releasing an unknown or already-released chunk is a no-op, not an error. The chunk is
    /// removed from the table before its keys are decremented, so a double release cannot
    /// double-decrement.</item>
    /// <item>Re-warming a chunk that is already warm is a diff, not an add: the new set is
    /// incremented <i>before</i> the old set is decremented, so a key present in both never
    /// transiently hits zero and never gets unloaded and reloaded.</item>
    /// <item>Counts are mutated synchronously, before any await. Two overlapping
    /// <see cref="PrewarmChunkAsync"/> calls therefore cannot interleave into a wrong count, even
    /// though the loads they kick off do overlap.</item>
    /// </list>
    /// <para>
    /// <b>Releasing a chunk whose views are still alive cascades: the views come down first.</b>
    /// Reference counting alone cannot make that case safe, because the counts track chunks while a
    /// live view is held by an entity the provisioner has never heard of. Releasing the asset first
    /// destroyed pooled instances that were on screen while <c>EntityViewRegistry</c> kept their
    /// handles and the entities kept an <c>EntityViewLink</c> that could never resolve or respawn.
    /// The order is now inverted: every view standing on a key this release would drop is put
    /// through the ordinary despawn path via <see cref="IViewCascadeSink"/> — recycled, handle
    /// dropped, link cleared — and only then does the reference count reach zero and
    /// <see cref="IViewAssetProvider.Release"/> run. The entities survive with no view, which is the
    /// intended outcome of a streaming unload, and <see cref="ChunkCascadeReleased"/> is published so
    /// that outcome is observable rather than silent.
    /// </para>
    /// <para>
    /// Only keys this chunk is the <i>last</i> referencer of are cascaded. A key another chunk still
    /// lists is not being released, so its views are in no danger and are left alone.
    /// </para>
    /// <para>
    /// <b>Accepted limitation:</b> the warm count for a key only grows. If chunk A asks for 8
    /// instances and chunk B for 2, releasing A leaves 8 warm rather than shrinking to 2. Shrinking
    /// would mean destroying pooled instances that a live chunk may be about to spawn, which is the
    /// hitch prewarming exists to avoid. Memory is reclaimed when the count reaches zero.
    /// </para>
    /// <para>
    /// Not thread-safe. Call from the main thread, like everything else that touches the pool.
    /// </para>
    /// </remarks>
    public sealed class ChunkViewProvisioner
    {
        private readonly IViewAssetProvider _provider;
        private readonly IViewCascadeSink _cascadeSink;
        private readonly IDotsPublisher<ChunkWarmed> _warmedPublisher;
        private readonly IDotsPublisher<ChunkReleased> _releasedPublisher;
        private readonly IDotsPublisher<ChunkCascadeReleased> _cascadePublisher;

        /// <summary>chunk id -> the de-duplicated key set that chunk currently holds a reference on.</summary>
        private readonly Dictionary<string, HashSet<string>> _chunkKeys = new Dictionary<string, HashSet<string>>();

        /// <summary>asset key -> number of chunks referencing it.</summary>
        private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>();

        /// <summary>asset key -> highest instance count anyone has asked to warm.</summary>
        private readonly Dictionary<string, int> _warmCounts = new Dictionary<string, int>();

        /// <summary>chunk ids whose prewarm has completed, as opposed to merely started.</summary>
        private readonly HashSet<string> _loaded = new HashSet<string>();

        /// <summary>Per-chunk lifecycle state for diagnostics.</summary>
        private readonly Dictionary<string, ChunkState> _chunkStates = new Dictionary<string, ChunkState>();

        /// <param name="cascadeSink">
        /// Tears down the views standing on the keys a release drops, before the assets go. Optional,
        /// and <b>omitting it is unsafe for streaming</b>: without it the provisioner cannot reach
        /// the view layer and releases assets that live views may still be standing on, which is the
        /// dangling-link bug this parameter exists to prevent. Pass an <c>EntityViewCascade</c>.
        /// </param>
        public ChunkViewProvisioner(
            IViewAssetProvider provider,
            IViewCascadeSink cascadeSink = null,
            IDotsPublisher<ChunkWarmed> warmedPublisher = null,
            IDotsPublisher<ChunkReleased> releasedPublisher = null,
            IDotsPublisher<ChunkCascadeReleased> cascadePublisher = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _cascadeSink = cascadeSink;
            _cascadePublisher = cascadePublisher ?? NullDotsPublisher<ChunkCascadeReleased>.Instance;
            _warmedPublisher = warmedPublisher ?? NullDotsPublisher<ChunkWarmed>.Instance;
            _releasedPublisher = releasedPublisher ?? NullDotsPublisher<ChunkReleased>.Instance;
        }

        /// <summary>Number of chunks currently holding references.</summary>
        public int ChunkCount => _chunkKeys.Count;

        /// <summary>Number of distinct keys with a non-zero reference count.</summary>
        public int TrackedKeyCount => _refCounts.Count;

        /// <summary>How many chunks reference <paramref name="key"/>; zero if none.</summary>
        public int GetReferenceCount(string key)
        {
            return key != null && _refCounts.TryGetValue(key, out var count) ? count : 0;
        }

        /// <summary>
        /// Whether the chunk holds references — i.e. it has been prewarmed and not released.
        /// </summary>
        /// <remarks>
        /// This says nothing about loading progress. It is true the instant
        /// <see cref="PrewarmChunkAsync"/> is called, before a single byte has loaded. It was called
        /// <c>IsChunkWarm</c> before 0.5.0, which every caller would reasonably read as "loads
        /// finished" — the question they actually want is <see cref="IsChunkLoaded"/>.
        /// </remarks>
        public bool IsChunkTracked(string chunkId) => chunkId != null && _chunkKeys.ContainsKey(chunkId);

        /// <summary>
        /// Whether every key the chunk asked for has finished loading, so spawning from it will not
        /// be deferred.
        /// </summary>
        public bool IsChunkLoaded(string chunkId) => chunkId != null && _loaded.Contains(chunkId);

        /// <summary>
        /// Lifecycle state of each tracked chunk. Read-only view for diagnostics and editor
        /// tooling. Keys are chunk ids; values transition Pending → Warming → Warm → Released.
        /// Released chunks are removed from the dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, ChunkState> ChunkStates => _chunkStates;

        /// <summary>Number of chunks in <see cref="ChunkState.Warm"/> state.</summary>
        public int WarmChunkCount => _loaded.Count;

        /// <summary>Number of chunks tracked but not yet fully loaded.</summary>
        public int PendingChunkCount => _chunkKeys.Count - _loaded.Count;

        /// <summary>
        /// Raised when a chunk transitions state. Useful for loading screens and editor tooling.
        /// </summary>
        public event Action<string, ChunkState> OnChunkStateChanged;

        /// <summary>
        /// Warms every key the chunk needs. Returns once all newly-required loads finished; keys
        /// already warm from another chunk cost nothing.
        /// </summary>
        /// <param name="chunkId">Opaque chunk or region identifier. Re-warming an existing id diffs.</param>
        /// <param name="keys">The prefab keys the chunk needs. Duplicates and nulls are ignored.</param>
        /// <param name="countPerKey">Instances to have ready per key. Clamped to at least 1.</param>
        public async Task PrewarmChunkAsync(string chunkId, IEnumerable<string> keys, int countPerKey = 1, CancellationToken cancellationToken = default)
        {
            if (chunkId == null) throw new ArgumentNullException(nameof(chunkId));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (countPerKey < 1) countPerKey = 1;

            var newSet = new HashSet<string>();
            foreach (var key in keys)
            {
                if (!string.IsNullOrEmpty(key)) newSet.Add(key);
            }

            // Increment first, decrement after: a key shared by the old and new set must never
            // transiently reach zero, or it would be unloaded and immediately reloaded.
            var toWarm = new List<string>();
            foreach (var key in newSet)
            {
                _refCounts.TryGetValue(key, out var count);
                _refCounts[key] = count + 1;

                _warmCounts.TryGetValue(key, out var warm);
                if (count == 0 || countPerKey > warm)
                {
                    _warmCounts[key] = Math.Max(warm, countPerKey);
                    toWarm.Add(key);
                }
            }

            if (_chunkKeys.TryGetValue(chunkId, out var oldSet))
            {
                foreach (var key in oldSet) ReleaseKey(key);
            }

            _chunkKeys[chunkId] = newSet;
            _loaded.Remove(chunkId); // re-warming reopens the loading window
            SetChunkState(chunkId, toWarm.Count > 0 ? ChunkState.Warming : ChunkState.Warm);

            if (toWarm.Count == 1)
            {
                await _provider.PrewarmAsync(toWarm[0], _warmCounts[toWarm[0]], cancellationToken);
            }
            else if (toWarm.Count > 1)
            {
                var tasks = new Task[toWarm.Count];
                for (var i = 0; i < toWarm.Count; i++)
                {
                    tasks[i] = _provider.PrewarmAsync(toWarm[i], _warmCounts[toWarm[i]], cancellationToken);
                }

                await Task.WhenAll(tasks);
            }

            // Only mark loaded if the chunk still exists as warmed here — a release or a re-warm
            // that landed while this awaited must win over a stale completion.
            if (_chunkKeys.TryGetValue(chunkId, out var current) && ReferenceEquals(current, newSet))
            {
                _loaded.Add(chunkId);
                SetChunkState(chunkId, ChunkState.Warm);
                _warmedPublisher.Publish(new ChunkWarmed(chunkId, newSet.Count));
            }
        }

        /// <summary>
        /// Drops the chunk's references. Keys whose count reaches zero are released through the
        /// provider; keys another chunk still lists are left alone.
        /// </summary>
        /// <remarks>
        /// <b>Views standing on the keys this call would release are despawned first</b>, through
        /// the ordinary despawn path, so nothing is left pointing at a released asset. Their
        /// entities survive without views and <see cref="ChunkCascadeReleased"/> is published. An
        /// unknown chunk id is a no-op, distinguishable via
        /// <see cref="ChunkReleaseResult.WasTracked"/>.
        /// </remarks>
        public ChunkReleaseResult ReleaseChunk(string chunkId)
        {
            if (chunkId == null || !_chunkKeys.TryGetValue(chunkId, out var set))
            {
                return new ChunkReleaseResult(false, false, 0, 0);
            }

            // Keys this chunk is the last referencer of — the only ones that will actually be
            // released, and therefore the only ones whose views are in danger.
            var expiring = new List<string>();
            foreach (var key in set)
            {
                if (GetReferenceCount(key) <= 1) expiring.Add(key);
            }

            // Views down first, assets after. This ordering is the entire fix.
            var despawned = expiring.Count > 0 && _cascadeSink != null
                ? _cascadeSink.CascadeDespawn(expiring)
                : 0;

            // Remove before decrementing so a re-entrant or repeated call cannot decrement twice.
            _chunkKeys.Remove(chunkId);
            _loaded.Remove(chunkId);
            foreach (var key in set) ReleaseKey(key);

            if (despawned > 0)
            {
                Debug.Log(
                    $"[Cuvara.DOTS] Chunk '{chunkId}' released {expiring.Count} key(s) and cascaded " +
                    $"{despawned} view(s) away. Those entities are still alive and now have no view.");
                _cascadePublisher.Publish(new ChunkCascadeReleased(chunkId, expiring.Count, despawned));
            }

            SetChunkState(chunkId, ChunkState.Released);
            _chunkStates.Remove(chunkId); // released chunks leave the state table
            _releasedPublisher.Publish(new ChunkReleased(chunkId, true, despawned));
            return new ChunkReleaseResult(true, true, despawned, expiring.Count);
        }

        /// <summary>
        /// Releases every chunk. For scene teardown.
        /// </summary>
        /// <returns>Total views the cascades despawned.</returns>
        public int ReleaseAll()
        {
            var chunkIds = new List<string>(_chunkKeys.Keys);
            var despawned = 0;
            foreach (var chunkId in chunkIds) despawned += ReleaseChunk(chunkId).ViewsDespawned;
            return despawned;
        }

        private void ReleaseKey(string key)
        {
            if (!_refCounts.TryGetValue(key, out var count)) return;

            if (count > 1)
            {
                _refCounts[key] = count - 1;
                return;
            }

            _refCounts.Remove(key);
            _warmCounts.Remove(key);
            _provider.Release(key);
        }

        private void SetChunkState(string chunkId, ChunkState newState)
        {
            _chunkStates[chunkId] = newState;
            OnChunkStateChanged?.Invoke(chunkId, newState);
        }
    }
}
