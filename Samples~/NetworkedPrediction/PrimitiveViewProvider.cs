using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Provisioning;
using UnityEngine;

namespace Cuvara.DOTS.Samples.NetworkedPrediction
{
    /// <summary>
    /// An <see cref="IViewAssetProvider"/> over Unity primitives, so the sample needs no prefabs,
    /// no Addressables and no art pipeline.
    /// </summary>
    /// <remarks>
    /// A real provider adapts an existing pool and loader — GameFoundation's
    /// <c>IAssetsManager</c> + <c>IObjectPoolManager</c> in Cuvara projects. This one exists so the
    /// sample can be dropped into an empty scene and pressed play, and it pools for real (a freed
    /// instance is reused) so the recycle path the view layer relies on is actually exercised rather
    /// than hidden behind Instantiate/Destroy.
    /// </remarks>
    internal sealed class PrimitiveViewProvider : IViewAssetProvider
    {
        private readonly Dictionary<string, Stack<GameObject>> _pools = new Dictionary<string, Stack<GameObject>>();
        private readonly Dictionary<GameObject, string> _keys = new Dictionary<GameObject, string>();
        private readonly Dictionary<string, (PrimitiveType Shape, Color Colour, float Scale)> _kinds;
        private readonly Transform _root;

        public PrimitiveViewProvider(Transform root)
        {
            _root = root;
            _kinds = new Dictionary<string, (PrimitiveType Shape, Color Colour, float Scale)>
            {
                ["player-local"] = (PrimitiveType.Capsule, new Color(0.2f, 0.8f, 1f), 1.2f),
                ["player-remote"] = (PrimitiveType.Capsule, new Color(0.9f, 0.9f, 0.9f), 1f),
                ["mob"] = (PrimitiveType.Sphere, new Color(0.9f, 0.15f, 0.1f), 0.8f),
            };
        }

        /// <summary>Instances currently handed out, by key. Drawn in the overlay.</summary>
        public readonly Dictionary<string, int> Live = new Dictionary<string, int>();

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            var pool = Pool(key);
            while (pool.Count < count) pool.Push(Create(key));
            return Task.CompletedTask;
        }

        public bool IsWarm(string key) => _kinds.ContainsKey(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var pool = Pool(key);
            var instance = pool.Count > 0 ? pool.Pop() : Create(key);
            instance.transform.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);
            Live[key] = Live.TryGetValue(key, out var n) ? n + 1 : 1;
            return instance;
        }

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Acquire(key, position, rotation, parent));

        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null || !_keys.TryGetValue(instance, out var key)) return;

            instance.SetActive(false);
            Pool(key).Push(instance);
            if (Live.TryGetValue(key, out var n)) Live[key] = Mathf.Max(0, n - 1);
        }

        public void Release(string key)
        {
            if (!_pools.TryGetValue(key, out var pool)) return;
            while (pool.Count > 0)
            {
                var instance = pool.Pop();
                _keys.Remove(instance);
                Object.Destroy(instance);
            }
        }

        private Stack<GameObject> Pool(string key)
        {
            if (!_pools.TryGetValue(key, out var pool)) _pools[key] = pool = new Stack<GameObject>();
            return pool;
        }

        private GameObject Create(string key)
        {
            // The fallback is spelled with element names too: an unnamed tuple as the other ternary
            // operand drops the names from the common type, and the result only fails at the use
            // site with "does not contain a definition for 'Shape'".
            var kind = _kinds.TryGetValue(key, out var k)
                ? k
                : (Shape: PrimitiveType.Cube, Colour: Color.magenta, Scale: 1f);
            var instance = GameObject.CreatePrimitive(kind.Shape);
            instance.name = key;
            instance.transform.SetParent(_root, false);
            instance.transform.localScale = Vector3.one * kind.Scale;

            // Collider removed: nothing in this sample uses physics, and a hundred pooled colliders
            // is a hundred things the physics system walks for no reason.
            var collider = instance.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            var renderer = instance.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = kind.Colour;

            instance.SetActive(false);
            _keys[instance] = key;
            return instance;
        }
    }
}
