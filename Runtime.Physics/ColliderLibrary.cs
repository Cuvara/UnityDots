using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace Cuvara.DOTS.Physics
{
    /// <summary>
    /// Owns collider blobs so entities can share them and so teardown returns every byte. One blob
    /// per distinct (shape, size, filter, material); leases are counted; <see cref="Dispose"/> frees
    /// whatever is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an owner at all.</b> A <c>BlobAssetReference&lt;Collider&gt;</c> is a raw allocation.
    /// Unity.Physics does not free one it did not create — only baked, force-unique colliders get
    /// its <c>ColliderBlobCleanupSystem</c> — so a collider made at runtime and dropped with its
    /// entity is a leak, and a collider shared by copying the reference onto a hundred goblins is
    /// freed when the first goblin's owner disposes it, leaving ninety-nine dangling. This class
    /// makes the ownership explicit: the library holds the blob, entities hold a copy of the
    /// reference, and the blob outlives every lease until <see cref="Release"/> brings the count to
    /// zero or the library is disposed.
    /// </para>
    /// <para>
    /// <b>Scope.</b> One library per session (or per world), disposed with it — a root-scoped
    /// library shared across sessions is legal but then nothing returns its memory until the app
    /// exits. Never mix: a collider handed out by a library must not be disposed by the caller, and
    /// a caller-created collider (the <c>PhysicsBodyFactory</c> overloads without a library) is the
    /// caller's to dispose.
    /// </para>
    /// </remarks>
    public sealed class ColliderLibrary : IDisposable
    {
        /// <summary>What makes two colliders the same blob.</summary>
        public readonly struct Key : IEquatable<Key>
        {
            public readonly ColliderShape Shape;
            public readonly float3 Size;
            public readonly CollisionFilter Filter;
            public readonly Material Material;

            public Key(ColliderShape shape, float3 size, CollisionFilter filter, Material material)
            {
                Shape = shape;
                Size = size;
                Filter = filter;
                Material = material;
            }

            public bool Equals(Key other) =>
                Shape == other.Shape && Size.Equals(other.Size) && Filter.Equals(other.Filter) && Material.Equals(other.Material);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)Shape;
                    hash = hash * 397 ^ Size.GetHashCode();
                    hash = hash * 397 ^ Filter.GetHashCode();
                    hash = hash * 397 ^ Material.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class Entry
        {
            public BlobAssetReference<Collider> Blob;
            public int Leases;
        }

        private readonly Dictionary<Key, Entry> _entries = new Dictionary<Key, Entry>();
        private bool _disposed;

        /// <summary>Distinct blobs currently allocated.</summary>
        public int Count => _entries.Count;

        /// <summary>Sum of outstanding leases across every blob.</summary>
        public int TotalLeases
        {
            get
            {
                var total = 0;
                foreach (var entry in _entries.Values) total += entry.Leases;
                return total;
            }
        }

        /// <summary>Outstanding leases on one blob; 0 when none exists.</summary>
        public int LeasesOf(ColliderShape shape, float3 size, CollisionFilter filter, Material material) =>
            _entries.TryGetValue(new Key(shape, size, filter, material), out var entry) ? entry.Leases : 0;

        /// <summary>
        /// Returns the shared blob for the description, creating it on first use. Validates the
        /// shape through <see cref="PhysicsBodyValidation.ValidateShape"/>.
        /// </summary>
        public BlobAssetReference<Collider> Acquire(ColliderShape shape, float3 size, CollisionFilter? filter = null, Material? material = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ColliderLibrary));

            var f = filter ?? CollisionFilter.Default;
            var m = material ?? Material.Default;
            PhysicsBodyValidation.ValidateShape(shape, size);
            PhysicsBodyValidation.ValidateFilter(f);

            var key = new Key(shape, size, f, m);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Blob = PhysicsBodyFactory.CreateCollider(shape, size, f, m) };
                _entries.Add(key, entry);
            }

            entry.Leases++;
            return entry.Blob;
        }

        /// <summary>
        /// Returns one lease. When the last lease goes, the blob is disposed. False for a blob this
        /// library does not own (already released to zero, or never acquired here).
        /// </summary>
        public bool Release(BlobAssetReference<Collider> blob)
        {
            if (_disposed) return false;

            foreach (var pair in _entries)
            {
                if (!pair.Value.Blob.Equals(blob)) continue;

                pair.Value.Leases--;
                if (pair.Value.Leases <= 0)
                {
                    pair.Value.Blob.Dispose();
                    _entries.Remove(pair.Key);
                }

                return true;
            }

            return false;
        }

        /// <summary>Whether <paramref name="blob"/> is one this library owns.</summary>
        public bool Owns(BlobAssetReference<Collider> blob)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Blob.Equals(blob)) return true;
            }

            return false;
        }

        /// <summary>
        /// Frees every blob regardless of outstanding leases. Entities still referencing them hold
        /// dangling pointers from here on — dispose the library after the world, or after the bodies
        /// are gone.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var entry in _entries.Values)
            {
                if (entry.Blob.IsCreated) entry.Blob.Dispose();
            }

            _entries.Clear();
        }
    }
}
