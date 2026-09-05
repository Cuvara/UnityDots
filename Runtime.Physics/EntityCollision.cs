using Unity.Mathematics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>Published when two physics entities collide.</summary>
    public readonly struct EntityCollision
    {
        public readonly int EntityIndexA;
        public readonly int EntityIndexB;
        public readonly float3 Normal;
        public readonly float3 Position;
        public readonly float Impulse;

        public EntityCollision(int a, int b, float3 normal, float3 position, float impulse)
        { EntityIndexA = a; EntityIndexB = b; Normal = normal; Position = position; Impulse = impulse; }
    }
}
