using System;
using Unity.Entities;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// An unordered entity pair in canonical order: <see cref="A"/> has the lower index, then the
    /// lower version. Full <see cref="Entity"/> on both sides, so a recycled index is a different key.
    /// </summary>
    public readonly struct PhysicsPairKey : IEquatable<PhysicsPairKey>, IComparable<PhysicsPairKey>
    {
        public readonly Entity A;
        public readonly Entity B;

        /// <summary>True when the caller's (a, b) was swapped to reach canonical order — a normal must then be flipped.</summary>
        public readonly bool Swapped;

        public PhysicsPairKey(Entity a, Entity b)
        {
            if (Compare(a, b) <= 0)
            {
                A = a;
                B = b;
                Swapped = false;
            }
            else
            {
                A = b;
                B = a;
                Swapped = true;
            }
        }

        public bool Involves(Entity entity) => A == entity || B == entity;

        public static int Compare(Entity x, Entity y)
        {
            var byIndex = x.Index.CompareTo(y.Index);
            return byIndex != 0 ? byIndex : x.Version.CompareTo(y.Version);
        }

        public int CompareTo(PhysicsPairKey other)
        {
            var byA = Compare(A, other.A);
            return byA != 0 ? byA : Compare(B, other.B);
        }

        public bool Equals(PhysicsPairKey other) => A == other.A && B == other.B;

        public override bool Equals(object obj) => obj is PhysicsPairKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = A.Index;
                hash = hash * 397 ^ A.Version;
                hash = hash * 397 ^ B.Index;
                hash = hash * 397 ^ B.Version;
                return hash;
            }
        }

        public override string ToString() => $"{A}-{B}";
    }
}
