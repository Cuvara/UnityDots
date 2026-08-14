using System;
using System.Linq;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode.Prediction;
using NUnit.Framework;
using Unity.Entities;

namespace Cuvara.DOTS.Tests.Prediction
{
    /// <summary>
    /// The ordering that makes reconciliation use this frame's snapshot rather than last frame's.
    /// </summary>
    public sealed class PredictionGroupLayoutTests
    {
        private static Type GroupOf(Type type) =>
            type.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                .Cast<UpdateInGroupAttribute>().Single().GroupType;

        [Test]
        public void PredictionRunsAfterSnapshotApply_BothInsideNetcodeGroup()
        {
            // Reconciling before the anchor is written uses the previous frame's authoritative
            // position — a one-frame-stale correction, which reads as mistuned prediction rather
            // than as an ordering bug, and would be chased in the wrong package.
            Assert.AreEqual(typeof(NetcodeSystemGroup), GroupOf(typeof(SnapshotApplyGroup)));
            Assert.AreEqual(typeof(NetcodeSystemGroup), GroupOf(typeof(PredictionSystemGroup)));

            var after = typeof(PredictionSystemGroup)
                .GetCustomAttributes(typeof(UpdateAfterAttribute), false)
                .Cast<UpdateAfterAttribute>().Select(a => a.SystemType).ToArray();
            CollectionAssert.Contains(after, typeof(SnapshotApplyGroup));
        }

        [Test]
        public void TheDrivingSystem_IsInternal_AndTheGroupIsTheContract()
        {
            Assert.IsFalse(typeof(LocalPredictionSystem).IsPublic,
                "a public system name is an accidental API promise");
            Assert.IsTrue(typeof(PredictionSystemGroup).IsPublic);
            Assert.IsTrue(typeof(SnapshotApplyGroup).IsPublic);
        }

        [Test]
        public void TheDependencyRunsOneWayOnly()
        {
            // The split exists so the adapter still compiles with Shared.GameLogic absent. If either
            // of the two older assemblies ever referenced this one, that property is gone — and it
            // would go quietly, because a project with both packages installed compiles either way.
            // Only a project missing one would notice, which is to say: only CI's third row.
            var prediction = typeof(LocalPredictionSystem).Assembly.GetName().Name;

            foreach (var upstream in new[]
                     {
                         typeof(Cuvara.DOTS.Netcode.DotsEntityView).Assembly,
                         typeof(Cuvara.DOTS.Views.EntityViewRegistry).Assembly,
                     })
            {
                CollectionAssert.DoesNotContain(
                    upstream.GetReferencedAssemblies().Select(a => a.Name).ToArray(),
                    prediction,
                    $"{upstream.GetName().Name} must not reference {prediction} — the arrow is one-way");
            }
        }

        [Test]
        public void NothingHereIsAutoCreated()
        {
            foreach (var type in new[]
                     {
                         typeof(LocalPredictionSystem),
                         typeof(SnapshotApplyGroup),
                         typeof(PredictionSystemGroup),
                     })
            {
                Assert.IsNotEmpty(type.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false),
                    $"{type.Name} must be created explicitly, not in every world");
            }
        }
    }
}
