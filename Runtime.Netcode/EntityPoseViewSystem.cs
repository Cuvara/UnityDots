using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Views;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Tells each linked view's <see cref="IEntityAnimationReceiver"/>, if it has one, what its
    /// entity is doing — and whether that is a new occurrence or a continuing state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The comparison is here, once, rather than in every view script.</b> Two rules have to
    /// hold and both are easy to get wrong in isolation: the retrigger test is INEQUALITY (the
    /// counter wraps and resets, so greater-than stops working for four billion actions after a
    /// wrap), and the memory of what was last shown must be tied to the ENTITY rather than to the
    /// pooled GameObject that is currently drawing it. <see cref="EntityPoseView"/> is that memory
    /// and it dies with the entity.
    /// </para>
    /// <para>
    /// <b>Not Bursted and not a job, deliberately.</b> It calls an interface method on a
    /// MonoBehaviour, which is managed and main-thread-only either way; the only thing a job would
    /// buy is the chunk walk, and the loop only touches entities whose pose actually changed.
    /// </para>
    /// <para>
    /// A view with no receiver costs one dictionary lookup and one <c>TryGetComponent</c> per
    /// change — not per frame — so a project that never implements the interface pays almost
    /// nothing for its existence.
    /// </para>
    /// <para>
    /// <b>It lives in the netcode assembly, not in the core.</b> It reads
    /// <see cref="EntityPose"/>, which carries a <c>Shared.GameLogic</c> enum, and the core
    /// assembly resolves against Entities, Burst, Collections and Mathematics alone. Putting it
    /// under <c>Runtime/Views</c> compiled fine in a plain csproj and failed in Unity, which is
    /// the only build that models asmdef visibility — the package's standalone-install rule is
    /// enforced by the asmdef graph and by nothing else.
    /// </para>
    /// </remarks>
    [DisableAutoCreation]
    // No [UpdateAfter] on the transform sync, which is internal to the core assembly and cannot
    // be named from here. Order inside the group comes from the registration order in
    // DotsNetcodeBootstrap instead — the transform sync is added by the core bootstrap, which a
    // consumer always runs first. The relation is weaker than a declared one and is stated here
    // rather than assumed: a view that reacts to an action before its transform has been synced
    // would react one frame behind, which is a cosmetic lag, not a correctness break.
    [UpdateInGroup(typeof(ViewTransformSyncGroup))]
    internal partial struct EntityPoseViewSystem : ISystem
    {
        private EntityQuery _posed;

        public void OnCreate(ref SystemState state)
        {
            _posed = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityPose, EntityViewLink>()
                .Build(ref state);

            state.RequireForUpdate(_posed);
            state.RequireForUpdate<EntityViewRegistryReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var registry = SystemAPI.ManagedAPI.GetSingleton<EntityViewRegistryReference>().Registry;
            if (registry == null) return;

            var ecb = new EntityCommandBuffer(state.WorldUpdateAllocator);

            foreach (var (pose, link, entity) in
                     SystemAPI.Query<RefRO<EntityPose>, RefRO<EntityViewLink>>().WithEntityAccess())
            {
                // An entity that has never been shown anything gets Initialised = false, which makes
                // its first pose a change rather than a comparison against a default that happens
                // to match — an entity whose first reported action is Idle would otherwise be
                // reported to the view as "no change" and never announced at all.
                var hasShown = SystemAPI.HasComponent<EntityPoseView>(entity);
                var shown = hasShown
                    ? SystemAPI.GetComponent<EntityPoseView>(entity)
                    : default;

                var action = pose.ValueRO.Action;
                var seq = pose.ValueRO.ActionSeq;

                bool actionChanged = !shown.Initialised || shown.ShownAction != action;

                // INEQUALITY, never greater-than. And only when the server actually sends a
                // counter: zero means it does not, and treating that as an edge would retrigger
                // every animation on every frame against an older server.
                bool retriggered = seq != 0 && shown.Initialised && shown.ShownActionSeq != seq
                                   && shown.ShownAction == action;

                if (!actionChanged && !retriggered) continue;

                // Resolved BEFORE the state is recorded, and the order is the whole of this fix.
                // A view is provisioned asynchronously — the asset may still be loading when the
                // entity's first action arrives — and marking the action as shown while there was
                // nobody to show it to CONSUMES it: the view appears a frame later and is never
                // told about the swing that happened just before it existed. An entity that spawns
                // and immediately attacks would silently skip its first attack animation, forever,
                // with nothing anywhere reporting a problem. Caught by running a built player and
                // seeing 9 swings played against 10 sent.
                // Recorded before the view is resolved, and that is safe rather than lucky: the
                // query above requires EntityViewLink, so an entity whose view has not been
                // provisioned yet is not in this loop AT ALL and its action cannot be consumed
                // here. The `go == null` case below is the narrower one — a link that outlived the
                // GameObject — and EntityViewLinkCleanup clears those.
                var next = new EntityPoseView
                {
                    ShownAction = action,
                    ShownActionSeq = seq,
                    Initialised = true,
                };

                if (hasShown) ecb.SetComponent(entity, next);
                else ecb.AddComponent(entity, next);

                var go = registry.Get(link.ValueRO.ViewId);
                if (go == null) continue;

                // TryGetComponent rather than GetComponent: a view without a receiver is the
                // ordinary case, and GetComponent would log a null-reference for each one.
                if (go.TryGetComponent<IEntityAnimationReceiver>(out var receiver))
                {
                    receiver.OnAction(action, retriggered);
                }
            }

            ecb.Playback(state.EntityManager);
        }
    }
}
