using System;
using System.Linq;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using NUnit.Framework;
using Unity.Entities;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// The adapter's placement in the frame, asserted by reflection for the same reason
    /// <c>ViewSystemGroupLayoutTests</c> does it: a wrong <c>[UpdateInGroup]</c> compiles, does not
    /// throw, and shows up only as views trailing the wire by a frame.
    /// </summary>
    public sealed class NetcodeSystemLayoutTests
    {
        private static readonly Type DrainSystem = typeof(NetworkViewCommandSystem);
        private static readonly Type InterpolationSystem = typeof(RemoteInterpolationSystem);
        private static readonly Type ClockSystem = typeof(InterpolationClockSystem);

        private static Type GroupOf(Type type) =>
            type.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                .Cast<UpdateInGroupAttribute>().Single().GroupType;

        [Test]
        public void Drain_IsInTheNetcodeGroup_WhichIsInInitialization()
        {
            // This is the whole "same frame" argument, and it is two hops: the drain is in
            // NetcodeSystemGroup, and NetcodeSystemGroup is in InitializationSystemGroup — which runs
            // before SimulationSystemGroup (and so before TransformSystemGroup) and long before
            // PresentationSystemGroup, where ViewSystemGroup lives.
            // Three hops since 0.13.0, not two: the drain moved into SnapshotApplyGroup so that
            // prediction could order itself after it without naming a system. What this test
            // protects is the containment chain, which is unchanged — everything still lands in
            // InitializationSystemGroup, before this frame's transforms and views.
            var group = DrainSystem.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                .Cast<UpdateInGroupAttribute>()
                .Single();

            Assert.AreEqual(typeof(SnapshotApplyGroup), group.GroupType);
            Assert.AreEqual(typeof(NetcodeSystemGroup),
                typeof(SnapshotApplyGroup).GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                    .Cast<UpdateInGroupAttribute>().Single().GroupType);
            Assert.AreEqual(typeof(InitializationSystemGroup),
                typeof(NetcodeSystemGroup).GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                    .Cast<UpdateInGroupAttribute>().Single().GroupType);
        }

        [Test]
        public void Interpolation_IsInPresentation_AndRunsBeforeAnythingThatReadsTheTransform()
        {
            // The other half of the same "same frame" argument, in the other direction. Snapshot
            // application is early — before this frame's transforms — because everything downstream
            // reacts to what arrived. Interpolation is late, in presentation, because it answers
            // "where is this drawn on this frame", which is a per-drawn-frame question and must not
            // inherit fixed-step semantics from SimulationSystemGroup.
            Assert.AreEqual(typeof(ViewInterpolationGroup), GroupOf(InterpolationSystem));
            Assert.AreEqual(typeof(ViewInterpolationGroup), GroupOf(ClockSystem));
            Assert.AreEqual(typeof(ViewSystemGroup), GroupOf(typeof(ViewInterpolationGroup)));
            Assert.AreEqual(typeof(PresentationSystemGroup), GroupOf(typeof(ViewSystemGroup)));

            // Before the lifecycle group — and so, transitively, before the sync group that already
            // declares itself after it. Running later would place this frame's views and copy this
            // frame's transforms from last frame's interpolated position: a constant one-frame lag
            // on every remote entity, which reads as softness rather than as an ordering bug.
            var before = typeof(ViewInterpolationGroup)
                .GetCustomAttributes(typeof(UpdateBeforeAttribute), false)
                .Cast<UpdateBeforeAttribute>().Select(a => a.SystemType).ToArray();
            CollectionAssert.Contains(before, typeof(ViewLifecycleGroup));
        }

        [Test]
        public void TheRenderClock_IsAdvancedBeforeAnythingIsEvaluatedAgainstIt()
        {
            // An explicit relation rather than OrderFirst: Entities sorts OrderFirst members into a
            // separate batch and drops ordering relations between that batch and ordinary members,
            // with a warning. The failure would be a clock advanced after the frame it describes was
            // already drawn — one frame of lag that no assertion about position could attribute.
            var before = ClockSystem
                .GetCustomAttributes(typeof(UpdateBeforeAttribute), false)
                .Cast<UpdateBeforeAttribute>().Select(a => a.SystemType).ToArray();

            CollectionAssert.Contains(before, InterpolationSystem);
        }

        [Test]
        public void InterpolationSystems_AreNotAutoCreated_AndAreInternal()
        {
            // Same two reasons as the drain: a second copy in a second world would advance the same
            // timeline twice per frame, and a public system name is an accidental API promise. The
            // group is the contract.
            Assert.IsNotEmpty(InterpolationSystem.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false));
            Assert.IsNotEmpty(ClockSystem.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false));
            Assert.IsNotEmpty(typeof(ViewInterpolationGroup)
                .GetCustomAttributes(typeof(DisableAutoCreationAttribute), false));

            Assert.IsFalse(InterpolationSystem.IsPublic, "a public system name is an accidental API promise");
            Assert.IsFalse(ClockSystem.IsPublic);
            Assert.IsTrue(typeof(ViewInterpolationGroup).IsPublic);
        }

        [Test]
        public void Drain_IsNotAutoCreated()
        {
            // Unity's default bootstrap creates every non-disabled system in EVERY world. A second
            // drain in a second world would race this one for the same queue and each would get
            // half the commands.
            Assert.IsNotEmpty(DrainSystem.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false));
        }

        [Test]
        public void Drain_IsInternal_AndTheGroupIsThePublicContract()
        {
            Assert.IsFalse(DrainSystem.IsPublic, "a public system name is an accidental API promise");
            Assert.IsTrue(typeof(NetcodeSystemGroup).IsPublic);
        }

        [Test]
        public void PublicSurface_DoesNotCollideWithTheVocabularyOfTheDependencies()
        {
            // A previous type in this package's history was named Lifetime and broke an assembly its
            // author could not touch, invisibly from the declaring side. These are the simple names
            // this assembly adds; the check is that none of them is a word VContainer (Lifetime,
            // Scope), MessagePipe (IPublisher, ISubscriber), UniT (IAssetsManager) or Entities
            // (World, SystemState, Entity) already owns.
            var reserved = new[]
            {
                "Lifetime", "Scope", "LifetimeScope",
                "IPublisher", "ISubscriber",
                "IAssetsManager", "IObjectPoolManager",
                "World", "SystemState", "Entity", "EntityManager", "Health",
            };

            var declared = DrainSystem.Assembly.GetTypes()
                .Where(t => t.IsPublic || t.IsNestedPublic)
                .Select(t => t.Name)
                .ToArray();

            CollectionAssert.IsNotEmpty(declared);
            foreach (var name in declared)
            {
                CollectionAssert.DoesNotContain(reserved, name, $"'{name}' is a word a supported dependency already owns");
            }
        }
    }
}
