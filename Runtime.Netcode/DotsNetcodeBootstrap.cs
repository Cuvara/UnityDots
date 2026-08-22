using System;
using Cuvara.DOTS.Groups;
using Cuvara.Netcode.Interpolation;
using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Installs the snapshot adapter into one world: publishes the
    /// <see cref="NetworkEntityViewReference"/> singleton and creates the drain system inside
    /// <see cref="NetcodeSystemGroup"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate entry point rather than an addition to <c>DotsViewBootstrap</c>, and that is the
    /// one-way dependency arrow made concrete: <c>Cuvara.DOTS.Runtime</c> must keep compiling with
    /// <c>com.cuvara.netcode</c> absent, so it cannot name a system that lives in this assembly.
    /// Consumers call <c>DotsViewBootstrap.Install</c> and then this.
    /// </para>
    /// <para>
    /// Idempotent, in the same sense and for the same reason as <c>DotsViewBootstrap.Install</c>: a
    /// second call replaces the referenced view rather than creating a second singleton, which would
    /// make every <c>GetSingleton</c> throw.
    /// </para>
    /// </remarks>
    public static class DotsNetcodeBootstrap
    {
        /// <param name="world">The world that presents.</param>
        /// <param name="view">The adapter netcode calls.</param>
        /// <param name="interpolation">
        /// Remote interpolation tuning. Leave at <c>default</c> for netcode's own defaults — every
        /// non-positive field is filled from them, which is what <c>default(InterpolationConfig)</c>
        /// being all zeroes requires. Pass a configured value for a deployment at a world rate other
        /// than the 15 Hz this package is tuned for.
        /// </param>
        public static Entity Install(World world, DotsEntityView view, InterpolationConfig interpolation = default)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (view == null) throw new ArgumentNullException(nameof(view));

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkEntityViewReference>());

            Entity entity;
            if (query.IsEmpty)
            {
                entity = entityManager.CreateEntity();
                entityManager.AddComponentObject(entity, new NetworkEntityViewReference { View = view });
#if UNITY_EDITOR
                entityManager.SetName(entity, "NetworkEntityView");
#endif
            }
            else
            {
                entity = query.GetSingletonEntity();
                entityManager.GetComponentObject<NetworkEntityViewReference>(entity).View = view;
            }

            InstallSystems(world);

            // Seeded from the view's own mapping, not from a second configuration: the samples are
            // stored in server space and projected in the job, so the mapping the states arrived
            // under has to be the mapping they are rendered under. Two independently configured
            // copies would draw every remote entity in the wrong place with each half passing its
            // own test.
            SeedInterpolation(world, interpolation.Normalized(), view.Mapping, overwrite: true);

            return entity;
        }

        /// <summary>
        /// Creates the drain system and hangs it under <see cref="NetcodeSystemGroup"/>, creating
        /// that group and its parent if the view bootstrap has not already.
        /// </summary>
        public static void InstallSystems(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var initialization = world.GetOrCreateSystemManaged<InitializationSystemGroup>();
            var netcode = world.GetOrCreateSystemManaged<NetcodeSystemGroup>();
            initialization.AddSystemToUpdateList(netcode);

            // Into SnapshotApplyGroup, which is where the drain declares itself. Adding it straight
            // to NetcodeSystemGroup — as this did before the sub-groups existed — puts the system in
            // a group its [UpdateInGroup] does not name, so the ordering relation between it and
            // PredictionSystemGroup never resolves and prediction can run BEFORE the anchor it
            // reconciles against is written. That failed three prediction tests with the marker
            // simply never being claimed, which pointed at the predictor rather than at this line.
            var snapshotApply = world.GetOrCreateSystemManaged<SnapshotApplyGroup>();
            netcode.AddSystemToUpdateList(snapshotApply);
            snapshotApply.AddSystemToUpdateList(world.GetOrCreateSystem<NetworkViewCommandSystem>());

            // Remote interpolation is the presentation half of the adapter, and it is installed
            // from here rather than from DotsViewBootstrap for the reason this whole assembly
            // exists: the core must keep compiling with com.cuvara.netcode absent, so it cannot
            // name a system that reads netcode's interpolation core. The group itself is created by
            // the core bootstrap and stands empty without this call.
            var presentation = world.GetOrCreateSystemManaged<PresentationSystemGroup>();
            var viewGroup = world.GetOrCreateSystemManaged<ViewSystemGroup>();
            var interpolation = world.GetOrCreateSystemManaged<ViewInterpolationGroup>();
            presentation.AddSystemToUpdateList(viewGroup);
            viewGroup.AddSystemToUpdateList(interpolation);
            interpolation.AddSystemToUpdateList(world.GetOrCreateSystem<InterpolationClockSystem>());
            interpolation.AddSystemToUpdateList(world.GetOrCreateSystem<RemoteInterpolationSystem>());

            // Both singletons exist from installation, with netcode's defaults and this package's
            // default mapping, so a world whose consumer never calls Install(world, view) — a test
            // driving the groups directly, say — still has a clock to advance rather than two
            // systems that never update. Install overwrites them with the view's real mapping.
            SeedInterpolation(world, InterpolationConfig.Default, SnapshotSpaceMapping.XZPlane, overwrite: false);

            // Manual adds are not sorted automatically; without this the group's ordering relations
            // are declared but never applied.
            initialization.SortSystems();
            presentation.SortSystems();
        }

        /// <summary>
        /// Creates or overwrites the two interpolation singletons on one entity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The timeline is created but never overwritten.</b> Re-seeding the clock would throw
        /// away the render moment mid-session, and the clock's entire guarantee is that it is never
        /// snapped — a reinstall that reset it would step every remote entity backwards, which is
        /// the exact discontinuity the core was written to remove. A consumer that genuinely wants a
        /// fresh timeline is starting a fresh session and should dispose the world, as the netcode
        /// side's <c>Reset</c> exists for.
        /// </para>
        /// <para>
        /// One entity carrying both: they are separate component types, so each is still a
        /// singleton, and the pair is created and found together.
        /// </para>
        /// </remarks>
        private static void SeedInterpolation(
            World world,
            InterpolationConfig config,
            SnapshotSpaceMapping mapping,
            bool overwrite)
        {
            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<InterpolationSettings>());

            var settings = new InterpolationSettings { Config = config, Mapping = mapping };

            if (query.IsEmpty)
            {
                var entity = entityManager.CreateEntity();
                entityManager.AddComponentData(entity, settings);
                entityManager.AddComponentData(entity, new InterpolationTimeline());
#if UNITY_EDITOR
                entityManager.SetName(entity, "InterpolationSettings");
#endif
                return;
            }

            // Only Install overwrites. InstallSystems is also reachable on its own and after
            // Install, and a bare call must not quietly replace a configured mapping with the
            // default one — an entity drawn on the wrong plane looks like a networking fault.
            if (overwrite) entityManager.SetComponentData(query.GetSingletonEntity(), settings);
        }

        /// <summary>
        /// Removes the singleton. Safe on a world that never had one, and does not destroy the
        /// mirrored entities — those belong to the world, and a consumer tearing down a session
        /// usually disposes the world itself.
        /// </summary>
        public static void Uninstall(World world)
        {
            if (world == null || !world.IsCreated) return;

            var entityManager = world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkEntityViewReference>());
            if (!query.IsEmpty) entityManager.DestroyEntity(query.GetSingletonEntity());

            // The interpolation singletons go with it: they are this assembly's, and leaving a
            // render clock behind in a world whose adapter was removed would keep advancing a
            // timeline nothing feeds.
            using var interpolation = entityManager.CreateEntityQuery(ComponentType.ReadWrite<InterpolationSettings>());
            if (!interpolation.IsEmpty) entityManager.DestroyEntity(interpolation.GetSingletonEntity());
        }
    }
}
