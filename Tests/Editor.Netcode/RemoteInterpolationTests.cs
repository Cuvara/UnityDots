using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// Remote entities rendered from their buffered states rather than teleported to the newest one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven through the <b>public groups</b> — <c>NetcodeSystemGroup</c> then
    /// <c>ViewSystemGroup</c> — for the reason <c>NetworkEntityViewTests</c> states: that pair is
    /// the package's ordering contract, and a test naming systems would pass while the contract
    /// broke. Here it also earns its keep twice over, because the whole point of stage 4 is that the
    /// two halves land in different <i>phases</i> of the frame.
    /// </para>
    /// <para>
    /// <b>Time is set explicitly rather than left to the player loop.</b> A world updated group by
    /// group never advances <c>SystemAPI.Time</c>, so the render clock would sit still and every
    /// assertion about motion would be an assertion about nothing. <see cref="Frame"/> stamps a
    /// delta before it updates, which is also what makes these tests deterministic — the same
    /// property netcode's <c>IViewClock</c> exists to give its own suite.
    /// </para>
    /// <para>
    /// <b>Nothing here asserts an interpolated coordinate against a hand-computed one.</b> The
    /// arithmetic is netcode's, it is covered by that package's own 24 core tests, and duplicating a
    /// number here would only pin this suite to an implementation it does not own. What these assert
    /// is what this package is responsible for: which entities are interpolated, which are not, that
    /// the samples reach the buffer, that the rendered position is on the buffered path rather than
    /// at its newest end, and that the degenerate buffers do not throw.
    /// </para>
    /// </remarks>
    public sealed class RemoteInterpolationTests
    {
        private const string Archetype = "player-remote";
        private const string PlayerType = "player";

        /// <summary>The 15 Hz world rate this package is tuned for, as netcode's default assumes.</summary>
        private const double SecondsPerSnapshot = 1.0 / 15.0;

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private double _elapsed;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.RemoteInterpolationTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");

            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = Archetype, Config = _config });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(Archetype, null, new TypeArchetypeResolver.Rule(PlayerType, Archetype)),
                SnapshotSpaceMapping.XZPlane);

            DotsNetcodeBootstrap.Install(_world, _view);
        }

        [TearDown]
        public void TearDown()
        {
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            Object.DestroyImmediate(_library);
            Object.DestroyImmediate(_config);
            DotsViewBootstrap.Uninstall(_world);
            _world.Dispose();
        }

        /// <summary>One rendered frame: stamp the delta, drain the wire, then present.</summary>
        private void Frame(float delta = 1f / 60f)
        {
            _elapsed += delta;
            _world.SetTime(new TimeData(_elapsed, delta));
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
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted))
                {
                    return entities[i];
                }
            }

            return Entity.Null;
        }

        private float3 PositionOf(string id) =>
            _entityManager.GetComponentData<LocalTransform>(Find(id)).Position;

        private int SampleCountOf(string id) =>
            _entityManager.GetBuffer<SnapshotSample>(Find(id)).Length;

        /// <summary>
        /// A steady 15 Hz stream walking +1 along the server's x, starting at 3 so that neither the
        /// oldest sample, the newest, nor the spawn origin is the same number as any other.
        /// </summary>
        private void FeedLine(string id, int ticks)
        {
            for (var i = 1; i <= ticks; i++)
            {
                _view.SetStateAtTick(id, 2f + i, 0f, 100, 100, i, (i - 1) * SecondsPerSnapshot);
            }
        }

        private void Spawn(string id) => ((IEntityView)_view).Spawn(id, isLocal: false, type: PlayerType);

        [Test]
        public void ATickedState_IsBuffered_AndTheEntityIsRenderedFromTheBufferNotFromTheNewestState()
        {
            // The producer half. Before this change the drain wrote every state straight to the
            // transform, so an entity sat at the newest server position and jumped to the next one
            // 66 ms later — motion at the snapshot rate however fast the client drew.
            Spawn("uuid-r");
            FeedLine("uuid-r", 8);

            Frame();

            Assert.AreEqual(8, SampleCountOf("uuid-r"), "every ticked state reached the buffer");

            var x = PositionOf("uuid-r").x;
            Assert.AreNotEqual(10f, x, "the newest state is not what is drawn — that is the teleport");
            Assert.AreNotEqual(0f, x, "and the entity did not stay at the spawn origin either");

            // The render clock deliberately starts a full TargetDelay behind the first tick, so the
            // first frame of a join renders the oldest retained state. That is the jitter buffer
            // filling, and it is the behaviour netcode's core documents rather than an accident.
            Assert.AreEqual(3f, x, 1e-3f);
        }

        [Test]
        public void TheRenderedPosition_MovesForwardEveryFrame_AndNeverPassesTheNewestState()
        {
            // The consumer half, and the property the whole core exists for: a monotonic clock along
            // a fixed path is a monotonic rendered position. A backward step here is the
            // rubber-banding the GameObject path used to show.
            Spawn("uuid-r");
            FeedLine("uuid-r", 8);
            Frame();

            var previous = PositionOf("uuid-r").x;

            for (var i = 0; i < 20; i++)
            {
                Frame();

                var current = PositionOf("uuid-r").x;
                Assert.GreaterOrEqual(current, previous, $"the entity stepped backwards on frame {i}");
                previous = current;
            }

            Assert.Greater(previous, 3f, "it left the oldest sample");
            Assert.Less(previous, 10f, "and it never ran past the newest state the server sent");
        }

        [Test]
        public void APredictedEntity_IsNotInterpolated_AndItsSamplesAreStillKept()
        {
            // The one-writer rule. A predictor claims LocalTransform by adding the tag, and both the
            // drain and the interpolation job must then leave it alone — the failure otherwise is
            // intermittent, local-player-only, and reads as a prediction bug.
            Spawn("uuid-p");
            Frame();

            var entity = Find("uuid-p");
            _entityManager.AddComponent<PredictedTransform>(entity);

            var claimed = new float3(42f, 0f, 42f);
            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(claimed));

            FeedLine("uuid-p", 8);
            for (var i = 0; i < 10; i++) Frame();

            Assert.AreEqual(claimed, PositionOf("uuid-p"), "something else owns this transform");

            // Kept, not skipped: releasing the tag mid-session must hand interpolation a history to
            // render from rather than a cold buffer, and a uniform component set is what keeps every
            // mirror in one archetype.
            Assert.AreEqual(8, SampleCountOf("uuid-p"));
        }

        [Test]
        public void AnUntickedState_IsStillWrittenStraightToTheTransform()
        {
            // The 0.23.1 path, unchanged. IEntityView carries no tick, so a consumer driving the
            // adapter through WorldViewBinder is handing over positions that were already smoothed
            // on the netcode side; buffering those would smooth them twice and double the render
            // delay, silently. An empty sample buffer is what keeps the two paths exclusive.
            Spawn("uuid-u");
            ((IEntityView)_view).SetState("uuid-u", 5f, 6f, 100, 100);

            Frame();

            Assert.AreEqual(new float3(5f, 0f, 6f), PositionOf("uuid-u"));
            Assert.AreEqual(0, SampleCountOf("uuid-u"), "nothing was buffered, so nothing interpolates it");
        }

        [Test]
        public void AnEntityWithNoSamplesAtAll_IsLeftWhereTheDrainPutIt()
        {
            // Spawned and never stated — the window between an area-of-interest entry and its first
            // snapshot. An empty buffer is a legal state, and the job must pass over it rather than
            // draw the entity at the origin or throw.
            Spawn("uuid-e");

            for (var i = 0; i < 5; i++) Frame();

            Assert.AreEqual(float3.zero, PositionOf("uuid-e"));
            Assert.AreEqual(0, SampleCountOf("uuid-e"));
            Assert.IsFalse(_entityManager.GetComponentData<InterpolationState>(Find("uuid-e")).HasRendered);
        }

        [Test]
        public void ASingleSample_IsHeldAtRatherThanExtrapolatedFrom()
        {
            // One sample is the other degenerate buffer, and it is the ordinary state of every
            // entity for one snapshot interval after it enters the area of interest. There is no
            // history to have come from, so holding is the only honest answer.
            Spawn("uuid-s");
            _view.SetStateAtTick("uuid-s", 2f, 3f, 100, 100, tick: 5, receiveTimeSeconds: 0.0);

            for (var i = 0; i < 5; i++) Frame();

            Assert.AreEqual(1, SampleCountOf("uuid-s"));
            Assert.AreEqual(new float3(2f, 0f, 3f), PositionOf("uuid-s"));
            Assert.IsTrue(_entityManager.GetComponentData<InterpolationState>(Find("uuid-s")).HasRendered);
        }

        [Test]
        public void ARepeatedTick_IsRefused_AndDoesNotFallBackToADirectWrite()
        {
            // The admission rule is shared with the GameObject path's ring precisely because the
            // evaluator's bracketing assumes strictly increasing ticks. The second half of the
            // assertion is the subtler one: a refused sample must NOT make the drain write the
            // transform itself, or a duplicate would snap an interpolated entity.
            Spawn("uuid-d");
            _view.SetStateAtTick("uuid-d", 2f, 3f, 100, 100, tick: 5, receiveTimeSeconds: 0.0);
            Frame();

            _view.SetStateAtTick("uuid-d", 99f, 99f, 100, 100, tick: 5, receiveTimeSeconds: SecondsPerSnapshot);
            Frame();

            Assert.AreEqual(1, SampleCountOf("uuid-d"), "the duplicate tick was refused");
            Assert.AreEqual(new float3(2f, 0f, 3f), PositionOf("uuid-d"));
        }

        [Test]
        public void TheRenderClock_AdvancesOnlyOnceAFrame_AndOnlyAfterTheFirstSnapshot()
        {
            // Nothing renders before the first snapshot, and the clock must not drift forward in the
            // meantime — a clock that ran while the buffer was empty would arrive already ahead of
            // the first sample and skip the fill.
            Spawn("uuid-c");
            for (var i = 0; i < 5; i++) Frame();

            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<InterpolationTimeline>());
            Assert.IsFalse(query.GetSingleton<InterpolationTimeline>().Clock.HasSamples);

            FeedLine("uuid-c", 4);
            Frame();

            var clock = query.GetSingleton<InterpolationTimeline>().Clock;
            Assert.IsTrue(clock.HasSamples);
            Assert.AreEqual(4L, clock.NewestTick, "the newest tick heard from, on any entity");
        }
    }
}
