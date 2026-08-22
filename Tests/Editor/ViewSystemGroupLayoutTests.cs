using System;
using System.Linq;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Asserts the system group layout by reflection, because a wrong
    /// <see cref="UpdateInGroupAttribute"/> does not fail a build, does not throw at runtime, and
    /// shows up only as views lagging the simulation by a frame.
    /// </summary>
    public sealed class ViewSystemGroupLayoutTests
    {
        private static Type GroupOf(Type type)
        {
            var attribute = type.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                .Cast<UpdateInGroupAttribute>()
                .SingleOrDefault();

            Assert.IsNotNull(attribute, $"{type.Name} must declare exactly one explicit [UpdateInGroup]");
            return attribute.GroupType;
        }

        private static Type[] UpdateAfterOf(Type type) =>
            type.GetCustomAttributes(typeof(UpdateAfterAttribute), false)
                .Cast<UpdateAfterAttribute>()
                .Select(a => a.SystemType)
                .ToArray();

        private static Type[] UpdateBeforeOf(Type type) =>
            type.GetCustomAttributes(typeof(UpdateBeforeAttribute), false)
                .Cast<UpdateBeforeAttribute>()
                .Select(a => a.SystemType)
                .ToArray();

        private static readonly Type[] PackageSystems =
        {
            typeof(EntityViewSpawnSystem),
            typeof(EntityViewDespawnSystem),
            typeof(EntityViewTransformSyncSystem),
        };

        private static readonly Type[] PackageGroups =
        {
            typeof(NetcodeSystemGroup),
            typeof(SnapshotApplyGroup),
            typeof(PredictionSystemGroup),
            typeof(ProvisioningSystemGroup),
            typeof(GameplaySystemGroup),
            typeof(MovementSystemGroup),
            typeof(LifecycleSystemGroup),
            typeof(ViewSystemGroup),
            typeof(ViewInterpolationGroup),
            typeof(ViewLifecycleGroup),
            typeof(ViewTransformSyncGroup),
            typeof(DotsEndSimulationCommandBufferSystem),
        };

        [Test]
        public void InitializationGroups_AreOrderedNetcodeThenProvisioning()
        {
            Assert.AreEqual(typeof(InitializationSystemGroup), GroupOf(typeof(NetcodeSystemGroup)));
            Assert.AreEqual(typeof(InitializationSystemGroup), GroupOf(typeof(ProvisioningSystemGroup)));
            CollectionAssert.Contains(UpdateAfterOf(typeof(ProvisioningSystemGroup)), typeof(NetcodeSystemGroup));
        }

        [Test]
        public void GameplayGroup_RunsBeforeTheTransformSystems()
        {
            // Same parent group, so this ordering is legal and actually applied — and it is what
            // guarantees a position written this frame reaches LocalToWorld this frame.
            Assert.AreEqual(typeof(SimulationSystemGroup), GroupOf(typeof(GameplaySystemGroup)));
            CollectionAssert.Contains(UpdateBeforeOf(typeof(GameplaySystemGroup)), typeof(TransformSystemGroup));
        }

        [Test]
        public void GameplaySubGroups_AreOrderedMovementThenLifecycle()
        {
            Assert.AreEqual(typeof(GameplaySystemGroup), GroupOf(typeof(MovementSystemGroup)));
            Assert.AreEqual(typeof(GameplaySystemGroup), GroupOf(typeof(LifecycleSystemGroup)));
            CollectionAssert.Contains(UpdateAfterOf(typeof(LifecycleSystemGroup)), typeof(MovementSystemGroup));
        }

        [Test]
        public void CommandBufferSystem_IsLastInGameplay_NotUnitys()
        {
            var attribute = typeof(DotsEndSimulationCommandBufferSystem)
                .GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                .Cast<UpdateInGroupAttribute>()
                .Single();

            Assert.AreEqual(typeof(GameplaySystemGroup), attribute.GroupType);
            Assert.IsTrue(attribute.OrderLast, "playback must happen after every gameplay system");
        }

        [Test]
        public void ViewGroup_IsInPresentation_NotSimulation()
        {
            // In SimulationSystemGroup the sync would race TransformSystemGroup and the views would
            // trail the entities by a frame.
            Assert.AreEqual(typeof(PresentationSystemGroup), GroupOf(typeof(ViewSystemGroup)));
        }

        [Test]
        public void ViewSubGroups_NestUnderTheViewGroup_InterpolationThenLifecycleThenSync()
        {
            // Nested rather than flat so a consumer can inject between "views exist" and "views are
            // positioned" without naming a package system.
            Assert.AreEqual(typeof(ViewSystemGroup), GroupOf(typeof(ViewInterpolationGroup)));
            Assert.AreEqual(typeof(ViewSystemGroup), GroupOf(typeof(ViewLifecycleGroup)));
            Assert.AreEqual(typeof(ViewSystemGroup), GroupOf(typeof(ViewTransformSyncGroup)));

            // Interpolation first, because both of the other two READ the transform: the lifecycle
            // group places a new view from LocalToWorld and the sync group copies it onto every live
            // GameObject. Ordered later it would show this frame's views at last frame's rendered
            // position — one constant frame of lag on every remote entity, read as softness rather
            // than as an ordering fault. Empty without com.cuvara.netcode, and declared here anyway
            // so the position is fixed before anyone orders against it.
            CollectionAssert.Contains(UpdateBeforeOf(typeof(ViewInterpolationGroup)), typeof(ViewLifecycleGroup));
            CollectionAssert.Contains(UpdateAfterOf(typeof(ViewTransformSyncGroup)), typeof(ViewLifecycleGroup));
        }

        [Test]
        public void LifecycleSystems_AreInTheLifecycleGroup_DespawnBeforeSpawn()
        {
            Assert.AreEqual(typeof(ViewLifecycleGroup), GroupOf(typeof(EntityViewDespawnSystem)));
            Assert.AreEqual(typeof(ViewLifecycleGroup), GroupOf(typeof(EntityViewSpawnSystem)));

            // Despawn first, so a freed pool instance is reusable by this same frame's spawns.
            CollectionAssert.Contains(UpdateAfterOf(typeof(EntityViewSpawnSystem)), typeof(EntityViewDespawnSystem));
        }

        [Test]
        public void SyncSystem_IsInTheSyncGroup()
        {
            Assert.AreEqual(typeof(ViewTransformSyncGroup), GroupOf(typeof(EntityViewTransformSyncSystem)));
        }

        [Test]
        public void NoPackageSystemOrGroup_IsAutoCreated()
        {
            // Unity's default bootstrap creates every non-disabled system in EVERY world; two view
            // groups driving one registry would double-spawn every entity.
            foreach (var type in PackageSystems.Concat(PackageGroups))
            {
                Assert.IsNotEmpty(
                    type.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false),
                    $"{type.Name} must be [DisableAutoCreation] and created by DotsViewBootstrap");
            }
        }

        [Test]
        public void Groups_ArePublic_AndSystems_AreNot()
        {
            // The group tree is the ordering contract; individual system names are internal detail.
            foreach (var group in PackageGroups)
            {
                Assert.IsTrue(group.IsPublic, $"{group.Name} is part of the package's public ordering surface");
            }

            foreach (var system in PackageSystems)
            {
                Assert.IsFalse(system.IsPublic, $"{system.Name} must not be an accidental API promise");
            }
        }
    }
}
