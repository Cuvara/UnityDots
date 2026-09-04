namespace Cuvara.DOTS.Samples.StressBenchmark
{
    using System;
    using System.Diagnostics;
    using Cuvara.DOTS.Simulation;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
    using UnityEngine;
    using Debug = UnityEngine.Debug;
#if DOTS_PHYSICS
    using Unity.Physics;
    using SphereCollider = Unity.Physics.SphereCollider;
#endif

    /// <summary>
    /// Shared base for both pure-DOTS and hybrid stress benchmarks. Handles the tier
    /// ramp, entity creation, physics setup, measurement, and reporting. Subclasses
    /// override <see cref="OnTierSpawned"/> to add view-layer work (hybrid) or not (pure).
    /// </summary>
    public abstract class StressBenchmarkBase : MonoBehaviour
    {
        [Header("Benchmark")]
        [SerializeField] internal bool useDefaultTiers = true;
        [SerializeField] internal float warmupPerTier = 3f;
        [SerializeField] internal float boundsExtent = 200f;
        [SerializeField] internal bool enablePhysics = true;
        [SerializeField] [Range(0f, 1f)] internal float physicsRatio = 0.3f;

        protected World world;
        protected NativeList<Entity> allEntities;

        private BenchmarkTier[] tiers;
        private int currentTier;
        private float warmupRemaining;
        private float tierElapsed;
        private bool measuring;
        private BenchmarkMetrics metrics;
        private Stopwatch stopwatch;
        private Unity.Mathematics.Random random;
        private int simCount;
        private int physCount;
        private bool finished;

        protected virtual void Awake()
        {
            tiers = useDefaultTiers ? BenchmarkTier.DefaultTiers : BenchmarkTier.QuickTiers;
            metrics = new BenchmarkMetrics();
            stopwatch = new Stopwatch();
            random = new Unity.Mathematics.Random(0xDEADBEEFu);
        }

        protected virtual void Start()
        {
            world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
            {
                Debug.LogError("[STRESS-BENCH] No default ECS world.");
                enabled = false;
                return;
            }

            DotsSimulationBootstrap.InstallSimulationSystems(world);
            allEntities = new NativeList<Entity>(Allocator.Persistent);

            Debug.Log($"[STRESS-BENCH] {GetType().Name} starting. " +
                      $"Tiers: {tiers.Length}. Physics: {enablePhysics} ({physicsRatio:P0}). " +
                      $"Warmup: {warmupPerTier}s.");

            SpawnTier(0);
        }

        protected virtual void OnDestroy()
        {
            if (allEntities.IsCreated)
            {
                if (world is { IsCreated: true })
                {
                    var em = world.EntityManager;
                    for (int i = 0; i < allEntities.Length; i++)
                        if (em.Exists(allEntities[i]))
                            em.DestroyEntity(allEntities[i]);
                }
                allEntities.Dispose();
            }
        }

        private void Update()
        {
            if (finished || currentTier >= tiers.Length) return;
            var dt = Time.unscaledDeltaTime;

            if (warmupRemaining > 0f)
            {
                warmupRemaining -= dt;
                if (warmupRemaining <= 0f)
                    StartMeasuring();
                return;
            }

            if (!measuring) return;

            tierElapsed += dt;
            metrics.RecordFrame(dt * 1000f);

            if (tierElapsed >= tiers[currentTier].Seconds)
            {
                FinishTier();
                currentTier++;
                if (currentTier < tiers.Length)
                    SpawnTier(currentTier);
                else
                    OnAllTiersComplete();
            }
        }

        private void SpawnTier(int index)
        {
            var tier = tiers[index];

            // Check available memory before attempting large tiers.
            var estimatedMb = (long)tier.EntityCount * 128 / (1024 * 1024);
            if (estimatedMb > SystemInfo.systemMemorySize * 0.6f)
            {
                Debug.LogWarning($"[STRESS-BENCH] Skipping tier {tier.Label} " +
                                 $"({tier.EntityCount:N0} entities, ~{estimatedMb:N0} MB estimated) " +
                                 $"— would exceed 60% of {SystemInfo.systemMemorySize} MB system RAM.");
                currentTier++;
                if (currentTier < tiers.Length)
                    SpawnTier(currentTier);
                else
                    OnAllTiersComplete();
                return;
            }

            DestroyAll();
            var sw = Stopwatch.StartNew();
            SpawnEntities(tier.EntityCount);
            sw.Stop();

            Debug.Log($"[STRESS-BENCH] Spawned tier {tier.Label}: " +
                      $"sim={simCount} phys={physCount} total={allEntities.Length} " +
                      $"spawn={sw.ElapsedMilliseconds}ms");

            OnTierSpawned(tier, allEntities);
            warmupRemaining = warmupPerTier;
            measuring = false;
        }

        private void SpawnEntities(int total)
        {
            var em = world.EntityManager;
            var extent = boundsExtent;

            var physTarget = enablePhysics ? (int)(total * physicsRatio) : 0;
            var simTarget = total - physTarget;

            // Simulation archetype.
            var simArchetype = em.CreateArchetype(
                ComponentType.ReadWrite<LocalTransform>(),
                ComponentType.ReadWrite<LocalToWorld>(),
                ComponentType.ReadWrite<MoveData>(),
                ComponentType.ReadWrite<SpinSpeed>(),
                ComponentType.ReadWrite<Health>(),
                ComponentType.ReadWrite<TimeToLive>());

            SpawnBatch(em, simArchetype, simTarget, extent, false);

#if DOTS_PHYSICS
            if (physTarget > 0)
            {
                var physArchetype = em.CreateArchetype(
                    ComponentType.ReadWrite<LocalTransform>(),
                    ComponentType.ReadWrite<LocalToWorld>(),
                    ComponentType.ReadWrite<MoveData>(),
                    ComponentType.ReadWrite<SpinSpeed>(),
                    ComponentType.ReadWrite<Health>(),
                    ComponentType.ReadWrite<TimeToLive>(),
                    ComponentType.ReadWrite<PhysicsVelocity>(),
                    ComponentType.ReadWrite<PhysicsMass>(),
                    ComponentType.ReadWrite<PhysicsCollider>());

                SpawnBatch(em, physArchetype, physTarget, extent, true);
            }
#endif

            simCount = simTarget;
            physCount = physTarget;
        }

        private void SpawnBatch(EntityManager em, EntityArchetype archetype,
            int count, float extent, bool isPhysics)
        {
            // Batch create in chunks to avoid single massive allocation.
            const int batchSize = 65536;
            var remaining = count;

            while (remaining > 0)
            {
                var n = math.min(remaining, batchSize);
                using (var batch = new NativeArray<Entity>(n, Allocator.Temp))
                {
                    em.CreateEntity(archetype, batch);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        InitEntity(em, batch[i], extent);
#if DOTS_PHYSICS
                        if (isPhysics)
                            InitPhysics(em, batch[i]);
#endif
                        allEntities.Add(batch[i]);
                    }
                }
                remaining -= n;
            }
        }

        private void InitEntity(EntityManager em, Entity e, float extent)
        {
            var pos = new float3(
                random.NextFloat(-extent, extent),
                random.NextFloat(0.5f, 10f),
                random.NextFloat(-extent, extent));

            em.SetComponentData(e, LocalTransform.FromPosition(pos));
            em.SetComponentData(e, new LocalToWorld
            {
                Value = float4x4.TRS(pos, quaternion.identity, new float3(1f)),
            });
            em.SetComponentData(e, new MoveData
            {
                Velocity = random.NextFloat3Direction() * random.NextFloat(2f, 8f),
                BoundsMin = new float3(-extent, 0.5f, -extent),
                BoundsMax = new float3(extent, 10f, extent),
            });
            em.SetComponentData(e, new SpinSpeed
            {
                RadiansPerSecond = random.NextFloat(0.5f, 4f),
            });
            em.SetComponentData(e, new Health { Current = 100, Max = 100 });
            em.SetComponentData(e, new TimeToLive { Remaining = 9999f });
        }

#if DOTS_PHYSICS
        private void InitPhysics(EntityManager em, Entity e)
        {
            var collider = SphereCollider.Create(
                new SphereGeometry { Center = float3.zero, Radius = 0.5f },
                CollisionFilter.Default);

            em.SetComponentData(e, new PhysicsVelocity
            {
                Linear = random.NextFloat3Direction() * random.NextFloat(1f, 5f),
                Angular = random.NextFloat3Direction() * random.NextFloat(0.5f, 2f),
            });
            em.SetComponentData(e, PhysicsMass.CreateDynamic(MassProperties.UnitSphere, 1f));
            em.SetComponentData(e, new PhysicsCollider { Value = collider });
        }
#endif

        private void DestroyAll()
        {
            if (!allEntities.IsCreated || world is not { IsCreated: true }) return;
            var em = world.EntityManager;
            for (int i = 0; i < allEntities.Length; i++)
                if (em.Exists(allEntities[i]))
                    em.DestroyEntity(allEntities[i]);
            allEntities.Clear();
            simCount = 0;
            physCount = 0;
        }

        private void StartMeasuring()
        {
            measuring = true;
            tierElapsed = 0f;
            metrics.Reset();
            stopwatch.Restart();
        }

        private void FinishTier()
        {
            stopwatch.Stop();
            var tier = tiers[currentTier];
            Debug.Log(metrics.Summarize(tier.Label, tier.EntityCount,
                stopwatch.Elapsed.TotalSeconds, simCount, physCount));
        }

        private void OnAllTiersComplete()
        {
            finished = true;
            Debug.Log("[STRESS-BENCH] All tiers complete.");
#if !UNITY_EDITOR
            Application.Quit();
#endif
        }

        /// <summary>Called after entities are spawned for a tier. Override to add views.</summary>
        protected virtual void OnTierSpawned(BenchmarkTier tier, NativeList<Entity> entities) { }
    }
}
