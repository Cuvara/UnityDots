using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Provisioning;
using UnityEngine;

namespace Cuvara.DOTS.Samples.ProductionHardening
{
    /// <summary>
    /// Serialized description of one view key: what to instantiate for it, and what colour to tint it.
    /// </summary>
    [Serializable]
    public sealed class PrimitiveViewDefinition
    {
        [Tooltip("Pool/asset key. This is the string an entity puts in its EntityViewRequest.")]
        public string Key = "cube";

        [Tooltip("Optional prefab. When null, a Unity primitive of the type below is created instead.")]
        public GameObject Prefab;

        [Tooltip("Primitive spawned when Prefab is null.")]
        public PrimitiveType Primitive = PrimitiveType.Cube;

        public Color Color = Color.white;
    }

    /// <summary>
    /// A complete <see cref="IViewAssetProvider"/> that needs nothing but the four pinned Unity
    /// dependencies — no Addressables, no GameFoundation/UniT, no VContainer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists to prove the package's standalone claim, not to be copied into a game. A real
    /// project should adapt its existing loader and pool behind this interface (that is what
    /// <c>Cuvara.DOTS.GameFoundation</c> does) rather than run a second pool like this one — two
    /// owners over the same prefabs fight over recycling, which is exactly what
    /// <see cref="IViewAssetProvider"/>'s own docs warn about.
    /// </para>
    /// <para>
    /// Every operation is synchronous; the <see cref="Task"/>-returning members return already
    /// completed tasks. There is no loading to wait for when the "asset" is
    /// <see cref="GameObject.CreatePrimitive"/>. That is a deliberate simplification and it hides
    /// one real behaviour: with a genuine async loader, <see cref="PrewarmAsync"/> spans frames and
    /// entities requesting a cold key stay invisible until it lands.
    /// </para>
    /// <para>Main thread only, like the pool it stands in for.</para>
    /// </remarks>
    public sealed class PrimitiveViewAssetProvider : IViewAssetProvider
    {
        private readonly Dictionary<string, PrimitiveViewDefinition> _definitions =
            new Dictionary<string, PrimitiveViewDefinition>();

        /// <summary>key -> recycled instances waiting to be handed out again.</summary>
        private readonly Dictionary<string, Stack<GameObject>> _pooled = new Dictionary<string, Stack<GameObject>>();

        /// <summary>key -> instances currently out in the world.</summary>
        private readonly Dictionary<string, List<GameObject>> _live = new Dictionary<string, List<GameObject>>();

        /// <summary>instance -> the key it came from, so ReleaseInstance knows which pool to return it to.</summary>
        private readonly Dictionary<GameObject, string> _instanceKeys = new Dictionary<GameObject, string>();

        private readonly HashSet<string> _warm = new HashSet<string>();
        private readonly Transform _poolRoot;
        private readonly bool _verbose;

        /// <summary>How many GameObjects were actually instantiated. A pool that works keeps this low.</summary>
        public int InstantiateCount { get; private set; }

        public int AcquireCount { get; private set; }

        public int ReleaseInstanceCount { get; private set; }

        /// <summary>How many times the whole key was torn down — i.e. how often a refcount hit zero.</summary>
        public int ReleaseKeyCount { get; private set; }

