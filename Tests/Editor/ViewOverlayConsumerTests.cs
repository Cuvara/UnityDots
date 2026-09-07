using System.Collections.Generic;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The overlay consumer contract: the buffer never goes stale, the projection rule hides
    /// behind-camera and too-far anchors, and the reconciler acquires/places/hides/releases exactly
    /// once per transition, keyed by entity.
    /// </summary>
    public sealed class ViewOverlayConsumerTests
    {
        private World _world;
        private SpawningViewAssetProvider _provider;
        private EntityViewRegistry _registry;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _world = new World("ViewOverlayConsumerTests");
            _provider = new SpawningViewAssetProvider();
            _registry = new EntityViewRegistry(_provider);
            DotsViewBootstrap.Install(_world, _registry);
        }

        [TearDown]
        public void TearDown()
        {
            if (_camera != null) Object.DestroyImmediate(_camera.gameObject);
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }
        }

        private void Tick() => _world.GetExistingSystemManaged<ViewSystemGroup>().Update();

        private Entity Anchored(float3 at, float3 offset = default)
        {
            var entityManager = _world.EntityManager;
            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, LocalTransform.FromPosition(at));
            entityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(at) });
            entityManager.AddComponentData(entity, new EntityViewRequest { ViewKey = "goblin" });
            entityManager.AddComponentData(entity, new ViewOverlayAnchor { WorldOffset = offset });
            return entity;
        }

        private ViewOverlayBuffer Buffer()
        {
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewOverlayBuffer>());
            return query.IsEmpty ? null : _world.EntityManager.GetComponentObject<ViewOverlayBuffer>(query.GetSingletonEntity());
        }

        private Camera CameraAt(float3 position, float3 lookAt)
        {
            var go = new GameObject("ViewOverlayConsumerTests.Camera");
            _camera = go.AddComponent<Camera>();
            _camera.transform.position = position;
            _camera.transform.LookAt((Vector3)lookAt);
            return _camera;
        }

        // ---- producer --------------------------------------------------------------------------

        [Test]
        public void Spawn_Move_Despawn_Empty_KeepTheBufferConsistent()
        {
            var a = Anchored(new float3(1f, 0f, 1f), new float3(0f, 2f, 0f));
            var b = Anchored(new float3(5f, 0f, 5f));
            Tick();

            var buffer = Buffer();
            Assert.IsNotNull(buffer);
            Assert.AreEqual(2, buffer.Count);
            var entryA = Find(buffer, a);
            Assert.AreEqual(new float3(1f, 2f, 1f), entryA.WorldPosition, "anchor offset applied");
            Assert.AreEqual(_world.EntityManager.GetComponentData<EntityViewLink>(a).ViewId, entryA.ViewId);

            _world.EntityManager.SetComponentData(a, new LocalToWorld { Value = float4x4.Translate(new float3(9f, 0f, 9f)) });
            Tick();
            Assert.AreEqual(new float3(9f, 2f, 9f), Find(buffer, a).WorldPosition);

            _world.EntityManager.DestroyEntity(a);
            Tick();
            Assert.AreEqual(1, buffer.Count);
            Assert.AreEqual(b, buffer.Entries[0].Entity);

            // The regression: with the last anchored entity gone the system used to stop updating
            // and leave this entry behind.
            var version = buffer.Version;
            _world.EntityManager.DestroyEntity(b);
            Tick();
            Assert.AreEqual(0, buffer.Count, "no stale name plate");
            Assert.Greater(buffer.Version, version);
        }

        [Test]
        public void ViewRecycled_KeepsTheEntity_ChangesTheViewId()
        {
            var a = Anchored(float3.zero);
            Tick();
            var buffer = Buffer();
            var firstViewId = Find(buffer, a).ViewId;

            // Hand the view back and let it re-acquire: same entity, new handle.
            DotsViewBootstrap.Uninstall(_world);
            DotsViewBootstrap.Install(_world, _registry);
            Tick();

            var entry = Find(buffer, a);
            Assert.AreEqual(a, entry.Entity);
            Assert.AreNotEqual(firstViewId, entry.ViewId, "handles are never reused; the entity is the stable key");
        }

        private static ViewOverlayData Find(ViewOverlayBuffer buffer, Entity entity)
        {
            for (var i = 0; i < buffer.Entries.Length; i++)
            {
                if (buffer.Entries[i].Entity == entity) return buffer.Entries[i];
            }

            Assert.Fail($"{entity} is not in the overlay buffer");
            return default;
        }

        // ---- projection ------------------------------------------------------------------------

        [Test]
        public void Projection_InFront_IsVisible_WithScreenCoordinatesAndDistance()
        {
            var camera = CameraAt(new float3(0f, 0f, -10f), float3.zero);

            var placement = ViewOverlayProjection.Project(camera, float3.zero);

            Assert.AreEqual(ViewOverlayVisibility.Visible, placement.Visibility);
            Assert.IsTrue(placement.IsVisible);
            Assert.AreEqual(10f, placement.Distance, 1e-4f);
            Assert.AreEqual(camera.pixelWidth * 0.5f, placement.Screen.x, 1f, "dead centre");
            Assert.AreEqual(camera.pixelHeight * 0.5f, placement.Screen.y, 1f);
        }

        [Test]
        public void Projection_BehindCamera_IsHidden_NotMirrored()
        {
            var camera = CameraAt(new float3(0f, 0f, -10f), float3.zero);

            var placement = ViewOverlayProjection.Project(camera, new float3(3f, 0f, -20f));

            Assert.AreEqual(ViewOverlayVisibility.BehindCamera, placement.Visibility);
            Assert.IsFalse(placement.IsVisible);
            Assert.AreEqual(math.distance(new float3(0f, 0f, -10f), new float3(3f, 0f, -20f)), placement.Distance, 1e-4f);
        }

        [Test]
        public void Projection_TooFar_IsHidden_AndZeroDisablesTheFilter()
        {
            var camera = CameraAt(new float3(0f, 0f, -10f), float3.zero);

            Assert.AreEqual(ViewOverlayVisibility.TooFar, ViewOverlayProjection.Project(camera, new float3(0f, 0f, 100f), maxDistance: 50f).Visibility);
            Assert.AreEqual(ViewOverlayVisibility.Visible, ViewOverlayProjection.Project(camera, new float3(0f, 0f, 100f), maxDistance: 0f).Visibility);
            Assert.AreEqual(ViewOverlayVisibility.Visible, ViewOverlayProjection.Project(camera, new float3(0f, 0f, 100f), maxDistance: 200f).Visibility);
        }

        [Test]
        public void Projection_OffScreenEdge_IsStillVisible_SoPlatesSlideOffRatherThanPop()
        {
            var camera = CameraAt(new float3(0f, 0f, -10f), float3.zero);

            var placement = ViewOverlayProjection.Project(camera, new float3(500f, 0f, 0f));

            Assert.AreEqual(ViewOverlayVisibility.Visible, placement.Visibility);
            Assert.Greater(placement.Screen.x, camera.pixelWidth);
        }

        [Test]
        public void Projection_NoCamera_ReportsNoCamera()
        {
            Assert.AreEqual(ViewOverlayVisibility.NoCamera, ViewOverlayProjection.Project(null, float3.zero).Visibility);
        }

        // ---- reconciler ------------------------------------------------------------------------

        private sealed class RecordingPresenter : IViewOverlayPresenter<string>
        {
            public readonly List<string> Log = new List<string>();
            public readonly Dictionary<Entity, string> Elements = new Dictionary<Entity, string>();
            private int _next;

            public string Acquire(in ViewOverlayData data)
            {
                var element = "el" + _next++;
                Elements[data.Entity] = element;
                Log.Add("acquire:" + element);
                return element;
            }

            public void Place(string element, in ViewOverlayData data, in ViewOverlayPlacement placement) => Log.Add("place:" + element);

            public void Hide(string element, in ViewOverlayData data, in ViewOverlayPlacement placement) => Log.Add("hide:" + element + ":" + placement.Visibility);

            public void Release(string element) => Log.Add("release:" + element);
        }

        [Test]
        public void Reconciler_AcquiresOnce_PlacesEachFrame_ReleasesWhenTheEntityLeaves()
        {
            var camera = CameraAt(new float3(0f, 5f, -10f), float3.zero);
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);

            var a = Anchored(float3.zero);
            Tick();
            Assert.IsTrue(reconciler.Sync(Buffer(), camera));
            CollectionAssert.AreEqual(new[] { "acquire:el0", "place:el0" }, presenter.Log);
            Assert.AreEqual(1, reconciler.ElementCount);
            Assert.AreEqual(1, reconciler.VisibleCount);

            presenter.Log.Clear();
            Tick();
            reconciler.Sync(Buffer(), camera);
            CollectionAssert.AreEqual(new[] { "place:el0" }, presenter.Log, "no re-acquire for a known entity");

            presenter.Log.Clear();
            _world.EntityManager.DestroyEntity(a);
            Tick();
            reconciler.Sync(Buffer(), camera);
            CollectionAssert.AreEqual(new[] { "release:el0" }, presenter.Log);
            Assert.AreEqual(0, reconciler.ElementCount);
        }

        [Test]
        public void Reconciler_IsVersionGated_SoRepeatedSyncsInOneFrameDoNothing()
        {
            var camera = CameraAt(new float3(0f, 5f, -10f), float3.zero);
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);
            Anchored(float3.zero);
            Tick();

            Assert.IsTrue(reconciler.Sync(Buffer(), camera));
            Assert.IsFalse(reconciler.Sync(Buffer(), camera), "same buffer version: OnGUI may call this several times");
            Assert.IsFalse(reconciler.Sync(Buffer(), camera));
            Assert.AreEqual(2, presenter.Log.Count);
        }

        [Test]
        public void Reconciler_HidesBehindCameraAndTooFar_KeepingTheElement()
        {
            var camera = CameraAt(new float3(0f, 0f, -10f), float3.zero);
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);

            var behind = Anchored(new float3(0f, 0f, -30f));
            var far = Anchored(new float3(0f, 0f, 500f));
            var near = Anchored(new float3(0f, 0f, 5f));
            Tick();
            reconciler.Sync(Buffer(), camera, maxDistance: 100f);

            Assert.AreEqual(3, reconciler.ElementCount, "hidden entries keep their element");
            Assert.AreEqual(1, reconciler.VisibleCount);
            Assert.AreEqual(2, reconciler.HiddenCount);
            CollectionAssert.Contains(presenter.Log, "hide:" + presenter.Elements[behind] + ":BehindCamera");
            CollectionAssert.Contains(presenter.Log, "hide:" + presenter.Elements[far] + ":TooFar");
            CollectionAssert.Contains(presenter.Log, "place:" + presenter.Elements[near]);
            CollectionAssert.DoesNotContain(presenter.Log, "release:" + presenter.Elements[behind]);
        }

        [Test]
        public void Reconciler_KeysByEntity_NotViewId()
        {
            var camera = CameraAt(new float3(0f, 5f, -10f), float3.zero);
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);
            var a = Anchored(float3.zero);
            Tick();
            reconciler.Sync(Buffer(), camera);

            // Same entity, new view handle.
            DotsViewBootstrap.Uninstall(_world);
            DotsViewBootstrap.Install(_world, _registry);
            Tick();
            presenter.Log.Clear();
            reconciler.Sync(Buffer(), camera);

            CollectionAssert.DoesNotContain(presenter.Log, "acquire:el1");
            CollectionAssert.DoesNotContain(presenter.Log, "release:el0");
            Assert.AreEqual(1, reconciler.ElementCount);
            Assert.AreEqual(a, new List<Entity>(presenter.Elements.Keys)[0]);
        }

        [Test]
        public void Reconciler_ReleasedBuffer_ReleasesEverything_AndClearIsIdempotent()
        {
            var camera = CameraAt(new float3(0f, 5f, -10f), float3.zero);
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);
            Anchored(float3.zero);
            Anchored(new float3(1f, 0f, 0f));
            Tick();
            reconciler.Sync(Buffer(), camera);
            Assert.AreEqual(2, reconciler.ElementCount);

            presenter.Log.Clear();
            Assert.IsFalse(reconciler.Sync(null, camera), "module gone: nothing to reconcile against");
            Assert.AreEqual(2, presenter.Log.FindAll(l => l.StartsWith("release:")).Count);
            Assert.AreEqual(0, reconciler.ElementCount);

            reconciler.Clear();
            Assert.AreEqual(2, presenter.Log.Count, "nothing left to release");
        }

        [Test]
        public void Reconciler_NoCamera_HidesEverything_WithoutThrowing()
        {
            var presenter = new RecordingPresenter();
            var reconciler = new ViewOverlayReconciler<string>(presenter);
            Anchored(float3.zero);
            Tick();

            Assert.DoesNotThrow(() => reconciler.Sync(Buffer(), null));
            Assert.AreEqual(0, reconciler.VisibleCount);
            Assert.AreEqual(1, reconciler.HiddenCount);
            CollectionAssert.Contains(presenter.Log, "hide:el0:NoCamera");
        }
    }
}
