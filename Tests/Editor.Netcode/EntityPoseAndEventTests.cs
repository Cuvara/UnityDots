using System.Collections.Generic;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// The pose and event paths through the netcode adapter: facing and action reaching the
    /// mirror, the retrigger counter surviving to a view, and one frame of game events landing
    /// on the buffer with their participants resolved.
    /// </summary>
    /// <remarks>
    /// Both features fail the same quiet way: the wire carries the value, the adapter accepts
    /// it, and nothing downstream ever sees it — with no exception and no failing assertion
    /// anywhere. So every test here asserts on what a CONSUMER would read, never on what was
    /// enqueued.
    /// </remarks>
    public sealed class EntityPoseAndEventTests
    {
        private const string RemoteArchetype = "player-remote";
        private const string PlayerType = "player";

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private Entity _viewEntity;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.EntityPoseAndEventTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");
            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = RemoteArchetype, Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(RemoteArchetype, null, new TypeArchetypeResolver.Rule(PlayerType, RemoteArchetype)),
                SnapshotSpaceMapping.XZPlane);
            _viewEntity = DotsNetcodeBootstrap.Install(_world, _view);

            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            Object.DestroyImmediate(_library);
            Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        private void Tick()
        {
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
            _world.GetExistingSystemManaged<ViewSystemGroup>().Update();
        }

        private Entity Find(string id)
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);
            for (var i = 0; i < entities.Length; i++)
            {
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted)) return entities[i];
            }

            return Entity.Null;
        }

        private void Spawn(string id) => ((IEntityView)_view).Spawn(id, isLocal: false, type: PlayerType);

        private void State(string id, float x = 0f, float y = 0f, int hp = 100) =>
            ((IEntityView)_view).SetState(id, x, y, hp, 100);

        private void Pose(string id, uint facing, SimAction action, uint seq) =>
            ((IEntityPoseView)_view).SetPose(id, facing, action, seq);

        // ── Pose ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Before this, the DOTS adapter implemented <c>IEntityView</c> only — so a DOTS client
        /// received no facing and no action at all and could not turn or animate a character.
        /// </summary>
        [Test]
        public void TheAdapterAcceptsAPose()
        {
            Assert.That(_view, Is.InstanceOf<IEntityPoseView>(),
                "the binder only calls SetPose on a view that implements IEntityPoseView; " +
                "without this the whole pose path is silently skipped");
        }

        [Test]
        public void APoseReachesTheMirror()
        {
            Spawn("r1");
            State("r1");
            Pose("r1", facing: 16385u, action: SimAction.Attacking, seq: 3u);
            Tick();

            var entity = Find("r1");
            Assert.That(entity, Is.Not.EqualTo(Entity.Null));
            Assert.That(_entityManager.HasComponent<EntityPose>(entity), Is.True);

            var pose = _entityManager.GetComponentData<EntityPose>(entity);
            Assert.That(pose.FacingBrad, Is.EqualTo(16385u));
            Assert.That(pose.Action, Is.EqualTo(SimAction.Attacking));
            Assert.That(pose.ActionSeq, Is.EqualTo(3u));
        }

        /// <summary>
        /// Zero means "this server does not send it". Holding the last non-zero value in the
        /// adapter would make an old server look like one that had stopped turning.
        /// </summary>
        [Test]
        public void AZeroPoseIsWrittenVerbatimRatherThanHeld()
        {
            Spawn("r1");
            State("r1");
            Pose("r1", facing: 16385u, action: SimAction.Moving, seq: 2u);
            Tick();

            Pose("r1", facing: 0u, action: SimAction.Unspecified, seq: 0u);
            Tick();

            var pose = _entityManager.GetComponentData<EntityPose>(Find("r1"));
            Assert.That(pose.FacingBrad, Is.Zero);
            Assert.That(pose.Action, Is.EqualTo(SimAction.Unspecified));
            Assert.That(pose.ActionSeq, Is.Zero);
        }

        [Test]
        public void APoseForAnUnknownEntityIsDroppedRatherThanResurrectingIt()
        {
            Pose("never-spawned", facing: 1u, action: SimAction.Idle, seq: 1u);
            Tick();

            Assert.That(Find("never-spawned"), Is.EqualTo(Entity.Null));
        }

        [Test]
        public void APoseAfterDespawnDoesNotRecreateTheMirror()
        {
            Spawn("r1");
            State("r1");
            Tick();

            ((IEntityView)_view).Despawn("r1");
            Tick();

            Pose("r1", facing: 1u, action: SimAction.Idle, seq: 1u);
            Tick();

            Assert.That(Find("r1"), Is.EqualTo(Entity.Null));
        }

        /// <summary>
        /// A repeated attack changes nothing else about the entity. If the counter were dropped
        /// anywhere on this path, this case would be indistinguishable from a single attack.
        /// </summary>
        [Test]
        public void ARepeatedActionChangesOnlyTheCounter()
        {
            Spawn("r1");
            State("r1");
            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 5u);
            Tick();
            var first = _entityManager.GetComponentData<EntityPose>(Find("r1"));

            State("r1");
            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 6u);
            Tick();
            var second = _entityManager.GetComponentData<EntityPose>(Find("r1"));

            Assert.That(second.Action, Is.EqualTo(first.Action));
            Assert.That(second.FacingBrad, Is.EqualTo(first.FacingBrad));
            Assert.That(second.ActionSeq, Is.Not.EqualTo(first.ActionSeq));
        }

        // ── The animation seam ───────────────────────────────────────────────────

        /// <summary>
        /// Records what the seam reported, so a test can assert on the calls rather than on a
        /// component's internal state.
        /// </summary>
        private sealed class RecordingReceiver : MonoBehaviour, IEntityAnimationReceiver
        {
            public readonly List<(SimAction Action, bool Retriggered)> Calls =
                new List<(SimAction, bool)>();

            public void OnAction(SimAction action, bool retriggered) => Calls.Add((action, retriggered));
        }

        /// <summary>
        /// Attaches a recorder to the GameObject standing in for <paramref name="id"/>.
        /// </summary>
        private RecordingReceiver AttachReceiver(string id)
        {
            var entity = Find(id);
            Assert.That(entity, Is.Not.EqualTo(Entity.Null));
            Assert.That(_entityManager.HasComponent<EntityViewLink>(entity), Is.True,
                "no view was ever spawned for this entity, so there is nothing for the seam to talk to");

            var go = _registry.Get(_entityManager.GetComponentData<EntityViewLink>(entity).ViewId);
            Assert.That(go, Is.Not.Null);
            return go.AddComponent<RecordingReceiver>();
        }

        /// <summary>
        /// The test that was missing, and whose absence let a built player ship with the seam
        /// never firing. Everything upstream — the pose on the mirror, the counter, the event
        /// buffer — was asserted; that the receiver is actually CALLED was not.
        /// </summary>
        [Test]
        public void TheReceiverIsToldAboutANewAction()
        {
            Spawn("r1");
            State("r1");
            Tick();

            var receiver = AttachReceiver("r1");

            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 1u);
            Tick();

            Assert.That(receiver.Calls, Has.Count.EqualTo(1));
            Assert.That(receiver.Calls[0].Action, Is.EqualTo(SimAction.Attacking));
            // False on a NEW action: that is a transition, and a state machine driven by the
            // action plays it anyway. True here would make every implementation double-trigger.
            Assert.That(receiver.Calls[0].Retriggered, Is.False);
        }

        [Test]
        public void ARepeatedActionIsReportedAsARetrigger()
        {
            Spawn("r1");
            State("r1");
            Tick();
            var receiver = AttachReceiver("r1");

            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 1u);
            Tick();
            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 2u);
            Tick();

            Assert.That(receiver.Calls, Has.Count.EqualTo(2));
            Assert.That(receiver.Calls[1].Retriggered, Is.True,
                "the second swing is a new occurrence even though the action did not change");
        }

        /// <summary>
        /// The mirror-image failure: a continuous state must not retrigger on every frame it is
        /// resent. A walking entity is posed on every snapshot.
        /// </summary>
        [Test]
        public void AnUnchangedActionIsNotReportedAgain()
        {
            Spawn("r1");
            State("r1");
            Tick();
            var receiver = AttachReceiver("r1");

            Pose("r1", facing: 1u, action: SimAction.Moving, seq: 4u);
            Tick();
            Pose("r1", facing: 1u, action: SimAction.Moving, seq: 4u);
            Tick();
            Pose("r1", facing: 1u, action: SimAction.Moving, seq: 4u);
            Tick();

            Assert.That(receiver.Calls, Has.Count.EqualTo(1),
                "a walk cycle retriggered once per snapshot is the failure this counter exists to avoid");
        }

        /// <summary>
        /// Zero means the server sends no counter. Treating it as an edge would retrigger every
        /// animation on every snapshot from an older server.
        /// </summary>
        [Test]
        public void AZeroCounterNeverRetriggers()
        {
            Spawn("r1");
            State("r1");
            Tick();
            var receiver = AttachReceiver("r1");

            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 0u);
            Tick();
            Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 0u);
            Tick();

            // One call for entering the action, and nothing after it.
            Assert.That(receiver.Calls, Has.Count.EqualTo(1));
            Assert.That(receiver.Calls[0].Retriggered, Is.False);
        }

        /// <summary>
        /// Warm only once <see cref="Warm"/> is called, and every instance it hands out already
        /// carries a <see cref="RecordingReceiver"/> — the way a real prefab would.
        /// </summary>
        /// <remarks>
        /// <see cref="StubViewAssetProvider"/> is always warm, so a view appears in the same frame
        /// the entity does and the deferral path is never exercised by it. That is why the defect
        /// this fixture reproduces was invisible to every existing test.
        /// </remarks>
        private sealed class ColdViewAssetProvider : Cuvara.DOTS.Provisioning.IViewAssetProvider
        {
            private readonly HashSet<string> _warm = new HashSet<string>();
            public readonly List<RecordingReceiver> Handed = new List<RecordingReceiver>();

            public void Warm(string key) => _warm.Add(key);

            public System.Threading.Tasks.Task PrewarmAsync(
                string key, int count, System.Threading.CancellationToken cancellationToken = default)
            {
                Warm(key);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public bool IsWarm(string key) => key != null && _warm.Contains(key);

            public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
            {
                var instance = new GameObject(key);
                instance.transform.SetPositionAndRotation(position, rotation);
                if (parent != null) instance.transform.SetParent(parent, true);
                var receiver = instance.AddComponent<RecordingReceiver>();
                Handed.Add(receiver);
                return instance;
            }

            public System.Threading.Tasks.Task<GameObject> AcquireAsync(
                string key, Vector3 position, Quaternion rotation, Transform parent = null,
                System.Threading.CancellationToken cancellationToken = default)
                => System.Threading.Tasks.Task.FromResult(Acquire(key, position, rotation, parent));

            public void ReleaseInstance(GameObject instance)
            {
                if (instance != null) Object.DestroyImmediate(instance);
            }

            public void Release(string key) => _warm.Remove(key);
        }

        /// <summary>
        /// An action that happens while the view is still cold is reported once the view arrives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A CHARACTERISATION test, not a regression test — it passes before and after the change
        /// that prompted it, and that is the finding. The system's query requires
        /// <c>EntityViewLink</c>, so an entity whose asset is still loading is not in the loop at
        /// all and its action cannot be consumed; the guarantee comes from the query filter, which
        /// nothing had written down.
        /// </para>
        /// <para>
        /// It exists because a built player showed 9 swings played against 10 sent and that was
        /// first diagnosed HERE, in the package. It was wrong: the miss was the sample attaching
        /// its receiver lazily, one frame after the first swing. This fixture is what proved the
        /// package innocent, and it is kept so the next person does not have to re-derive it —
        /// <see cref="StubViewAssetProvider"/> is always warm and cannot exercise deferral at all.
        /// </para>
        /// </remarks>
        [Test]
        public void AnActionWhileTheViewIsStillColdIsReportedOnceItArrives()
        {
            // A world of its own: the shared fixture's provider is always warm and cannot defer.
            using var world = new World("Cuvara.DOTS.ColdViewTest");
            var provider = new ColdViewAssetProvider();
            var registry = new EntityViewRegistry(provider);
            DotsViewBootstrap.Install(world, registry);

            var config = ScriptableObject.CreateInstance<ViewConfig>();
            config.Configure(PlayerType);
            var library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            library.Configure(new ViewArchetypeLibrary.Entry { Name = RemoteArchetype, Config = config });
            var catalog = new ViewConfigCatalog();
            catalog.Build(library);
            catalog.Install(world);

            var view = new DotsEntityView(
                catalog,
                new TypeArchetypeResolver(RemoteArchetype, null, new TypeArchetypeResolver.Rule(PlayerType, RemoteArchetype)),
                SnapshotSpaceMapping.XZPlane);
            DotsNetcodeBootstrap.Install(world, view);

            void Step()
            {
                world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
                world.GetExistingSystemManaged<ViewSystemGroup>().Update();
            }

            try
            {
                ((IEntityView)view).Spawn("r1", isLocal: false, type: PlayerType);
                ((IEntityView)view).SetState("r1", 0f, 0f, 100, 100);
                ((IEntityPoseView)view).SetPose("r1", 1u, SimAction.Attacking, 1u);

                // Cold: the mirror exists, the view does not.
                Step();
                Assert.That(provider.Handed, Is.Empty, "the provider was warm after all — this fixture proves nothing");

                // The asset lands. Nothing new arrives from the server.
                provider.Warm(PlayerType);
                Step();
                Step();

                Assert.That(provider.Handed, Has.Count.EqualTo(1), "no view was ever provisioned");
                Assert.That(provider.Handed[0].Calls, Has.Count.EqualTo(1),
                    "the action was consumed while no view existed to show it to");
                Assert.That(provider.Handed[0].Calls[0].Action, Is.EqualTo(SimAction.Attacking));
            }
            finally
            {
                DotsNetcodeBootstrap.Uninstall(world);
                catalog.Dispose();
                Object.DestroyImmediate(library);
                Object.DestroyImmediate(config);
                DotsViewBootstrap.Uninstall(world);
            }
        }

        [Test]
        public void AViewWithNoReceiverIsHarmless()
        {
            Spawn("r1");
            State("r1");
            Tick();

            Assert.DoesNotThrow(() =>
            {
                Pose("r1", facing: 1u, action: SimAction.Attacking, seq: 1u);
                Tick();
            });
        }

        // ── Game events ──────────────────────────────────────────────────────────

        private static ResolvedGameEvent Damage(string source, string target, int amount = 25) =>
            new ResolvedGameEvent(GameEventType.Damage, source, target, amount, 0, GameEventFlags.None);

        private DynamicBuffer<NetworkGameEvent> Events() =>
            _entityManager.GetBuffer<NetworkGameEvent>(_viewEntity);

        [Test]
        public void TheBufferExistsFromInstallRatherThanFromTheFirstEvent()
        {
            // "No events yet" and "events not installed" must not be the same observation.
            Assert.That(_entityManager.HasBuffer<NetworkGameEvent>(_viewEntity), Is.True);
            Assert.That(Events().Length, Is.Zero);
        }

        [Test]
        public void AnEventLandsOnTheBufferWithBothParticipantsResolved()
        {
            Spawn("attacker");
            Spawn("victim");
            State("attacker");
            State("victim");
            _view.EnqueueGameEvents(new List<ResolvedGameEvent> { Damage("attacker", "victim") });
            Tick();

            Assert.That(Events().Length, Is.EqualTo(1));
            var e = Events()[0];
            Assert.That(e.Type, Is.EqualTo(GameEventType.Damage));
            Assert.That(e.Amount, Is.EqualTo(25));
            Assert.That(e.Source, Is.EqualTo(Find("attacker")));
            Assert.That(e.Target, Is.EqualTo(Find("victim")));
        }

        /// <summary>
        /// The ordinary case, not an error: the spawn command and the event arrive in the same
        /// drain. Resolving events before the commands would leave this participant null while
        /// the mirror existed one line later.
        /// </summary>
        [Test]
        public void AnEventNamingAnEntitySpawnedInTheSameDrainResolves()
        {
            Spawn("fresh");
            State("fresh");
            _view.EnqueueGameEvents(new List<ResolvedGameEvent> { Damage("fresh", "fresh") });
            Tick();

            Assert.That(Events().Length, Is.EqualTo(1));
            Assert.That(Events()[0].Target, Is.EqualTo(Find("fresh")));
        }

        /// <summary>
        /// A damage number with no visible attacker is still the number a player needs — the
        /// alternative is a health bar that drops with no explanation.
        /// </summary>
        [Test]
        public void AnUnknownParticipantIsNullButTheEventStillArrives()
        {
            Spawn("victim");
            State("victim");
            _view.EnqueueGameEvents(new List<ResolvedGameEvent> { Damage("someone-outside-aoi", "victim") });
            Tick();

            Assert.That(Events().Length, Is.EqualTo(1));
            var e = Events()[0];
            Assert.That(e.HasSource, Is.False);
            Assert.That(e.HasTarget, Is.True);
            // The id is still carried, so a consumer can log or attribute it even with no mirror.
            Assert.That(e.SourceId.ToString(), Is.EqualTo("someone-outside-aoi"));
        }

        /// <summary>
        /// One frame's worth and no more. A consumer that read a stale buffer would show a
        /// player the same hit twice.
        /// </summary>
        [Test]
        public void TheBufferIsClearedEveryDrain()
        {
            Spawn("victim");
            State("victim");
            _view.EnqueueGameEvents(new List<ResolvedGameEvent> { Damage("victim", "victim") });
            Tick();
            Assert.That(Events().Length, Is.EqualTo(1));

            Tick();

            Assert.That(Events().Length, Is.Zero,
                "events are not state; a keyframe restates the world, never its history");
        }

        [Test]
        public void EventsArriveInTheOrderTheSimulationProducedThem()
        {
            Spawn("a");
            Spawn("b");
            State("a");
            State("b");
            _view.EnqueueGameEvents(new List<ResolvedGameEvent>
            {
                Damage("a", "b", 1),
                Damage("a", "b", 2),
                Damage("a", "b", 3),
            });
            Tick();

            Assert.That(Events().Length, Is.EqualTo(3));
            Assert.That(Events()[0].Amount, Is.EqualTo(1));
            Assert.That(Events()[1].Amount, Is.EqualTo(2));
            Assert.That(Events()[2].Amount, Is.EqualTo(3));
        }

        /// <summary>
        /// A stalled main thread must not accumulate events without limit, and the OLDEST are
        /// the right ones to lose: a client catching up needs the recent world.
        /// </summary>
        [Test]
        public void TheQueueIsBoundedAndDropsTheOldest()
        {
            Spawn("victim");
            State("victim");

            var many = new List<ResolvedGameEvent>();
            for (var i = 0; i < DotsEntityView.MaxQueuedEvents + 10; i++)
            {
                many.Add(Damage("victim", "victim", i));
            }

            _view.EnqueueGameEvents(many);
            Tick();

            Assert.That(Events().Length, Is.EqualTo(DotsEntityView.MaxQueuedEvents));
            Assert.That(_view.EventsDropped, Is.EqualTo(10));
            // The first survivor is the 11th produced, not the 1st.
            Assert.That(Events()[0].Amount, Is.EqualTo(10));
        }

        [Test]
        public void EnqueueingNullIsIgnoredRatherThanThrowingInsideTheDrain()
        {
            Assert.DoesNotThrow(() => _view.EnqueueGameEvents(null));
            Tick();
            Assert.That(Events().Length, Is.Zero);
        }
    }
}
