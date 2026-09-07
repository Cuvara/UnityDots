using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// <see cref="IViewAssetProvider"/> with GameObject pooling. Acquire reactivates from
    /// the pool; release deactivates and returns. No Instantiate/Destroy in steady state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An RPG MMO spawns and despawns hundreds of entities per minute (AOI churn: a player
    /// walks and entities behind them leave the interest radius while new ones enter ahead).
    /// Without pooling, every churn cycle is an Instantiate + Destroy pair, each of which
    /// hitches the main thread. With pooling, churn is SetActive(true) + SetActive(false) —
    /// three orders of magnitude cheaper.
    /// </para>
    /// <para>
    /// <b>Prefabs are registered, not loaded.</b> This provider does not touch Addressables,
    /// Resources, or any asset pipeline. The caller registers prefabs by key via
    /// <see cref="RegisterPrefab"/> before prewarming. A host that loads from Addressables
    /// registers after the load completes.
    /// </para>
    /// <para>
    /// <b>Ownership contract.</b> Every instance this provider hands out was instantiated by this
    /// provider, and only those are ever deactivated, re-parented or destroyed by it. Identity is
    /// tracked per instance in a lease table — <c>GameObject.name</c> is set for the hierarchy
    /// view and nothing reads it back, so renaming an instance changes nothing. The pool root is
    /// owned only when the provider created it (<c>poolRoot == null</c>); a caller-supplied root is
    /// never destroyed, only emptied of the provider's own children.
    /// </para>
    /// <para>
    /// <b>Return policies.</b> Returning an instance that is already pooled is ignored and counted
    /// in <see cref="DuplicateReleaseCount"/>; it can never be enqueued twice, so two later
    /// acquires can never receive the same object. Returning an instance this provider did not
    /// create is ignored and counted in <see cref="ForeignReleaseCount"/> — it is not the
    /// provider's to deactivate or destroy. Returning an instance that was destroyed externally
    /// drops its lease. Returning after <see cref="Dispose"/> is a no-op, so a despawn system that
    /// tears down after the pool does not throw.
    /// </para>
    /// <para>
    /// <b>Re-registration.</b> Registering a different prefab under an existing key destroys that
    /// key's pooled instances (they would render the old prefab) and un-warms the key. Instances
    /// currently acquired keep running; when returned they are destroyed instead of pooled, because
    /// the pool now belongs to the new prefab. Registering the same prefab again is a no-op.
    /// </para>
    /// <para>
    /// <b>Pool cap versus admission budget.</b> <c>maxPoolSize</c> bounds the <i>inactive</i>
    /// instances kept per key; a return beyond it destroys the instance. It does not bound how many
    /// instances exist: the total is acquired + pooled, and acquired is bounded only by
    /// <c>maxActivePerKey</c>, the admission budget. With the default of 0 (unlimited) the provider
    /// will happily instantiate as many views as callers ask for, which is the correct default for
    /// a server-authoritative client that must show every entity in its area of interest; a host
    /// that wants a hard ceiling sets the budget and treats a <c>null</c> from <see cref="Acquire"/>
    /// as "not admitted" (counted in <see cref="AdmissionRejectedCount"/>).
    /// </para>
    /// <para>
    /// Not thread-safe. Main thread only, like every Unity object operation.
    /// </para>
    /// </remarks>
    public sealed class PooledViewAssetProvider : IViewAssetProvider, IDisposable
    {
        /// <summary>Everything the provider knows about one instance it created.</summary>
        private sealed class Lease
        {
            public string Key;

            /// <summary>Registration generation of the prefab this instance was made from.</summary>
            public int Registration;

            /// <summary>True while handed out; false while parked in the pool.</summary>
            public bool Acquired;
        }

        private readonly Transform _poolRoot;
        private readonly bool _ownsPoolRoot;
        private readonly int _defaultPoolSize;
        private readonly int _maxPoolSize;
        private readonly int _maxActivePerKey;
        private readonly IPooledViewLifecycle _lifecycle;
        private readonly OutstandingLeasePolicy _outstandingLeasePolicy;

        private readonly Dictionary<string, GameObject> _prefabs = new();
        private readonly Dictionary<string, int> _registrations = new();
        private readonly Dictionary<string, Queue<GameObject>> _pools = new();
        private readonly Dictionary<string, int> _warmCounts = new();
        private readonly Dictionary<string, int> _activeByKey = new();

        /// <summary>instance -> lease, for every instance this provider created and still tracks.</summary>
        private readonly Dictionary<GameObject, Lease> _leases = new();

        private int _activeCount;
        private bool _disposed;

        /// <summary>
        /// Creates a pooled provider.
        /// </summary>
        /// <param name="poolRoot">
        /// Parent transform for deactivated pooled instances. If null, a hidden root is created and
        /// owned by the provider — destroyed on <see cref="Dispose"/>. A supplied root is
        /// caller-owned and survives <see cref="Dispose"/>.
        /// </param>
        /// <param name="defaultPoolSize">Instances to prewarm per key when count is not specified.</param>
        /// <param name="maxPoolSize">
        /// Maximum pooled (inactive) instances per key. Beyond this, released instances are destroyed.
        /// Prevents unbounded memory growth from a key that had a transient spike. Does not bound
        /// the number of acquired instances — see <paramref name="maxActivePerKey"/>.
        /// </param>
        /// <param name="lifecycle">
        /// Optional host hook to reset reusable state on acquire/release. See
        /// <see cref="IPooledViewLifecycle"/>.
        /// </param>
        /// <param name="outstandingLeasePolicy">
        /// What to do with instances still acquired when <see cref="Dispose"/> runs.
        /// </param>
        /// <param name="maxActivePerKey">
        /// Admission budget: maximum acquired instances per key, 0 for unlimited. When reached,
        /// <see cref="Acquire"/> returns null and <see cref="AdmissionRejectedCount"/> increments.
        /// </param>
        public PooledViewAssetProvider(
            Transform poolRoot = null,
            int defaultPoolSize = 8,
            int maxPoolSize = 64,
            IPooledViewLifecycle lifecycle = null,
            OutstandingLeasePolicy outstandingLeasePolicy = OutstandingLeasePolicy.Destroy,
            int maxActivePerKey = 0)
        {
            _defaultPoolSize = Math.Max(1, defaultPoolSize);
            _maxPoolSize = Math.Max(_defaultPoolSize, maxPoolSize);
            _maxActivePerKey = Math.Max(0, maxActivePerKey);
            _lifecycle = lifecycle;
            _outstandingLeasePolicy = outstandingLeasePolicy;

            if (poolRoot != null)
            {
                _poolRoot = poolRoot;
                _ownsPoolRoot = false;
            }
            else
            {
                var go = new GameObject("[PooledViewAssetProvider]");
                go.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                _poolRoot = go.transform;
                _ownsPoolRoot = true;
            }
        }

        /// <summary>Total active (acquired) instances across all keys.</summary>
        public int ActiveCount => _activeCount;

        /// <summary>Total pooled (inactive, ready to reuse) instances across all keys.</summary>
        public int PooledCount
        {
            get
            {
                int count = 0;
                foreach (var q in _pools.Values) count += q.Count;
                return count;
            }
        }

        /// <summary>Every instance this provider currently tracks: acquired + pooled.</summary>
        public int TotalInstanceCount => _leases.Count;

        /// <summary>Number of registered prefab keys.</summary>
        public int RegisteredKeyCount => _prefabs.Count;

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// True when the provider created its own pool root and will destroy it on dispose; false
        /// when the root was supplied by the caller.
        /// </summary>
        public bool OwnsPoolRoot => _ownsPoolRoot;

        /// <summary>The transform pooled instances are parked under. Null after dispose of an owned root.</summary>
        public Transform PoolRoot => _poolRoot;

        /// <summary>Admission budget per key; 0 means unlimited.</summary>
        public int MaxActivePerKey => _maxActivePerKey;

        /// <summary>Inactive-pool cap per key.</summary>
        public int MaxPoolSize => _maxPoolSize;

        /// <summary>Returns of an instance that was already pooled. Each was ignored.</summary>
        public int DuplicateReleaseCount { get; private set; }

        /// <summary>Returns of an instance this provider did not create. Each was left untouched.</summary>
        public int ForeignReleaseCount { get; private set; }

        /// <summary>Acquires refused because the key's admission budget was full.</summary>
        public int AdmissionRejectedCount { get; private set; }

        /// <summary>Tracked instances found destroyed externally and dropped from tracking.</summary>
        public int ExternallyDestroyedCount { get; private set; }

        /// <summary>
        /// Registers a prefab for a key. Must be called before <see cref="PrewarmAsync"/> or
        /// <see cref="Acquire"/>. Registering the same prefab again is a no-op. Registering a
        /// <i>different</i> prefab replaces it: pooled instances of the old prefab are destroyed,
        /// the key is no longer warm, and acquired instances of the old prefab are destroyed rather
        /// than pooled when returned.
        /// </summary>
        public void RegisterPrefab(string key, GameObject prefab)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty.", nameof(key));
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            if (_prefabs.TryGetValue(key, out var existing) && existing == prefab) return;

            if (existing != null)
            {
                // The pool belongs to the new prefab from here on. Old inactive instances would
                // render the wrong thing on their next acquire, so they go now; live ones are
                // caught by the registration stamp on their lease when they come back.
                DestroyPooled(key);
                _warmCounts.Remove(key);
            }

            _prefabs[key] = prefab;
            _registrations.TryGetValue(key, out var generation);
            _registrations[key] = generation + 1;
        }

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_prefabs.TryGetValue(key, out var prefab))
                throw new InvalidOperationException($"No prefab registered for key '{key}'. Call RegisterPrefab first.");

            // Before any work: a cancelled prewarm creates nothing.
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            var pool = GetOrCreatePool(key);
            int needed = Math.Max(count, _defaultPoolSize) - pool.Count;
            for (int i = 0; i < needed; i++)
            {
                // Between instances too: what was already instantiated is tracked and pooled, so a
                // mid-loop cancel leaks nothing — it just leaves the key un-warm.
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

                var instance = CreateInstance(key, prefab);
                Park(instance, pool);
            }

            _warmCounts[key] = Math.Max(_warmCounts.GetValueOrDefault(key), count);
            return Task.CompletedTask;
        }

        public bool IsWarm(string key) => !_disposed && _warmCounts.ContainsKey(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            ThrowIfDisposed();
            if (key == null || !_prefabs.TryGetValue(key, out var prefab)) return null;

            _activeByKey.TryGetValue(key, out var active);
            if (_maxActivePerKey > 0 && active >= _maxActivePerKey)
            {
                AdmissionRejectedCount++;
                return null;
            }

            GameObject instance = null;
            if (_pools.TryGetValue(key, out var pool))
            {
                while (pool.Count > 0)
                {
                    var candidate = pool.Dequeue();
                    if (candidate == null)
                    {
                        // Destroyed while pooled. Drop the lease; the dictionary still finds the
                        // dead reference by instance id.
                        if (candidate is not null) _leases.Remove(candidate);
                        ExternallyDestroyedCount++;
                        continue;
                    }

                    instance = candidate;
                    break;
                }
            }

            if (instance == null) instance = CreateInstance(key, prefab);

            var lease = _leases[instance];
            lease.Acquired = true;
            _activeByKey[key] = active + 1;
            _activeCount++;

            var t = instance.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);

            _lifecycle?.OnAcquired(key, instance);
            return instance;
        }

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation,
            Transform parent = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<GameObject>(cancellationToken);
            return Task.FromResult(Acquire(key, position, rotation, parent));
        }

        public void ReleaseInstance(GameObject instance)
        {
            // Reference-null: nothing to look up. Unity-null (destroyed) still has an instance id
            // and is handled below, because its lease has to be dropped.
            if (instance is null) return;

            // Teardown ordering is not the caller's problem: a despawn system that runs after the
            // pool was disposed has nothing left to return to and must not throw.
            if (_disposed) return;

            if (!_leases.TryGetValue(instance, out var lease))
            {
                // Not ours. Never deactivate, re-parent or destroy something we did not create.
                ForeignReleaseCount++;
                return;
            }

            if (!lease.Acquired)
            {
                // Already parked. Enqueueing again would let two later acquires share one object.
                DuplicateReleaseCount++;
                return;
            }

            // Bookkeeping first, so the state is consistent whatever happens to the object below.
            lease.Acquired = false;
            _activeCount--;
            if (_activeByKey.TryGetValue(lease.Key, out var active))
            {
                if (active <= 1) _activeByKey.Remove(lease.Key);
                else _activeByKey[lease.Key] = active - 1;
            }

            if (instance == null)
            {
                // Destroyed while acquired (scene unload, manual Destroy). Nothing to park.
                _leases.Remove(instance);
                ExternallyDestroyedCount++;
                return;
            }

            _lifecycle?.OnReleased(lease.Key, instance);

            _registrations.TryGetValue(lease.Key, out var currentRegistration);
            Queue<GameObject> pool = null;
            var poolable = lease.Registration == currentRegistration
                           && _pools.TryGetValue(lease.Key, out pool)
                           && pool.Count < _maxPoolSize;

            if (poolable)
            {
                Park(instance, pool);
            }
            else
            {
                // Stale prefab, released key, or pool at cap: the instance is ours and goes away.
                _leases.Remove(instance);
                SafeDestroy(instance);
            }
        }

        /// <summary>
        /// Drops the key's pooled instances and un-warms it. The prefab stays registered — this
        /// provider registers rather than loads, so there is nothing to unload. Instances of the key
        /// that are still acquired keep running and are destroyed rather than pooled when returned;
        /// callers that need them gone first run them through the view layer's cascade
        /// (<see cref="ChunkViewProvisioner"/> does).
        /// </summary>
        public void Release(string key)
        {
            if (_disposed || key == null) return;
            DestroyPooled(key);
            _pools.Remove(key);
            _warmCounts.Remove(key);
        }

        /// <summary>
        /// Drops tracking for every instance that was destroyed externally, pooled or acquired.
        /// Counts reconcile afterwards. Call after a scene unload that may have taken views with it.
        /// </summary>
        /// <returns>Instances found destroyed.</returns>
        public int SweepDestroyed()
        {
            if (_disposed) return 0;

            var dead = new List<GameObject>();
            foreach (var pair in _leases)
            {
                if (pair.Key == null) dead.Add(pair.Key);
            }

            foreach (var go in dead)
            {
                var lease = _leases[go];
                _leases.Remove(go);
                if (lease.Acquired)
                {
                    _activeCount--;
                    if (_activeByKey.TryGetValue(lease.Key, out var active))
                    {
                        if (active <= 1) _activeByKey.Remove(lease.Key);
                        else _activeByKey[lease.Key] = active - 1;
                    }
                }
            }

            // Pooled dead references sit in the queues too; rebuild each queue without them.
            foreach (var pair in _pools)
            {
                var pool = pair.Value;
                if (pool.Count == 0) continue;
                var survivors = new List<GameObject>(pool.Count);
                while (pool.Count > 0)
                {
                    var go = pool.Dequeue();
                    if (go != null) survivors.Add(go);
                }

                foreach (var go in survivors) pool.Enqueue(go);
            }

            ExternallyDestroyedCount += dead.Count;
            return dead.Count;
        }

        /// <summary>
        /// Whether this provider created <paramref name="instance"/> and still tracks it, pooled or
        /// acquired. Renaming, re-parenting or deactivating the object does not change the answer.
        /// </summary>
        public bool IsOwned(GameObject instance) => instance is not null && _leases.ContainsKey(instance);

        /// <summary>The key an owned instance was created for; false for foreign or untracked instances.</summary>
        public bool TryGetKey(GameObject instance, out string key)
        {
            if (instance is not null && _leases.TryGetValue(instance, out var lease))
            {
                key = lease.Key;
                return true;
            }

            key = null;
            return false;
        }

        /// <summary>True while the owned instance is handed out; false if pooled or not owned.</summary>
        public bool IsAcquired(GameObject instance) =>
            instance is not null && _leases.TryGetValue(instance, out var lease) && lease.Acquired;

        /// <summary>
        /// Destroys every pooled instance, reclaims outstanding acquired instances per the
        /// <see cref="OutstandingLeasePolicy"/>, destroys the pool root if this provider created
        /// it, and clears all state. Idempotent: a second call does nothing. Acquisition and
        /// registration throw <see cref="ObjectDisposedException"/> afterwards; returns are no-ops.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var pool in _pools.Values)
            {
                while (pool.Count > 0)
                {
                    var instance = pool.Dequeue();
                    if (instance != null) SafeDestroy(instance);
                }
            }

            var outstanding = 0;
            foreach (var pair in _leases)
            {
                if (!pair.Value.Acquired) continue;
                outstanding++;
                if (_outstandingLeasePolicy == OutstandingLeasePolicy.Destroy && pair.Key != null)
                    SafeDestroy(pair.Key);
            }

            if (outstanding > 0)
            {
                Debug.LogWarning(
                    $"[Cuvara.DOTS] PooledViewAssetProvider disposed with {outstanding} instance(s) still " +
                    $"acquired; policy {_outstandingLeasePolicy}. Each is a despawn the host did not run.");
            }

            _pools.Clear();
            _warmCounts.Clear();
            _activeByKey.Clear();
            _activeCount = 0;
            _leases.Clear();
            _prefabs.Clear();
            _registrations.Clear();

            // Only what we created. A caller-supplied root belongs to the caller; it has been
            // emptied of our instances above and is otherwise untouched.
            if (_ownsPoolRoot && _poolRoot != null)
                SafeDestroy(_poolRoot.gameObject);
        }

        /// <summary>Number of pooled (inactive) instances for a key.</summary>
        public int GetPooledCount(string key) =>
            key != null && _pools.TryGetValue(key, out var pool) ? pool.Count : 0;

        /// <summary>Number of active (acquired) instances for a key.</summary>
        public int GetActiveCount(string key) =>
            key != null && _activeByKey.TryGetValue(key, out var count) ? count : 0;

        private GameObject CreateInstance(string key, GameObject prefab)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            // Diagnostic only. Nothing reads this back; the lease table is the identity.
            instance.name = $"{prefab.name} [pool:{key}]";
            _registrations.TryGetValue(key, out var registration);
            _leases[instance] = new Lease { Key = key, Registration = registration, Acquired = false };
            return instance;
        }

        private void Park(GameObject instance, Queue<GameObject> pool)
        {
            instance.SetActive(false);
            // A caller-owned root may have been destroyed under us; parking at the scene root is
            // still correct, just less tidy.
            instance.transform.SetParent(_poolRoot != null ? _poolRoot : null, false);
            pool.Enqueue(instance);
        }

        private Queue<GameObject> GetOrCreatePool(string key)
        {
            if (!_pools.TryGetValue(key, out var pool))
            {
                pool = new Queue<GameObject>();
                _pools[key] = pool;
            }

            return pool;
        }

        private void DestroyPooled(string key)
        {
            if (!_pools.TryGetValue(key, out var pool)) return;
            while (pool.Count > 0)
            {
                var instance = pool.Dequeue();
                if (instance is null) continue;
                _leases.Remove(instance);
                if (instance != null) SafeDestroy(instance);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledViewAssetProvider));
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEngine.Object.DestroyImmediate(obj);
                return;
            }
#endif
            UnityEngine.Object.Destroy(obj);
        }
    }
}
