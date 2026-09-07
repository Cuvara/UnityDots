using System;
using Cuvara.DOTS.Physics;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Physics;

namespace Cuvara.DOTS.Tests.Physics
{
    /// <summary>Shared collider ownership: one blob per description, counted leases, teardown to zero.</summary>
    public sealed class ColliderLibraryTests
    {
        private ColliderLibrary _library;

        [SetUp]
        public void SetUp() => _library = new ColliderLibrary();

        [TearDown]
        public void TearDown() => _library.Dispose();

        [Test]
        public void SameDescription_SharesOneBlob_AndCountsLeases()
        {
            var a = _library.Acquire(ColliderShape.Sphere, new float3(0.5f));
            var b = _library.Acquire(ColliderShape.Sphere, new float3(0.5f));

            Assert.IsTrue(a.Equals(b), "same pointer, one allocation");
            Assert.AreEqual(1, _library.Count);
            Assert.AreEqual(2, _library.TotalLeases);
            Assert.IsTrue(_library.Owns(a));
        }

        [Test]
        public void DifferentSizeFilterOrMaterial_AreDifferentBlobs()
        {
            var plain = _library.Acquire(ColliderShape.Sphere, new float3(0.5f));
            var bigger = _library.Acquire(ColliderShape.Sphere, new float3(0.75f));
            var filtered = _library.Acquire(ColliderShape.Sphere, new float3(0.5f), new CollisionFilter { BelongsTo = 2u, CollidesWith = ~0u });
            var trigger = _library.Acquire(ColliderShape.Sphere, new float3(0.5f), null, PhysicsBodyFactory.TriggerMaterial());

            Assert.IsFalse(plain.Equals(bigger));
            Assert.IsFalse(plain.Equals(filtered));
            Assert.IsFalse(plain.Equals(trigger));
            Assert.AreEqual(4, _library.Count);
            Assert.AreEqual(CollisionResponsePolicy.RaiseTriggerEvents, trigger.Value.GetCollisionResponse());
        }

        [Test]
        public void Release_ToZero_FreesTheBlob_AndIsFalseAfterwards()
        {
            var a = _library.Acquire(ColliderShape.Box, new float3(1f, 2f, 3f));
            _library.Acquire(ColliderShape.Box, new float3(1f, 2f, 3f));

            Assert.IsTrue(_library.Release(a));
            Assert.AreEqual(1, _library.Count, "one lease left keeps the blob");
            Assert.IsTrue(_library.Release(a));
            Assert.AreEqual(0, _library.Count, "last lease frees it");
            Assert.AreEqual(0, _library.TotalLeases);
            Assert.IsFalse(_library.Release(a), "not owned any more");
            Assert.IsFalse(_library.Owns(a));
        }

        [Test]
        public void RepeatedAcquireRelease_ReturnsToBaseline()
        {
            for (var i = 0; i < 100; i++)
            {
                var blob = _library.Acquire(ColliderShape.Capsule, new float3(0.3f, 1.8f, 0f));
                _library.Release(blob);
            }

            Assert.AreEqual(0, _library.Count);
            Assert.AreEqual(0, _library.TotalLeases);
        }

        [Test]
        public void Dispose_FreesEverything_AndIsIdempotent()
        {
            _library.Acquire(ColliderShape.Sphere, new float3(1f));
            _library.Acquire(ColliderShape.Cylinder, new float3(1f, 2f, 0f));

            _library.Dispose();
            _library.Dispose();

            Assert.AreEqual(0, _library.Count);
            Assert.Throws<ObjectDisposedException>(() => _library.Acquire(ColliderShape.Sphere, new float3(1f)));
        }

        [Test]
        public void Acquire_ValidatesShapeAndFilter()
        {
            Assert.Throws<ArgumentException>(() => _library.Acquire(ColliderShape.Sphere, new float3(0f)));
            Assert.Throws<ArgumentException>(() => _library.Acquire(ColliderShape.Sphere, new float3(1f), CollisionFilter.Zero));
            Assert.AreEqual(0, _library.Count, "nothing allocated for a rejected description");
        }
    }
}
