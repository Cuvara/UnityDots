using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Cuvara.DOTS.Simulation;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Argument and state checks for the physics helpers. Every failure is an exception naming the
    /// value and what it must be — a zero-radius sphere or a NaN half-extent inside Unity.Physics
    /// is a broadphase assert or a silent non-collision, not a message.
    /// </summary>
    public static class PhysicsBodyValidation
    {
        /// <summary>
        /// Meaning of <c>size</c> per shape: Sphere = (radius, -, -); Box = full extents;
        /// Capsule = (radius, total height, -), height ≥ 2·radius; Cylinder = (radius, height, -).
        /// </summary>
        public static void ValidateShape(ColliderShape shape, float3 size)
        {
            if (!math.all(math.isfinite(size)))
                throw new ArgumentException($"[Cuvara.DOTS] {shape} collider size {size} must be finite.", nameof(size));

            switch (shape)
            {
                case ColliderShape.Sphere:
                    RequirePositive(size.x, "radius", shape);
                    break;
                case ColliderShape.Box:
                    RequirePositive(size.x, "size.x", shape);
                    RequirePositive(size.y, "size.y", shape);
                    RequirePositive(size.z, "size.z", shape);
                    break;
                case ColliderShape.Capsule:
                    RequirePositive(size.x, "radius", shape);
                    RequirePositive(size.y, "height", shape);
                    if (size.y < 2f * size.x)
                    {
                        throw new ArgumentException(
                            $"[Cuvara.DOTS] Capsule height {size.y} must be at least twice the radius {size.x}; the two end caps would overlap.", nameof(size));
                    }

                    break;
                case ColliderShape.Cylinder:
                    RequirePositive(size.x, "radius", shape);
                    RequirePositive(size.y, "height", shape);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, "[Cuvara.DOTS] Unknown collider shape.");
            }
        }

        /// <summary>Mass for a dynamic body: finite and &gt; 0.</summary>
        public static void ValidateMass(float mass)
        {
            if (!math.isfinite(mass) || mass <= 0f)
            {
                throw new ArgumentException(
                    $"[Cuvara.DOTS] Dynamic body mass {mass} must be finite and > 0. Use AddKinematicBody for an unmovable body that still collides.", nameof(mass));
            }
        }

        /// <summary>
        /// A filter that belongs to nothing or collides with nothing produces a body that never
        /// touches anything, which reads as physics being broken. <c>CollisionFilter.Zero</c> is
        /// legal in Unity.Physics but never what a body factory is asked for.
        /// </summary>
        public static void ValidateFilter(CollisionFilter filter)
        {
            if (filter.BelongsTo == 0u || filter.CollidesWith == 0u)
            {
                throw new ArgumentException(
                    $"[Cuvara.DOTS] Collision filter BelongsTo=0x{filter.BelongsTo:X} CollidesWith=0x{filter.CollidesWith:X} would collide with nothing. " +
                    "Use CollisionFilter.Default, or set both masks.", nameof(filter));
            }
        }

        /// <summary>
        /// The one-integrator rule (see <see cref="PhysicsDrivenMovement"/>): an entity with
        /// <c>PhysicsVelocity</c> must carry the tag so the package's direct movers skip it, and an
        /// entity with the tag must have a <c>PhysicsVelocity</c> for the bridge to write. Throws
        /// naming the entity otherwise.
        /// </summary>
        public static void AssertSingleIntegrator(EntityManager entityManager, Entity entity)
        {
            var hasVelocity = entityManager.HasComponent<PhysicsVelocity>(entity);
            var hasTag = entityManager.HasComponent<PhysicsDrivenMovement>(entity);
            if (hasVelocity == hasTag) return;

            throw new InvalidOperationException(hasVelocity
                ? $"[Cuvara.DOTS] {entity} has PhysicsVelocity but no PhysicsDrivenMovement tag: MoveBounce/MoveToward would integrate it as well as Unity.Physics. Create it through PhysicsBodyFactory or add the tag."
                : $"[Cuvara.DOTS] {entity} has PhysicsDrivenMovement but no PhysicsVelocity: nothing integrates it. Add a dynamic or kinematic body, or remove the tag.");
        }

        private static void RequirePositive(float value, string name, ColliderShape shape)
        {
            if (value > 0f) return;
            throw new ArgumentException($"[Cuvara.DOTS] {shape} collider {name} must be > 0, was {value}.", "size");
        }
    }
}
