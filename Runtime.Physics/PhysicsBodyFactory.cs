using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Factory methods for adding Unity.Physics components to entities.
    /// </summary>
    public static class PhysicsBodyFactory
    {
        /// <summary>Adds a dynamic physics body (moves, collides, responds to forces).</summary>
        public static void AddDynamicBody(EntityManager em, Entity entity, ColliderShape shape, float3 size, float mass = 1f)
        {
            var collider = CreateCollider(shape, size);
            em.AddComponentData(entity, new PhysicsCollider { Value = collider });
            em.AddComponentData(entity, new PhysicsVelocity { Linear = float3.zero, Angular = float3.zero });
            em.AddComponentData(entity, PhysicsMass.CreateDynamic(collider.Value.MassProperties, mass));
            EnsureTransform(em, entity);
        }

        /// <summary>Adds a static physics body (collides, does not move).</summary>
        public static void AddStaticBody(EntityManager em, Entity entity, ColliderShape shape, float3 size)
        {
            em.AddComponentData(entity, new PhysicsCollider { Value = CreateCollider(shape, size) });
            EnsureTransform(em, entity);
        }

        /// <summary>Adds a kinematic physics body (moves via code, collides, ignores forces).</summary>
        public static void AddKinematicBody(EntityManager em, Entity entity, ColliderShape shape, float3 size)
        {
            var collider = CreateCollider(shape, size);
            em.AddComponentData(entity, new PhysicsCollider { Value = collider });
            em.AddComponentData(entity, new PhysicsVelocity());
            em.AddComponentData(entity, PhysicsMass.CreateKinematic(collider.Value.MassProperties));
            EnsureTransform(em, entity);
        }

        /// <summary>Creates a collider blob asset from a shape enum and size.</summary>
        public static BlobAssetReference<Collider> CreateCollider(ColliderShape shape, float3 size, CollisionFilter? filter = null)
        {
            var f = filter ?? CollisionFilter.Default;
            switch (shape)
            {
                case ColliderShape.Sphere:
                    return SphereCollider.Create(new SphereGeometry { Center = float3.zero, Radius = size.x }, f);
                case ColliderShape.Box:
                    return BoxCollider.Create(new BoxGeometry { Center = float3.zero, Orientation = quaternion.identity, Size = size, BevelRadius = 0.05f }, f);
                case ColliderShape.Capsule:
                    var h = size.y * 0.5f;
                    return CapsuleCollider.Create(new CapsuleGeometry { Vertex0 = new float3(0, -h + size.x, 0), Vertex1 = new float3(0, h - size.x, 0), Radius = size.x }, f);
                case ColliderShape.Cylinder:
                    return CylinderCollider.Create(new CylinderGeometry { Center = float3.zero, Orientation = quaternion.identity, Height = size.y, Radius = size.x, BevelRadius = 0.05f, SideCount = 12 }, f);
                default:
                    return SphereCollider.Create(new SphereGeometry { Center = float3.zero, Radius = size.x }, f);
            }
        }

        private static void EnsureTransform(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<LocalTransform>(entity)) em.AddComponentData(entity, LocalTransform.Identity);
            if (!em.HasComponent<LocalToWorld>(entity)) em.AddComponentData(entity, new LocalToWorld { Value = float4x4.identity });
        }
    }
}
