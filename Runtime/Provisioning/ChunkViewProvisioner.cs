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
    /// <b>Epochs — why a stale completion cannot resurrect a chunk.</b> Every
    /// <see cref="PrewarmChunkAsync"/> call stamps the chunk with a fresh epoch before it awaits.
    /// When the await returns, the chunk is marked <see cref="ChunkState.Warm"/> only if it is still
    /// tracked <i>with that same epoch</i>. A release, or a re-warm of the same id, that landed in
    /// the meantime changed or removed the epoch, so the older completion finds a mismatch and does
    /// nothing — no <see cref="ChunkWarmed"/>, no state change, no counter touched. The legal
    /// transitions are listed on <see cref="ChunkState"/>.
    /// </para>
    /// <para>
    /// <b>Failure and retry.</b> If a provider prewarm faults or is cancelled while the epoch is
    /// still current, the chunk's references are rolled back through the ordinary release path
    /// (cascade included), the keys it tried to warm are marked not-warm so the next requester
    /// re-issues the load, the chunk transitions to <see cref="ChunkState.Failed"/> and leaves the
    /// table, and the exception propagates to the awaiting caller. Retrying is a plain
    /// <see cref="PrewarmChunkAsync"/> with the same id. If the epoch is no longer current — the
    /// chunk was released or re-warmed while the failed load was in flight — nothing is rolled
    /// back, because the newer operation already owns the chunk's state; the exception still
    /// propagates. Counts therefore reconcile on every path: after any sequence of warm and release
    /// calls whose awaits have all settled, <see cref="TrackedKeyCount"/> is exactly the number of
    /// keys some tracked chunk still lists.
    /// </para>
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
    /// Because that ordering is the whole safety argument, the sink is <b>required</b>. A streaming
    /// world passes <c>EntityViewCascade</c>; a context in which no view can exist says so with
    /// <see cref="NullViewCascadeSink.Instance"/>. Passing null throws at construction rather than
    /// stranding views at the first release.
    /// </para>
    /// <para>
    /// Only keys this chunk is the <i>last</i> referencer of are cascaded. A key another chunk still
    /// lists is not being released, so its views are in no danger and are left alone.
    /// </para>
    /// <para>
    /// <b>Chunk-owned versus session-owned assets.</b> Keys a whole session needs regardless of
    /// where the player stands — the local player, common projectiles, UI-attached views — are
    /// pinned with <see cref="PinSessionKeysAsync"/>. A pin is a reference like any chunk's, held
    /// under the reserved id <see cref="SessionId"/>, so no chunk release can ever drop the last
    /// reference to a pinned key; only <see cref="ReleaseSessionKeys"/> can, and it cascades like
    /// any other release. <see cref="ReleaseAll"/> leaves the pin alone unless told otherwise.
    /// </para>
    /// <para>
    /// <b>Accepted limitation:</b> the warm count for a key only grows. If chunk A asks for 8
    /// instances and chunk B for 2, releasing A leaves 8 warm rather than shrinking to 2. Shrinking
    /// would mean destroying pooled instances that a live chunk may be about to spawn, which is the
    /// hitch prewarming exists to avoid. Memory is reclaimed when the count reaches zero.
    /// </para>
    /// <para>
    /// <b>Thread affinity.</b> Every public member must be called on the thread that constructed
    /// the provisioner — the main thread — and throws <see cref="InvalidOperationException"/>
    /// otherwise. Provider loads may run anywhere; the continuation after the await is where Unity
    /// objects are touched, and Unity's <c>SynchronizationContext</c> brings it back to the main
    /// thread. The post-await path asserts this too, so a provider that completes on a worker
    /// thread in an environment without that context fails loudly instead of mutating the pool
    /// off-thread.
    /// </para>
    /// </remarks>
    public sealed class ChunkViewProvisioner
    {
        /// <summary>
        /// Reserved chunk id under which <see cref="PinSessionKeysAsync"/> holds session-wide
        /// references. Not accepted by <see cref="PrewarmChunkAsync"/> or <see cref="ReleaseChunk"/>.
        /// </summary>
        public const string SessionId = "$session";

        private sealed class ChunkRecord
        {
            public HashSet<string> Keys;

            /// <summary>Unique per prewarm call; a completion whose epoch no longer matches is stale.</summary>
            public int Epoch;
        }

        private readonly IViewAssetProvider _provider;
        private readonly IViewCascadeSink _cascadeSink;
        private readonly IDotsPublisher<ChunkWarmed> _warmedPublisher;
        private readonly IDotsPublisher<ChunkReleased> _releasedPublisher;
        private readonly IDotsPublisher<ChunkCascadeReleased> _cascadePublisher;
        private readonly int _mainThreadId;

        /// <summary>chunk id -> the de-duplicated key set that chunk currently holds a reference on, plus its epoch.</summary>
        private readonly Dictionary<string, ChunkRecord> _chunks = new Dictionary<string, ChunkRecord>();

        /// <summary>asset key -> number of chunks referencing it.</summary>
        private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>();

        /// <summary>asset key -> highest instance count a <i>completed</i> or in-flight prewarm asked for.</summary>
        private readonly Dictionary<string, int> _warmCounts = new Dictionary<string, int>();

        /// <summary>chunk ids whose prewarm has completed, as opposed to merely started.</summary>
        private readonly HashSet<string> _loaded = new HashSet<string>();

        /// <summary>Per-chunk lifecycle state for diagnostics.</summary>
        private readonly Dictionary<string, ChunkState> _chunkStates = new Dictionary<string, ChunkState>();

        private int _nextEpoch;

        /// <param name="cascadeSink">
        /// Tears down the views standing on the keys a release drops, before the assets go.
        /// <b>Required.</b> Pass an <c>EntityViewCascade</c> for a streaming world, or
        /// <see cref="NullViewCascadeSink.Instance"/> to state explicitly that no view layer exists.
        /// </param>
        public ChunkViewProvisioner(
            IViewAssetProvider provider,
            IViewCascadeSink cascadeSink,
            IDotsPublisher<ChunkWarmed> warmedPublisher = null,
            IDotsPublisher<ChunkReleased> releasedPublisher = null,
            IDotsPublisher<ChunkCascadeReleased> cascadePublisher = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _cascadeSink = cascadeSink ?? throw new ArgumentNullException(
                nameof(cascadeSink),
                "ChunkViewProvisioner needs a cascade sink: releasing a chunk without one strands every " +
                "live view standing on its keys. Pass EntityViewCascade for a streaming world, or " +
                "NullViewCascadeSink.Instance where no view layer exists.");
            _cascadePublisher = cascadePublisher ?? NullDotsPublisher<ChunkCascadeReleased>.Instance;
            _warmedPublisher = warmedPublisher ?? NullDotsPublisher<ChunkWarmed>.Instance;
            _releasedPublisher = releasedPublisher ?? NullDotsPublisher<ChunkReleased>.Instance;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Number of chunks currently holding references, not counting the session pin.</summary>
        public int ChunkCount => _chunks.Count - (_chunks.ContainsKey(SessionId) ? 1 : 0);

        /// <summary>Number of distinct keys with a non-zero reference count.</summary>
        public int TrackedKeyCount => _refCounts.Count;

        /// <summary>Whether <see cref="PinSessionKeysAsync"/> currently holds any key.</summary>
        public bool IsSessionPinned => _chunks.ContainsKey(SessionId);

        /// <summary>How many chunks (including the session pin) reference <paramref name="key"/>; zero if none.</summary>
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
        public bool IsChunkTracked(string chunkId) => chunkId != null && _chunks.ContainsKey(chunkId);

        /// <summary>
        /// Whether every key the chunk asked for has finished loading, so spawning from it will not
        /// be deferred.
        /// </summary>
        public bool IsChunkLoaded(string chunkId) => chunkId != null && _loaded.Contains(chunkId);

        /// <summary>
        /// Lifecycle state of each tracked chunk. Read-only view for diagnostics and editor
        /// tooling. Keys are chunk ids; transitions are listed on <see cref="ChunkState"/>.
        /// Released and failed chunks are removed from the dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, ChunkState> ChunkStates => _chunkStates;

        /// <summary>Number of chunks in <see cref="ChunkState.Warm"/> state.</summary>
        public int WarmChunkCount => _loaded.Count;

        /// <summary>Number of chunks tracked but not yet fully loaded.</summary>
        public int PendingChunkCount => _chunks.Count - _loaded.Count;

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
        /// <param name="cancellationToken">
        /// Checked before any state is touched — a pre-cancelled call changes nothing — and passed
        /// to the provider. Cancellation mid-load rolls the chunk back; see the class remarks.
        /// </param>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public Task PrewarmChunkAsync(string chunkId, IEnumerable<string> keys, int countPerKey = 1, CancellationToken cancellationToken = default)
        {
            if (chunkId == null) throw new ArgumentNullException(nameof(chunkId));
            if (chunkId == SessionId) throw new ArgumentException($"'{SessionId}' is reserved; use PinSessionKeysAsync.", nameof(chunkId));
            return WarmCoreAsync(chunkId, keys, countPerKey, cancellationToken);
        }

        /// <summary>
        /// Holds session-wide references on <paramref name="keys"/> so no chunk release can drop
        /// them. Calling again replaces the pinned set (a diff, like a chunk re-warm).
        /// </summary>
        public Task PinSessionKeysAsync(IEnumerable<string> keys, int countPerKey = 1, CancellationToken cancellationToken = default)
        {
            return WarmCoreAsync(SessionId, keys, countPerKey, cancellationToken);
        }

        private async Task WarmCoreAsync(string chunkId, IEnumerable<string> keys, int countPerKey, CancellationToken cancellationToken)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            AssertMainThread();
            if (countPerKey < 1) countPerKey = 1;

            // Before any state is touched: a cancelled warm must leave nothing behind.
            cancellationToken.ThrowIfCancellationRequested();

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

            if (_chunks.TryGetValue(chunkId, out var old))
            {
                foreach (var key in old.Keys) ReleaseKey(key);
            }

            var epoch = ++_nextEpoch;
            _chunks[chunkId] = new ChunkRecord { Keys = newSet, Epoch = epoch };
            _loaded.Remove(chunkId); // re-warming reopens the loading window
            SetChunkState(chunkId, toWarm.Count > 0 ? ChunkState.Warming : ChunkState.Warm);

            if (toWarm.Count == 0)
            {
                _loaded.Add(chunkId);
                _warmedPublisher.Publish(new ChunkWarmed(chunkId, newSet.Count));
                return;
            }

            try
            {
                if (toWarm.Count == 1)
                {
                    await _provider.PrewarmAsync(toWarm[0], _warmCounts[toWarm[0]], cancellationToken);
                }
                else
                {
                    var tasks = new Task[toWarm.Count];
                    for (var i = 0; i < toWarm.Count; i++)
                    {
                        tasks[i] = _provider.PrewarmAsync(toWarm[i], _warmCounts[toWarm[i]], cancellationToken);
                    }

                    await Task.WhenAll(tasks);
                }
            }
            catch
            {
                AssertMainThread();
                if (IsCurrentEpoch(chunkId, epoch))
                {
                    // The keys this attempt tried to load are not warm, whatever the count table
                    // says; drop the entries so the next requester re-issues the load. Then roll the
                    // chunk back through the ordinary release path — cascade included, in case a
                    // synchronous Acquire fallback spawned something during the load.
                    foreach (var key in toWarm) _warmCounts.Remove(key);
                    ReleaseCore(chunkId, ChunkState.Failed);
                }

                throw;
            }

            AssertMainThread();

            // Only mark loaded if this epoch is still the chunk's — a release or a re-warm that
            // landed while this awaited must win over a stale completion.
            if (IsCurrentEpoch(chunkId, epoch))
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
        /// <see cref="ChunkReleaseResult.WasTracked"/>. Releasing a chunk that is still warming is
        /// legal: its references drop now, and the in-flight load's completion is ignored.
        /// </remarks>
        public ChunkReleaseResult ReleaseChunk(string chunkId)
        {
            AssertMainThread();
            if (chunkId == null || chunkId == SessionId) return new ChunkReleaseResult(false, false, 0, 0);
            return ReleaseCore(chunkId, ChunkState.Released);
        }

        /// <summary>Drops the session pin's references, cascading views on keys nothing else holds.</summary>
        public ChunkReleaseResult ReleaseSessionKeys()
        {
            AssertMainThread();
            return ReleaseCore(SessionId, ChunkState.Released);
        }

        /// <summary>
        /// Releases every chunk. For scene teardown.
        /// </summary>
        /// <param name="includeSession">
        /// Also drop the session pin. False by default: a scene change is not a session end.
        /// </param>
        /// <returns>Total views the cascades despawned.</returns>
        public int ReleaseAll(bool includeSession = false)
        {
            AssertMainThread();
            var chunkIds = new List<string>(_chunks.Keys);
            var despawned = 0;
            foreach (var chunkId in chunkIds)
            {
                if (chunkId == SessionId && !includeSession) continue;
                despawned += ReleaseCore(chunkId, ChunkState.Released).ViewsDespawned;
            }

            return despawned;
        }

        private ChunkReleaseResult ReleaseCore(string chunkId, ChunkState finalState)
        {
            if (!_chunks.TryGetValue(chunkId, out var record))
            {
                return new ChunkReleaseResult(false, false, 0, 0);
            }

            var set = record.Keys;

            // Keys this chunk is the last referencer of — the only ones that will actually be
            // released, and therefore the only ones whose views are in danger.
            var expiring = new List<string>();
            foreach (var key in set)
            {
                if (GetReferenceCount(key) <= 1) expiring.Add(key);
            }

            // Views down first, assets after. This ordering is the entire fix.
            var despawned = expiring.Count > 0 ? _cascadeSink.CascadeDespawn(expiring) : 0;

            // Remove before decrementing so a re-entrant or repeated call cannot decrement twice,
            // and so an in-flight prewarm of this chunk finds its epoch gone.
            _chunks.Remove(chunkId);
            _loaded.Remove(chunkId);
            foreach (var key in set) ReleaseKey(key);

            if (despawned > 0)
            {
                Debug.Log(
                    $"[Cuvara.DOTS] Chunk '{chunkId}' released {expiring.Count} key(s) and cascaded " +
                    $"{despawned} view(s) away. Those entities are still alive and now have no view.");
                _cascadePublisher.Publish(new ChunkCascadeReleased(chunkId, expiring.Count, despawned));
            }

            SetChunkState(chunkId, finalState);
            _chunkStates.Remove(chunkId); // released and failed chunks leave the state table
            _releasedPublisher.Publish(new ChunkReleased(chunkId, true, despawned));
            return new ChunkReleaseResult(true, true, despawned, expiring.Count);
        }

        private bool IsCurrentEpoch(string chunkId, int epoch) =>
            _chunks.TryGetValue(chunkId, out var record) && record.Epoch == epoch;

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

        private void AssertMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "ChunkViewProvisioner must be used on the thread that created it (the main thread). " +
                    $"Created on {_mainThreadId}, called on {Thread.CurrentThread.ManagedThreadId}.");
            }
        }
    }
}
