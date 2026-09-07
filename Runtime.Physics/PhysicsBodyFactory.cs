using Cuvara.DOTS.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Adds Unity.Physics body components to entities, validated, with the pieces Unity.Physics
    /// needs and does not add for you.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every body gets <c>PhysicsWorldIndex</c>.</b> <c>BuildPhysicsWorld</c> selects bodies by
    /// that shared component; an entity without it is invisible to the simulation, collides with
    /// nothing and raises no events, with no error. Earlier versions of this factory omitted it.
    /// </para>
    /// <para>
    /// <b>Dynamic and kinematic bodies get <see cref="PhysicsDrivenMovement"/>.</b> Unity.Physics
    /// integrates their <c>LocalTransform</c>; the package's direct movers skip them and
    /// <c>PhysicsMovementBridge</c> requires the tag. Static bodies are not moved by anyone and get
    /// no tag.
    /// </para>
    /// <para>
    /// <b>Collider ownership</b> is the caller's choice. The overloads taking a
    /// <see cref="ColliderLibrary"/> share one blob per description and the library frees it; the
    /// overloads taking an explicit <c>BlobAssetReference&lt;Collider&gt;</c> use what the caller
    /// made and the caller disposes it. There is no overload that quietly allocates a blob nobody
    /// owns — that was the previous behaviour and it leaked one collider per body.
    /// </para>
    /// <para>
    /// Client-side only. A body created here collides on this client for feedback and presentation;
    /// it is not the server's collision and must not gate anything the server decides.
    /// </para>
    /// </remarks>
    public static class PhysicsBodyFactory
    {
        // ---- Library-owned colliders --------------------------------------------------------------

        /// <summary>Dynamic body (moves, collides, responds to forces) with a shared collider from <paramref name="library"/>.</summary>
        public static void AddDynamicBody(EntityManager em, Entity entity, ColliderLibrary library, ColliderShape shape, float3 size, float mass = 1f, CollisionFilter? filter = null, Material? material = null)
        {
            if (library == null) throw new System.ArgumentNullException(nameof(library));
            PhysicsBodyValidation.ValidateMass(mass);
            AddDynamicBody(em, entity, library.Acquire(shape, size, filter, material), mass);
        }

        /// <summary>Static body (collides, never moves) with a shared collider from <paramref name="library"/>.</summary>
        public static void AddStaticBody(EntityManager em, Entity entity, ColliderLibrary library, ColliderShape shape, float3 size, CollisionFilter? filter = null, Material? material = null)
        {
            if (library == null) throw new System.ArgumentNullException(nameof(library));
            AddStaticBody(em, entity, library.Acquire(shape, size, filter, material));
        }

        /// <summary>Kinematic body (moved by code through velocity, collides, ignores forces) with a shared collider from <paramref name="library"/>.</summary>
        public static void AddKinematicBody(EntityManager em, Entity entity, ColliderLibrary library, ColliderShape shape, float3 size, CollisionFilter? filter = null, Material? material = null)
        {
            if (library == null) throw new System.ArgumentNullException(nameof(library));
            AddKinematicBody(em, entity, library.Acquire(shape, size, filter, material));
        }

        // ---- Caller-owned colliders ---------------------------------------------------------------

        /// <summary>Dynamic body using a collider the caller created and will dispose.</summary>
        public static void AddDynamicBody(EntityManager em, Entity entity, BlobAssetReference<Collider> collider, float mass = 1f)
        {
            RequireCollider(collider);
            PhysicsBodyValidation.ValidateMass(mass);

            em.AddComponentData(entity, new PhysicsCollider { Value = collider });
            em.AddComponentData(entity, new PhysicsVelocity { Linear = float3.zero, Angular = float3.zero });
            em.AddComponentData(entity, PhysicsMass.CreateDynamic(collider.Value.MassProperties, mass));
            MarkPhysicsDriven(em, entity);
            EnsureWorldMembership(em, entity);
        }

        /// <summary>Static body using a collider the caller created and will dispose.</summary>
        public static void AddStaticBody(EntityManager em, Entity entity, BlobAssetReference<Collider> collider)
        {
            RequireCollider(collider);

            em.AddComponentData(entity, new PhysicsCollider { Value = collider });
            EnsureWorldMembership(em, entity);
        }

        /// <summary>Kinematic body using a collider the caller created and will dispose.</summary>
        public static void AddKinematicBody(EntityManager em, Entity entity, BlobAssetReference<Collider> collider)
        {
            RequireCollider(collider);

            em.AddComponentData(entity, new PhysicsCollider { Value = collider });
            em.AddComponentData(entity, new PhysicsVelocity());
            em.AddComponentData(entity, PhysicsMass.CreateKinematic(collider.Value.MassProperties));
            MarkPhysicsDriven(em, entity);
            EnsureWorldMembership(em, entity);
        }

        /// <summary>
        /// Creates a collider blob the <b>caller owns</b>. Validates shape and filter. See
        /// <see cref="PhysicsBodyValidation.ValidateShape"/> for what <paramref name="size"/> means per shape.
        /// </summary>
        public static BlobAssetReference<Collider> CreateCollider(ColliderShape shape, float3 size, CollisionFilter? filter = null, Material? material = null)
        {
            var f = filter ?? CollisionFilter.Default;
            var m = material ?? Material.Default;
            PhysicsBodyValidation.ValidateShape(shape, size);
            PhysicsBodyValidation.ValidateFilter(f);

            switch (shape)
            {
                case ColliderShape.Sphere:
                    return SphereCollider.Create(new SphereGeometry { Center = float3.zero, Radius = size.x }, f, m);
                case ColliderShape.Box:
                    return BoxCollider.Create(new BoxGeometry { Center = float3.zero, Orientation = quaternion.identity, Size = size, BevelRadius = math.min(0.05f, math.cmin(size) * 0.25f) }, f, m);
                case ColliderShape.Capsule:
                {
                    var half = size.y * 0.5f - size.x;
                    return CapsuleCollider.Create(new CapsuleGeometry { Vertex0 = new float3(0f, -half, 0f), Vertex1 = new float3(0f, half, 0f), Radius = size.x }, f, m);
                }
                case ColliderShape.Cylinder:
                    return CylinderCollider.Create(new CylinderGeometry { Center = float3.zero, Orientation = quaternion.identity, Height = size.y, Radius = size.x, BevelRadius = math.min(0.05f, size.x * 0.25f), SideCount = 12 }, f, m);
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(shape), shape, "[Cuvara.DOTS] Unknown collider shape.");
            }
        }

        /// <summary>A material that raises trigger events instead of colliding — for zones, pickups, AOI volumes.</summary>
        public static Material TriggerMaterial()
        {
            var material = Material.Default;
            material.CollisionResponse = CollisionResponsePolicy.RaiseTriggerEvents;
            return material;
        }

        /// <summary>A material that collides normally and raises collision events.</summary>
        public static Material CollisionEventMaterial()
        {
            var material = Material.Default;
            material.CollisionResponse = CollisionResponsePolicy.CollideRaiseCollisionEvents;
            return material;
        }

        private static void RequireCollider(BlobAssetReference<Collider> collider)
        {
            if (!collider.IsCreated)
                throw new System.ArgumentException("[Cuvara.DOTS] The collider blob is not created; make one with CreateCollider or a ColliderLibrary.", nameof(collider));
        }

        private static void MarkPhysicsDriven(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<PhysicsDrivenMovement>(entity)) em.AddComponent<PhysicsDrivenMovement>(entity);
        }

        private static void EnsureWorldMembership(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<LocalTransform>(entity)) em.AddComponentData(entity, LocalTransform.Identity);
            if (!em.HasComponent<LocalToWorld>(entity)) em.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            if (!em.HasComponent<PhysicsWorldIndex>(entity)) em.AddSharedComponent(entity, new PhysicsWorldIndex());
        }
    }
}
