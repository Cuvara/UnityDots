using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Tests for <see cref="EntityViewRegistry.SweepDestroyed"/> — the cleanup of views whose
    /// GameObject was destroyed externally (scene unload, manual Destroy, editor reset).
    /// </summary>
    public sealed class SweepDestroyedTests
    {
        private FakeViewProvider _provider;
        private EntityViewRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _provider = new FakeViewProvider();
            _registry = new EntityViewRegistry(_provider);
        }

        [TearDown]
        public void TearDown()
        {
            _registry.Clear();
        }

        [Test]
        public void SweepDestroyed_NoViews_ReturnsZero()
        {
            Assert.AreEqual(0, _registry.SweepDestroyed());
        }

        [Test]
        public void SweepDestroyed_AllHealthy_ReturnsZero()
        {
            _provider.WarmKey("goblin");
            _registry.Spawn("goblin", Vector3.zero);
            _registry.Spawn("goblin", Vector3.one);

            Assert.AreEqual(2, _registry.TotalViews);
            Assert.AreEqual(0, _registry.SweepDestroyed());
            Assert.AreEqual(2, _registry.TotalViews);
        }

        [Test]
        public void SweepDestroyed_DestroyedGO_RemovesStaleEntry()
        {
            _provider.WarmKey("goblin");
            var viewId = _registry.Spawn("goblin", Vector3.zero);
            Assert.AreEqual(1, _registry.TotalViews);

            // Simulate external destruction
            var go = _registry.Get(viewId);
            Object.DestroyImmediate(go);

            var swept = _registry.SweepDestroyed();
            Assert.AreEqual(1, swept);
            Assert.AreEqual(0, _registry.TotalViews);
            Assert.IsNull(_registry.Get(viewId));
        }

        [Test]
        public void SweepDestroyed_MixedHealthyAndDestroyed_OnlyRemovesDestroyed()
        {
            _provider.WarmKey("goblin");
            _provider.WarmKey("torch");
            var id1 = _registry.Spawn("goblin", Vector3.zero);
            var id2 = _registry.Spawn("torch", Vector3.one);
            Assert.AreEqual(2, _registry.TotalViews);

            // Destroy only one
            Object.DestroyImmediate(_registry.Get(id1));

            var swept = _registry.SweepDestroyed();
            Assert.AreEqual(1, swept);
            Assert.AreEqual(1, _registry.TotalViews);
            Assert.IsNull(_registry.Get(id1));
            Assert.IsNotNull(_registry.Get(id2));
        }

        [Test]
        public void SweepDestroyed_DecrementsLiveCount()
        {
            _provider.WarmKey("goblin");
            var id1 = _registry.Spawn("goblin", Vector3.zero);
            var id2 = _registry.Spawn("goblin", Vector3.one);
            Assert.AreEqual(2, _registry.LiveCountsByKey["goblin"]);

            Object.DestroyImmediate(_registry.Get(id1));
            _registry.SweepDestroyed();

            Assert.AreEqual(1, _registry.LiveCountsByKey["goblin"]);
        }

        [Test]
        public void SweepDestroyed_DoubleSweep_SecondReturnsZero()
        {
            _provider.WarmKey("goblin");
            var id = _registry.Spawn("goblin", Vector3.zero);
            Object.DestroyImmediate(_registry.Get(id));

            Assert.AreEqual(1, _registry.SweepDestroyed());
            Assert.AreEqual(0, _registry.SweepDestroyed());
        }

        [Test]
        public void DiagnosticProperties_ReflectLiveState()
        {
            _provider.WarmKey("goblin");
            _provider.WarmKey("torch");
            _registry.Spawn("goblin", Vector3.zero);
            _registry.Spawn("goblin", Vector3.one);
            _registry.Spawn("torch", Vector3.zero);

            Assert.AreEqual(3, _registry.TotalViews);
            Assert.AreEqual(2, _registry.TotalKeys);
            Assert.AreEqual(2, _registry.LiveCountsByKey["goblin"]);
            Assert.AreEqual(1, _registry.LiveCountsByKey["torch"]);
        }

        /// <summary>
        /// Minimal provider that creates real GameObjects so they can be destroyed.
        /// </summary>
        private sealed class FakeViewProvider : IViewAssetProvider
        {
            private readonly System.Collections.Generic.HashSet<string> _warm
                = new System.Collections.Generic.HashSet<string>();

            public void WarmKey(string key) => _warm.Add(key);

            public System.Threading.Tasks.Task PrewarmAsync(string key, int count,
                System.Threading.CancellationToken ct = default)
            {
                _warm.Add(key);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public bool IsWarm(string key) => _warm.Contains(key);

            public GameObject Acquire(string key, Vector3 position, Quaternion rotation,
                Transform parent = null)
            {
                var go = new GameObject($"view-{key}");
                go.transform.position = position;
                go.transform.rotation = rotation;
                if (parent != null) go.transform.SetParent(parent);
                return go;
            }

            public System.Threading.Tasks.Task<GameObject> AcquireAsync(string key,
                Vector3 position, Quaternion rotation, Transform parent = null,
                System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(Acquire(key, position, rotation, parent));

            public void ReleaseInstance(GameObject instance)
            {
                if (instance != null) Object.DestroyImmediate(instance);
            }

            public void Release(string key) => _warm.Remove(key);
        }
    }
}
