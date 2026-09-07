using Cuvara.DOTS.Messaging;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Reads Unity.Physics' collision and trigger event streams once per physics step, resolves
    /// them through <see cref="PhysicsContactTracker"/> into enter/stay/exit per entity pair, and
    /// publishes the result into <see cref="PhysicsEventBuffer"/> and the optional publishers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> Inside <see cref="PhysicsSystemGroup"/>, after <see cref="PhysicsSimulationGroup"/>:
    /// the event streams belong to the step that just ran and are valid until the next
    /// <c>BuildPhysicsWorld</c>. This is the same slot Unity's own samples use for
    /// <c>ICollisionEventsJob</c>. Because <see cref="PhysicsSystemGroup"/> is in
    /// <c>FixedStepSimulationSystemGroup</c>, the buffer is rebuilt at the fixed rate, not the
    /// render rate — a render frame with no physics step sees the previous step's events.
    /// </para>
    /// <para>
    /// <b>Not a second engine.</b> The jobs are Unity's <see cref="ICollisionEventsJob"/> and
    /// <see cref="ITriggerEventsJob"/>; contact position and impulse come from
    /// <c>CollisionEvent.CalculateDetails</c>. Nothing here decides whether anything collided; it
    /// reports what Unity.Physics decided, on this client, for presentation and local feedback.
    /// The server remains authoritative for anything that matters.
    /// </para>
    /// <para>
    /// The two native lists are allocated in <c>OnCreate</c> and disposed in <c>OnDestroy</c> after
    /// <c>CompleteDependency</c>, so uninstalling (which destroys the system) or disposing the world
    /// releases them.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateAfter(typeof(PhysicsSimulationGroup))]
    public partial class PhysicsEventCollectorSystem : SystemBase
    {
        internal struct RawCollision
        {
            public Entity A;
            public Entity B;
            public float3 Normal;
            public float3 Position;
            public float Impulse;
        }

        internal struct RawTrigger
        {
            public Entity A;
            public Entity B;
        }

        private struct CollectCollisionsJob : ICollisionEventsJob
        {
            [ReadOnly] public PhysicsWorld PhysicsWorld;
            public NativeList<RawCollision> Output;

            public void Execute(CollisionEvent collisionEvent)
            {
                var details = collisionEvent.CalculateDetails(ref PhysicsWorld);
                var position = details.EstimatedContactPointPositions.Length > 0
                    ? details.AverageContactPointPosition
                    : float3.zero;

                Output.Add(new RawCollision
                {
                    A = collisionEvent.EntityA,
                    B = collisionEvent.EntityB,
                    // Unity's Normal points from B toward A; the tracker's convention is A toward B.
                    Normal = -collisionEvent.Normal,
                    Position = position,
                    Impulse = details.EstimatedImpulse,
                });
            }
        }

        private struct CollectTriggersJob : ITriggerEventsJob
        {
            public NativeList<RawTrigger> Output;

            public void Execute(TriggerEvent triggerEvent)
            {
                Output.Add(new RawTrigger { A = triggerEvent.EntityA, B = triggerEvent.EntityB });
            }
        }

        private NativeList<RawCollision> _collisions;
        private NativeList<RawTrigger> _triggers;
        private readonly PhysicsContactTracker _tracker = new PhysicsContactTracker();

        /// <summary>The state machine behind the buffer. Exposed for diagnostics and tests.</summary>
        public PhysicsContactTracker Tracker => _tracker;

        /// <summary>Optional publishers, set by <see cref="PhysicsEventsBootstrap.Install"/>. Never null once installed.</summary>
        internal IDotsPublisher<EntityCollision> CollisionPublisher = NullDotsPublisher<EntityCollision>.Instance;

        internal IDotsPublisher<EntityTriggerEvent> TriggerPublisher = NullDotsPublisher<EntityTriggerEvent>.Instance;

        protected override void OnCreate()
        {
            _collisions = new NativeList<RawCollision>(64, Allocator.Persistent);
            _triggers = new NativeList<RawTrigger>(64, Allocator.Persistent);

            RequireForUpdate<SimulationSingleton>();
            RequireForUpdate<PhysicsWorldSingleton>();
            RequireForUpdate<PhysicsEventBuffer>();
        }

        protected override void OnDestroy()
        {
            // The event jobs may still be in flight if the world is torn down mid-frame; complete
            // before freeing what they write into.
            CompleteDependency();
            if (_collisions.IsCreated) _collisions.Dispose();
            if (_triggers.IsCreated) _triggers.Dispose();
        }

        protected override void OnUpdate()
        {
            var simulation = SystemAPI.GetSingleton<SimulationSingleton>();
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            var buffer = SystemAPI.ManagedAPI.GetSingleton<PhysicsEventBuffer>();

            _collisions.Clear();
            _triggers.Clear();

            Dependency = new CollectCollisionsJob { PhysicsWorld = physicsWorld, Output = _collisions }.Schedule(simulation, Dependency);
            Dependency = new CollectTriggersJob { Output = _triggers }.Schedule(simulation, Dependency);
            Dependency.Complete();

            _tracker.BeginStep();
            for (var i = 0; i < _collisions.Length; i++)
            {
                var raw = _collisions[i];
                _tracker.ReportCollision(raw.A, raw.B, raw.Normal, raw.Position, raw.Impulse);
            }

            for (var i = 0; i < _triggers.Length; i++) _tracker.ReportTrigger(_triggers[i].A, _triggers[i].B);

            buffer.Collisions.Clear();
            buffer.Triggers.Clear();
            var entityManager = EntityManager;
            _tracker.EndStep(buffer.Collisions, buffer.Triggers, entityManager.Exists);
            buffer.Step++;

            for (var i = 0; i < buffer.Collisions.Count; i++) CollisionPublisher.Publish(buffer.Collisions[i]);
            for (var i = 0; i < buffer.Triggers.Count; i++) TriggerPublisher.Publish(buffer.Triggers[i]);
        }
    }
}
