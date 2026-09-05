using System.Collections.Generic;
using System.Threading.Tasks;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Samples.ProductionHardening
{
    /// <summary>
    /// Demonstrates three production-hardening features:
    /// <list type="number">
    /// <item>Robust despawn — externally destroyed GameObjects are detected and cleaned up</item>
    /// <item>Chunk provisioner metrics — ChunkState transitions and counts</item>
    /// <item>View overlay anchors — world-space anchor points for health bars</item>
    /// </list>
    /// Drop on a GameObject, press Play, and read the Console.
    /// </summary>
    public sealed class ProductionHardeningSample : MonoBehaviour
    {
        [Header("Timeline")]
        [SerializeField] private float _stepSeconds = 3f;

        private World _world;
        private EntityViewRegistry _registry;
        private PrimitiveViewAssetProvider _provider;
        private ChunkViewProvisioner _provisioner;
        private EntityViewCascade _cascade;
        private Transform _viewRoot;

        private readonly List<Entity> _entities = new List<Entity>();
        private int _step;
        private float _nextStepTime;

        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null)
            {
                Debug.LogError("[ProductionHardening] No default world.");
                enabled = false;
                return;
            }

            var root = new GameObject("ViewRoot");
            root.transform.SetParent(transform);
            _viewRoot = root.transform;

            _provider = new PrimitiveViewAssetProvider(
                new PrimitiveViewDefinition
                {
                    Key = "cube",
                    Primitive = PrimitiveType.Cube,
                    Color = new Color(0.85f, 0.35f, 0.25f),
                },
                new PrimitiveViewDefinition
                {
                    Key = "sphere",
                    Primitive = PrimitiveType.Sphere,
                    Color = new Color(0.25f, 0.6f, 0.9f),
                });

            _registry = new EntityViewRegistry(_provider, _viewRoot);
            _cascade = new EntityViewCascade(_registry);
            _provisioner = new ChunkViewProvisioner(_provider, _cascade);

            // Subscribe to chunk state changes
            _provisioner.OnChunkStateChanged += (chunkId, state) =>
                Debug.Log($"[ProductionHardening] Chunk '{chunkId}' → {state}");

            DotsViewBootstrap.Install(_world, _registry);

            _nextStepTime = Time.time + 1f;
            Debug.Log("[ProductionHardening] Sample started. Watch the Console for the scripted timeline.");
        }

        private void Update()
        {
            if (Time.time < _nextStepTime) return;
            _nextStepTime = Time.time + _stepSeconds;

            switch (_step)
            {
                case 0: Step0_WarmAndSpawn(); break;
                case 1: Step1_ShowMetrics(); break;
                case 2: Step2_ExternalDestroy(); break;
                case 3: Step3_VerifySweep(); break;
                case 4: Step4_SpawnWithOverlayAnchors(); break;
                case 5: Step5_ReadOverlayBuffer(); break;
                case 6: Step6_Cleanup(); break;
            }

            _step++;
        }

        private void Step0_WarmAndSpawn()
        {
            Debug.Log("═══ Step 0: Warm chunk + spawn 4 entities ═══");

            _provisioner.PrewarmChunkAsync("demo-chunk", new[] { "cube", "sphere" }, 4);

            var em = _world.EntityManager;
            for (int i = 0; i < 4; i++)
            {
                var entity = em.CreateEntity();
                var key = i < 2 ? "cube" : "sphere";
                em.AddComponentData(entity, new EntityViewRequest { ViewKey = new Unity.Collections.FixedString64Bytes(key) });
                em.AddComponentData(entity, new LocalTransform { Position = new float3(i * 2f, 0, 0), Rotation = quaternion.identity, Scale = 1f });
                em.AddComponentData(entity, LocalToWorld.FromPosition(new float3(i * 2f, 0, 0)));
                _entities.Add(entity);
            }

            Debug.Log($"  Spawned 4 entities. Registry views: {_registry.TotalViews}");
        }

        private void Step1_ShowMetrics()
        {
            Debug.Log("═══ Step 1: Chunk provisioner metrics ═══");
            Debug.Log($"  Warm chunks: {_provisioner.WarmChunkCount}");
            Debug.Log($"  Pending chunks: {_provisioner.PendingChunkCount}");
            Debug.Log($"  Chunk states: {string.Join(", ", FormatChunkStates())}");
            Debug.Log($"  Registry — total views: {_registry.TotalViews}, keys: {_registry.TotalKeys}");

            foreach (var kvp in _registry.LiveCountsByKey)
                Debug.Log($"    key '{kvp.Key}': {kvp.Value} live views");
        }

        private void Step2_ExternalDestroy()
        {
            Debug.Log("═══ Step 2: Destroy 2 view GameObjects EXTERNALLY ═══");

            // Find and destroy the first 2 view GameObjects directly — bypassing the ECS despawn path.
            // This simulates a scene unload or manual Destroy().
            int destroyed = 0;
            foreach (var child in GetViewChildren())
            {
                if (destroyed >= 2) break;
                Debug.Log($"  Destroying '{child.name}' via Object.Destroy (external)");
                Destroy(child.gameObject);
                destroyed++;
            }

            Debug.Log($"  Registry still reports {_registry.TotalViews} views (stale!)");
            Debug.Log("  → SweepDestroyed will detect this next frame.");
        }

        private void Step3_VerifySweep()
        {
            Debug.Log("═══ Step 3: After SweepDestroyed ran ═══");
            Debug.Log($"  Registry views: {_registry.TotalViews} (should be 2, down from 4)");

            foreach (var kvp in _registry.LiveCountsByKey)
                Debug.Log($"    key '{kvp.Key}': {kvp.Value} live views");
        }

        private void Step4_SpawnWithOverlayAnchors()
        {
            Debug.Log("═══ Step 4: Spawn 3 entities WITH overlay anchors ═══");

            var em = _world.EntityManager;
            for (int i = 0; i < 3; i++)
            {
                var entity = em.CreateEntity();
                em.AddComponentData(entity, new EntityViewRequest { ViewKey = new Unity.Collections.FixedString64Bytes("cube") });
                var pos = new float3(i * 3f, 0, 3f);
                em.AddComponentData(entity, new LocalTransform { Position = pos, Rotation = quaternion.identity, Scale = 1f });
                em.AddComponentData(entity, LocalToWorld.FromPosition(pos));

                // Overlay anchor — "2 units above the entity"
                em.AddComponentData(entity, new ViewOverlayAnchor
                {
                    WorldOffset = new float3(0, 2f, 0),
                });

                _entities.Add(entity);
            }

            Debug.Log("  Added ViewOverlayAnchor (0, 2, 0) to each.");
        }

        private void Step5_ReadOverlayBuffer()
        {
            Debug.Log("═══ Step 5: Read ViewOverlayBuffer ═══");

            using var query = _world.EntityManager.CreateEntityQuery(typeof(ViewOverlayBuffer));
            if (query.IsEmpty)
            {
                Debug.Log("  No ViewOverlayBuffer singleton found (no anchored views this frame).");
                return;
            }

            var buffer = query.GetSingleton<ViewOverlayBuffer>();
            Debug.Log($"  Overlay entries: {buffer.Count}");

            for (int i = 0; i < buffer.Count; i++)
            {
                var entry = buffer.Entries[i];
                Debug.Log($"    ViewId={entry.ViewId} pos=({entry.WorldPosition.x:F1}, {entry.WorldPosition.y:F1}, {entry.WorldPosition.z:F1}) hp={entry.HealthFraction:F2}");
            }
        }

        private void Step6_Cleanup()
        {
            Debug.Log("═══ Step 6: Release chunk + cleanup ═══");

            var result = _provisioner.ReleaseChunk("demo-chunk");
            Debug.Log($"  Released: keys={result.KeysReleased}, cascaded={result.ViewsDespawned} views");
            Debug.Log($"  Chunk states: {string.Join(", ", FormatChunkStates())}");
            Debug.Log($"  Registry views: {_registry.TotalViews}");
            Debug.Log("[ProductionHardening] Sample complete.");
        }

        private void OnDestroy()
        {
            if (_world != null && _world.IsCreated)
            {
                var em = _world.EntityManager;
                foreach (var entity in _entities)
                {
                    if (em.Exists(entity)) em.DestroyEntity(entity);
                }

                DotsViewBootstrap.Uninstall(_world);
            }
        }

        private List<Transform> GetViewChildren()
        {
            var result = new List<Transform>();
            if (_viewRoot == null) return result;
            for (int i = 0; i < _viewRoot.childCount; i++)
                result.Add(_viewRoot.GetChild(i));
            return result;
        }

        private List<string> FormatChunkStates()
        {
            var result = new List<string>();
            foreach (var kvp in _provisioner.ChunkStates)
                result.Add($"{kvp.Key}={kvp.Value}");
            return result;
        }
    }
}