        public PrimitiveViewAssetProvider(IEnumerable<PrimitiveViewDefinition> definitions, Transform poolRoot = null, bool verbose = true)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            foreach (var definition in definitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Key)) continue;
                _definitions[definition.Key] = definition;
            }

            _poolRoot = poolRoot;
            _verbose = verbose;
        }

        /// <summary>Instances sitting in the pool for a key. Diagnostics only.</summary>
        public int PooledCount(string key) => _pooled.TryGetValue(key, out var stack) ? stack.Count : 0;

        /// <summary>Instances currently out in the world for a key. Diagnostics only.</summary>
        public int LiveCount(string key) => _live.TryGetValue(key, out var list) ? list.Count : 0;

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(key) || !_definitions.ContainsKey(key))
            {
                // Loud rather than silent: an unknown key otherwise looks like "the view never spawned".
                Debug.LogWarning($"[HybridViews] PrewarmAsync('{key}') — no definition for this key, nothing warmed.");
                return Task.CompletedTask;
            }

            if (!_pooled.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>();
                _pooled[key] = stack;
            }

            var existing = stack.Count + LiveCount(key);
            for (var i = existing; i < count; i++) stack.Push(CreateInstance(key, active: false));

            _warm.Add(key);
            if (_verbose) Debug.Log($"[HybridViews] warm '{key}' -> pooled={stack.Count} (instantiated so far: {InstantiateCount})");
            return Task.CompletedTask;
        }

        public bool IsWarm(string key) => key != null && _warm.Contains(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (string.IsNullOrEmpty(key) || !_definitions.ContainsKey(key)) return null;

            GameObject instance = null;
            if (_pooled.TryGetValue(key, out var stack) && stack.Count > 0) instance = stack.Pop();

            // Fallback path: a cold key still gets served, it just hitches. The spawn system checks
            // IsWarm first precisely so this stays rare.
            if (instance == null) instance = CreateInstance(key, active: false);

            instance.transform.SetParent(parent, false);
            instance.transform.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);

            if (!_live.TryGetValue(key, out var live))
            {
                live = new List<GameObject>();
                _live[key] = live;
            }

            live.Add(instance);
            AcquireCount++;
            return instance;
        }

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Acquire(key, position, rotation, parent));

        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null) return;

            ReleaseInstanceCount++;

            if (!_instanceKeys.TryGetValue(instance, out var key))
            {
                // Not ours — destroy rather than pool it, so a foreign object cannot be handed out later.
                UnityEngine.Object.Destroy(instance);
                return;
            }

            if (_live.TryGetValue(key, out var live)) live.Remove(instance);

            instance.SetActive(false);
            instance.transform.SetParent(_poolRoot, false);

            if (!_pooled.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>();
                _pooled[key] = stack;
            }

            stack.Push(instance);
        }

        public void Release(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            ReleaseKeyCount++;
            var destroyed = 0;

            if (_live.TryGetValue(key, out var live))
            {
                // NOTE: these instances may still be addressed by an EntityViewLink. See the README —
                // releasing a chunk whose entities are still alive leaves dangling links.
                for (var i = 0; i < live.Count; i++) destroyed += DestroyInstance(live[i]);
                live.Clear();
                _live.Remove(key);
            }

            if (_pooled.TryGetValue(key, out var stack))
            {
                while (stack.Count > 0) destroyed += DestroyInstance(stack.Pop());
                _pooled.Remove(key);
            }

            _warm.Remove(key);
            Debug.Log($"[HybridViews] RELEASE '{key}' — refcount hit zero, {destroyed} instance(s) destroyed, key no longer warm.");
        }

        /// <summary>Destroys everything this provider ever made. For sample teardown.</summary>
        public void DestroyAll()
        {
            var keys = new List<string>(_warm);
            foreach (var key in keys) Release(key);

            foreach (var pair in _pooled)
            {
                while (pair.Value.Count > 0) DestroyInstance(pair.Value.Pop());
            }

            _pooled.Clear();
            _live.Clear();
            _instanceKeys.Clear();
            _warm.Clear();
        }

        private int DestroyInstance(GameObject instance)
        {
            if (instance == null) return 0;
            _instanceKeys.Remove(instance);
            UnityEngine.Object.Destroy(instance);
            return 1;
        }

        private GameObject CreateInstance(string key, bool active)
        {
            var definition = _definitions[key];

            GameObject instance;
            if (definition.Prefab != null)
            {
                instance = UnityEngine.Object.Instantiate(definition.Prefab);
            }
            else
            {
                instance = GameObject.CreatePrimitive(definition.Primitive);

                // Colliders would make a few hundred views a physics problem for no benefit; the
                // views are display-only, the entity is the authority.
                var collider = instance.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.Destroy(collider);

                var renderer = instance.GetComponent<Renderer>();
                if (renderer != null) renderer.material.color = definition.Color;
            }

            instance.name = $"{key}#{InstantiateCount}";
            instance.transform.SetParent(_poolRoot, false);
            instance.SetActive(active);

            _instanceKeys[instance] = key;
            InstantiateCount++;
            return instance;
        }
    }
}
