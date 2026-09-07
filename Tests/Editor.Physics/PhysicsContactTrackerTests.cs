using System.Collections.Generic;
using System.Linq;
using Cuvara.DOTS.Physics;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace Cuvara.DOTS.Tests.Physics
{
    /// <summary>
    /// Enter/stay/exit semantics, contact aggregation, pair ordering, entity identity across index
    /// reuse, destroyed entities and deterministic output order — on the pure tracker, no physics.
    /// </summary>
    public sealed class PhysicsContactTrackerTests
    {
        private static Entity E(int index, int version = 1) => new Entity { Index = index, Version = version };

        // NUnit reuses one fixture instance for every test in the class, so these are rebuilt per
        // test: a tracker left mid-step by one test would fail the next with "BeginStep called twice".
        private PhysicsContactTracker _tracker;
        private List<EntityCollision> _collisions;
        private List<EntityTriggerEvent> _triggers;

        [SetUp]
        public void SetUp()
        {
            _tracker = new PhysicsContactTracker();
            _collisions = new List<EntityCollision>();
            _triggers = new List<EntityTriggerEvent>();
        }

        private void Step(params (Entity a, Entity b)[] contacts)
        {
            _collisions.Clear();
            _triggers.Clear();
            _tracker.BeginStep();
            foreach (var (a, b) in contacts) _tracker.ReportCollision(a, b, new float3(0f, 1f, 0f), float3.zero, 1f);
            _tracker.EndStep(_collisions, _triggers);
        }

        [Test]
        public void EnterStayExit_AcrossThreeSteps()
        {
            Step((E(1), E(2)));
            Assert.AreEqual(1, _collisions.Count);
            Assert.AreEqual(PhysicsContactPhase.Enter, _collisions[0].Phase);

            Step((E(1), E(2)));
            Assert.AreEqual(PhysicsContactPhase.Stay, _collisions[0].Phase);
            Assert.AreEqual(1, _tracker.ActiveCollisionPairs);

            Step();
            Assert.AreEqual(1, _collisions.Count);
            Assert.AreEqual(PhysicsContactPhase.Exit, _collisions[0].Phase);
            Assert.AreEqual(0, _collisions[0].ContactCount);
            Assert.AreEqual(0, _tracker.ActiveCollisionPairs);

            Step();
            Assert.IsEmpty(_collisions, "exit is reported exactly once");
        }

        [Test]
        public void MultipleContacts_OnePair_AreAggregated()
        {
            _tracker.BeginStep();
            _tracker.ReportCollision(E(1), E(2), new float3(0f, 1f, 0f), new float3(0f, 0f, 0f), 2f);
            _tracker.ReportCollision(E(1), E(2), new float3(0f, 1f, 0f), new float3(2f, 0f, 0f), 3f);
            _tracker.ReportCollision(E(1), E(2), new float3(1f, 0f, 0f), new float3(4f, 0f, 0f), 1f);
            _tracker.EndStep(_collisions, _triggers);

            Assert.AreEqual(1, _collisions.Count, "one event per pair per step");
            var e = _collisions[0];
            Assert.AreEqual(3, e.ContactCount);
            Assert.AreEqual(6f, e.Impulse, 1e-5f, "impulses sum");
            Assert.AreEqual(new float3(2f, 0f, 0f), e.Position, "positions average");
            Assert.AreEqual(1f, math.length(e.Normal), 1e-5f, "normal is normalised");
            Assert.Greater(e.Normal.y, e.Normal.x, "and weighted toward the majority direction");
        }

        [Test]
        public void PairOrder_IsCanonical_AndTheNormalFollowsIt()
        {
            _tracker.BeginStep();
            _tracker.ReportCollision(E(5), E(2), new float3(0f, 0f, 1f), float3.zero, 1f); // reported B-first
            _tracker.EndStep(_collisions, _triggers);

            var e = _collisions[0];
            Assert.AreEqual(E(2), e.EntityA, "A is the lower index");
            Assert.AreEqual(E(5), e.EntityB);
            Assert.AreEqual(new float3(0f, 0f, -1f), e.Normal, "flipped so it points from A toward B");

            // Reported the other way round next step: same pair, so Stay — not Exit + Enter.
            Step((E(2), E(5)));
            Assert.AreEqual(1, _collisions.Count);
            Assert.AreEqual(PhysicsContactPhase.Stay, _collisions[0].Phase);
        }

        [Test]
        public void SameIndexNewVersion_IsANewEntity_NotAStay()
        {
            Step((E(1, version: 1), E(2)));
            Assert.AreEqual(PhysicsContactPhase.Enter, _collisions[0].Phase);

            // Entity 1 was destroyed and its index reused; the new occupant touches entity 2.
            Step((E(1, version: 2), E(2)));

            Assert.AreEqual(2, _collisions.Count);
            Assert.AreEqual(PhysicsContactPhase.Exit, _collisions[0].Phase, "exits come first");
            Assert.AreEqual(1, _collisions[0].EntityA.Version);
            Assert.AreEqual(PhysicsContactPhase.Enter, _collisions[1].Phase);
            Assert.AreEqual(2, _collisions[1].EntityA.Version);
        }

        [Test]
        public void DestroyedEntity_ExitIsFlagged()
        {
            Step((E(1), E(2)));

            _collisions.Clear();
            _tracker.BeginStep();
            _tracker.EndStep(_collisions, _triggers, exists: entity => entity != E(1));

            Assert.AreEqual(PhysicsContactPhase.Exit, _collisions[0].Phase);
            Assert.IsTrue(_collisions[0].AnyEntityDestroyed);
        }

        [Test]
        public void OutputOrder_ExitsThenEntersThenStays_EachSortedByPair()
        {
            Step((E(7), E(8)), (E(3), E(4)));
            Step((E(3), E(4)), (E(1), E(2)), (E(9), E(10)));

            var phases = _collisions.Select(c => c.Phase).ToArray();
            CollectionAssert.AreEqual(
                new[] { PhysicsContactPhase.Exit, PhysicsContactPhase.Enter, PhysicsContactPhase.Enter, PhysicsContactPhase.Stay },
                phases);
            Assert.AreEqual(E(7), _collisions[0].EntityA);
            Assert.AreEqual(E(1), _collisions[1].EntityA, "enters sorted by pair");
            Assert.AreEqual(E(9), _collisions[2].EntityA);
            Assert.AreEqual(E(3), _collisions[3].EntityA);
        }

        [Test]
        public void Triggers_HaveTheSameLifecycle_AndDuplicatesFold()
        {
            _tracker.BeginStep();
            _tracker.ReportTrigger(E(1), E(2));
            _tracker.ReportTrigger(E(2), E(1)); // compound collider: two child events, one pair
            _tracker.EndStep(_collisions, _triggers);
            Assert.AreEqual(1, _triggers.Count);
            Assert.AreEqual(PhysicsContactPhase.Enter, _triggers[0].Phase);
            Assert.IsTrue(_triggers[0].Entered, "0.27 compatibility view");

            _triggers.Clear();
            _tracker.BeginStep();
            _tracker.ReportTrigger(E(1), E(2));
            _tracker.EndStep(_collisions, _triggers);
            Assert.AreEqual(PhysicsContactPhase.Stay, _triggers[0].Phase);

            _triggers.Clear();
            _tracker.BeginStep();
            _tracker.EndStep(_collisions, _triggers, exists: e => e != E(2));
            Assert.AreEqual(PhysicsContactPhase.Exit, _triggers[0].Phase);
            Assert.IsTrue(_triggers[0].AnyEntityDestroyed);
            Assert.IsFalse(_triggers[0].Entered);
        }

        [Test]
        public void SelfPair_IsIgnored()
        {
            Step((E(1), E(1)));
            Assert.IsEmpty(_collisions);
        }

        [Test]
        public void Clear_ForgetsPairs_WithoutExits()
        {
            Step((E(1), E(2)));
            _tracker.Clear();
            Step();

            Assert.IsEmpty(_collisions, "a reconnect reset does not emit an exit storm");
            Assert.AreEqual(0, _tracker.ActiveCollisionPairs);
        }

        [Test]
        public void ReportOutsideAStep_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(() => _tracker.ReportCollision(E(1), E(2), float3.zero, float3.zero, 0f));
            Assert.Throws<System.InvalidOperationException>(() => _tracker.EndStep(_collisions, _triggers));
            _tracker.BeginStep();
            Assert.Throws<System.InvalidOperationException>(() => _tracker.BeginStep());
        }

        [Test]
        public void PairKey_EqualityAndOrdering_UseIndexThenVersion()
        {
            Assert.AreEqual(new PhysicsPairKey(E(1), E(2)), new PhysicsPairKey(E(2), E(1)));
            Assert.AreNotEqual(new PhysicsPairKey(E(1, 1), E(2)), new PhysicsPairKey(E(1, 2), E(2)));
            Assert.IsTrue(new PhysicsPairKey(E(2), E(1)).Swapped);
            Assert.Less(new PhysicsPairKey(E(1), E(9)).CompareTo(new PhysicsPairKey(E(2), E(3))), 0);
            Assert.Less(new PhysicsPairKey(E(1, 1), E(9)).CompareTo(new PhysicsPairKey(E(1, 2), E(3))), 0);
        }
    }
}
