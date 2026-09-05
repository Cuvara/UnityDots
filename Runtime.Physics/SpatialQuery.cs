using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// ECS-friendly spatial query utilities using Unity.Physics <see cref="CollisionWorld"/>.
    /// No allocations — results are written to caller-provided buffers.
    /// </summary>
    public static class SpatialQuery
    {
        /// <summary>Finds all physics bodies within radius of center.</summary>
        public static int OverlapSphere(in CollisionWorld world, float3 center, float radius,
            ref NativeList<DistanceHit> hits, CollisionFilter filter = default)
        {
            if (filter.Equals(default(CollisionFilter))) filter = CollisionFilter.Default;
            int before = hits.Length;
            world.CalculateDistance(new PointDistanceInput { Position = center, MaxDistance = radius, Filter = filter }, ref hits);
            return hits.Length - before;
        }

        /// <summary>Casts a ray. Returns true if something was hit.</summary>
        public static bool Raycast(in CollisionWorld world, float3 from, float3 to,
            out RaycastHit hit, CollisionFilter filter = default)
        {
            if (filter.Equals(default(CollisionFilter))) filter = CollisionFilter.Default;
            return world.CastRay(new RaycastInput { Start = from, End = to, Filter = filter }, out hit);
        }

        /// <summary>Casts a ray and returns all hits.</summary>
        public static int RaycastAll(in CollisionWorld world, float3 from, float3 to,
            ref NativeList<RaycastHit> hits, CollisionFilter filter = default)
        {
            if (filter.Equals(default(CollisionFilter))) filter = CollisionFilter.Default;
            int before = hits.Length;
            world.CastRay(new RaycastInput { Start = from, End = to, Filter = filter }, ref hits);
            return hits.Length - before;
        }

        /// <summary>Finds the closest physics body to origin within maxDistance.</summary>
        public static bool ClosestBody(in CollisionWorld world, float3 origin, float maxDistance,
            out DistanceHit closestHit, CollisionFilter filter = default)
        {
            if (filter.Equals(default(CollisionFilter))) filter = CollisionFilter.Default;
            return world.CalculateDistance(new PointDistanceInput { Position = origin, MaxDistance = maxDistance, Filter = filter }, out closestHit);
        }
    }
}
