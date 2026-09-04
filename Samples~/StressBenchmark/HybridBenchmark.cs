namespace Cuvara.DOTS.Samples.StressBenchmark
{
    using Cuvara.DOTS.Views;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEngine;

    /// <summary>
    /// Hybrid stress benchmark — each entity gets a pooled GameObject view via
    /// <see cref="EntityViewRequest"/>. Measures simulation + view spawn/sync cost.
    /// </summary>
    /// <remarks>
    /// <para>At high entity counts (100K+), the view layer dominates: each view is a
    /// Transform + MeshRenderer. This is the realistic ceiling for rendered entities.</para>
    /// <para>A cap is enforced: beyond <see cref="MaxViewEntities"/>, extra entities are
    /// simulation-only. The view layer would OOM or freeze the main thread long before 1M
    /// GameObjects; measuring that crash is not the point.</para>
    /// </remarks>
    [AddComponentMenu("Cuvara/DOTS/Stress Benchmark (Hybrid)")]
    public sealed class HybridBenchmark : StressBenchmarkBase
    {
        [Header("Hybrid")]
        [Tooltip("Maximum entities that get a GameObject view. Beyond this, entities are simulation-only.")]
        [SerializeField] private int maxViewEntities = 50_000;

        private PrimitiveViewProvider viewProvider;
        private EntityViewRegistry viewRegistry;

        protected override void Start()
        {
            Debug.Log($"[STRESS-BENCH] Mode: Hybrid (views capped at {maxViewEntities:N0}).");

            viewProvider = new PrimitiveViewProvider();
            viewRegistry = new EntityViewRegistry();
            DotsViewBootstrap.Install(world, viewRegistry);
            viewProvider.PrewarmSync("cube", maxViewEntities / 2);
            viewProvider.PrewarmSync("sphere", maxViewEntities / 2);

            base.Start();
        }

        protected override void OnTierSpawned(BenchmarkTier tier, NativeList<Entity> entities)
        {
            var em = world.EntityManager;
            var viewCount = math.min(entities.Length, maxViewEntities);

            for (int i = 0; i < viewCount; i++)
            {
                var key = (i & 1) == 0 ? "cube" : "sphere";
                em.AddComponentData(entities[i], new EntityViewRequest
                {
                    ViewKey = new FixedString64Bytes(key),
                });
            }

            var simOnly = entities.Length - viewCount;
            if (simOnly > 0)
            {
                Debug.Log($"[STRESS-BENCH] {viewCount:N0} entities with views, " +
                          $"{simOnly:N0} simulation-only (view cap).");
            }
        }

        protected override void OnDestroy()
        {
            viewProvider?.Clear();
            base.OnDestroy();
        }
    }

    /// <summary>
    /// Minimal view provider using Unity primitives. Pools cubes and spheres.
    /// </summary>
    internal sealed class PrimitiveViewProvider : IViewAssetProvider
    {
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Queue<GameObject>> pools
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Queue<GameObject>>();

        public void PrewarmSync(string key, int count)
        {
            if (!pools.TryGetValue(key, out var pool))
            {
                pool = new System.Collections.Generic.Queue<GameObject>();
                pools[key] = pool;
            }

            var type = key == "sphere" ? PrimitiveType.Sphere : PrimitiveType.Cube;
            for (int i = 0; i < count; i++)
            {
                var go = GameObject.CreatePrimitive(type);
                go.SetActive(false);
                go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                pool.Enqueue(go);
            }
        }

        public bool IsWarm(string key) => pools.ContainsKey(key) && pools[key].Count > 0;

        public GameObject Acquire(string key, float3 pos, quaternion rot, Transform parent)
        {
            if (!pools.TryGetValue(key, out var pool) || pool.Count == 0)
            {
                var type = key == "sphere" ? PrimitiveType.Sphere : PrimitiveType.Cube;
                var go = GameObject.CreatePrimitive(type);
                go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                go.transform.SetPositionAndRotation(pos, rot);
                if (parent != null) go.transform.SetParent(parent, true);
                go.SetActive(true);
                return go;
            }

            var instance = pool.Dequeue();
            instance.transform.SetPositionAndRotation(pos, rot);
            if (parent != null) instance.transform.SetParent(parent, true);
            instance.SetActive(true);
            return instance;
        }

        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null) return;
            instance.SetActive(false);
        }

        public void Clear()
        {
            foreach (var pool in pools.Values)
            {
                while (pool.Count > 0)
                {
                    var go = pool.Dequeue();
                    if (go != null) Object.Destroy(go);
                }
            }
            pools.Clear();
        }
    }
}
