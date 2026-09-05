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
    /// </remarks>
    public sealed class PooledViewAssetProvider : IViewAssetProvider, IDisposable
    {
        private readonly Transform _poolRoot;
        private readonly int _defaultPoolSize;
        private readonly int _maxPoolSize;

        private readonly Dictionary<string, GameObject> _prefabs = new();
        private readonly Dictionary<string, Queue<GameObject>> _pools = new();
        private readonly Dictionary<string, int> _warmCounts = new();
        private readonly HashSet<GameObject> _active = new();

        /// <summary>
        /// Creates a pooled provider.
        /// </summary>
        /// <param name="poolRoot">
        /// Parent transform for deactivated pooled instances. If null, a hidden root is created.
        /// </param>
        /// <param name="defaultPoolSize">Instances to prewarm per key when count is not specified.</param>
        /// <param name="maxPoolSize">
        /// Maximum pooled (inactive) instances per key. Beyond this, released instances are destroyed.
        /// Prevents unbounded memory growth from a key that had a transient spike.
        /// </param>
        public PooledViewAssetProvider(Transform poolRoot = null, int defaultPoolSize = 8, int maxPoolSize = 64)
        {
            _defaultPoolSize = Math.Max(1, defaultPoolSize);
            _maxPoolSize = Math.Max(_defaultPoolSize, maxPoolSize);

            if (poolRoot != null)
            {
                _poolRoot = poolRoot;
            }
            else
            {
                var go = new GameObject("[PooledViewAssetProvider]");
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _poolRoot = go.transform;
            }
        }

        /// <summary>Total active (acquired) instances across all keys.</summary>
        public int ActiveCount => _active.Count;

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

        /// <summary>Number of registered prefab keys.</summary>
        public int RegisteredKeyCount => _prefabs.Count;

        /// <summary>
        /// Registers a prefab for a key. Must be called before <see cref="PrewarmAsync"/> or
        /// <see cref="Acquire"/>. Safe to call repeatedly with the same key — the last prefab wins.
        /// </summary>
        public void RegisterPrefab(string key, GameObject prefab)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty.", nameof(key));
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            _prefabs[key] = prefab;
        }

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            if (!_prefabs.TryGetValue(key, out var prefab))
                throw new InvalidOperationException($"No prefab registered for key '{key}'. Call RegisterPrefab first.");

            if (!_pools.TryGetValue(key, out var pool))
            {
                pool = new Queue<GameObject>();
                _pools[key] = pool;
            }

            int needed = Math.Max(count, _defaultPoolSize) - pool.Count;
            for (int i = 0; i < needed; i++)
            {
                var instance = UnityEngine.Object.Instantiate(prefab, _poolRoot);
                instance.SetActive(false);
                instance.name = $"{prefab.name} [pool:{key}]";
                pool.Enqueue(instance);
            }

            _warmCounts[key] = Math.Max(_warmCounts.GetValueOrDefault(key), count);
            return Task.CompletedTask;
        }

        public bool IsWarm(string key) => _warmCounts.ContainsKey(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (!_prefabs.TryGetValue(key, out var prefab)) return null;

            GameObject instance;

            if (_pools.TryGetValue(key, out var pool) && pool.Count > 0)
            {
                instance = pool.Dequeue();
                // Skip destroyed instances (external destruction while pooled)
                while (instance == null && pool.Count > 0)
                    instance = pool.Dequeue();

                if (instance == null)
                {
                    instance = UnityEngine.Object.Instantiate(prefab);
                    instance.name = $"{prefab.name} [pool:{key}]";
                }
            }
            else
            {
                instance = UnityEngine.Object.Instantiate(prefab);
                instance.name = $"{prefab.name} [pool:{key}]";
            }

            var t = instance.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);

            _active.Add(instance);
            return instance;
        }

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation,
            Transform parent = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Acquire(key, position, rotation, parent));
        }

        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null) return;
            _active.Remove(instance);

            // Find which key this instance belongs to (from its name)
            string key = null;
            foreach (var kvp in _prefabs)
            {
                if (instance.name.Contains($"[pool:{kvp.Key}]"))
                {
                    key = kvp.Key;
                    break;
                }
            }

            if (key != null && _pools.TryGetValue(key, out var pool) && pool.Count < _maxPoolSize)
            {
                instance.SetActive(false);
                instance.transform.SetParent(_poolRoot, false);
                pool.Enqueue(instance);
            }
            else
            {
                SafeDestroy(instance);
            }
        }

        public void Release(string key)
        {
            if (_pools.TryGetValue(key, out var pool))
            {
                while (pool.Count > 0)
                {
                    var instance = pool.Dequeue();
                    if (instance != null) SafeDestroy(instance);
                }
                _pools.Remove(key);
            }

            _warmCounts.Remove(key);
        }

        /// <summary>Destroys all pooled instances and clears all state.</summary>
        public void Dispose()
        {
            foreach (var pool in _pools.Values)
            {
                while (pool.Count > 0)
                {
                    var instance = pool.Dequeue();
                    if (instance != null) SafeDestroy(instance);
                }
            }
            _pools.Clear();
            _warmCounts.Clear();
            _active.Clear();
            _prefabs.Clear();

            if (_poolRoot != null && _poolRoot.gameObject != null)
                SafeDestroy(_poolRoot.gameObject);
        }

        /// <summary>Number of pooled (inactive) instances for a key.</summary>
        public int GetPooledCount(string key) =>
            _pools.TryGetValue(key, out var pool) ? pool.Count : 0;

        /// <summary>Number of active (acquired) instances for a key.</summary>
        public int GetActiveCount(string key)
        {
            int count = 0;
            string tag = $"[pool:{key}]";
            foreach (var go in _active)
                if (go != null && go.name.Contains(tag)) count++;
            return count;
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
