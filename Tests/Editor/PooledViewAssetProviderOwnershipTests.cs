using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cuvara.DOTS.Provisioning;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Identity, ownership, disposal and cancellation contract of <see cref="PooledViewAssetProvider"/>.
    /// </summary>
    /// <remarks>
    /// The regressions these pin: return was routed by a substring of <c>GameObject.name</c>, so a
    /// renamed instance lost its pool and a duplicate return enqueued the same object twice, letting
    /// two later acquires share it; and <c>Dispose</c> destroyed whatever root it was handed, owned
    /// or not, while forgetting acquired instances without reclaiming them. Every test here runs in
    /// EditMode with real GameObjects and <c>DestroyImmediate</c>, so "destroyed" means destroyed.
    /// </remarks>
    public sealed class PooledViewAssetProviderOwnershipTests
    {
        private sealed class RecordingLifecycle : IPooledViewLifecycle
        {
            public readonly List<(string key, GameObject go, bool activeAtCall)> Acquired = new();
            public readonly List<(string key, GameObject go, bool activeAtCall)> Released = new();

            public void OnAcquired(string key, GameObject instance) => Acquired.Add((key, instance, instance.activeSelf));
            public void OnReleased(string key, GameObject instance) => Released.Add((key, instance, instance.activeSelf));
        }

        private GameObject _prefab;
        private GameObject _prefab2;
        private Transform _root;
        private PooledViewAssetProvider _provider;
        private readonly List<GameObject> _garbage = new();

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("GoblinPrefab");
            _prefab.SetActive(false);
            _prefab2 = new GameObject("GoblinPrefabV2");
            _prefab2.SetActive(false);
            _root = new GameObject("[CallerOwnedRoot]").transform;
            _provider = new PooledViewAssetProvider(_root, defaultPoolSize: 2, maxPoolSize: 4);
            _provider.RegisterPrefab("goblin", _prefab);
        }

        [TearDown]
        public void TearDown()
        {
            // Tests that leave leases open on purpose make Dispose warn; that warning is the
            // behaviour under test elsewhere, not a failure here.
            LogAssert.ignoreFailingMessages = true;
            _provider?.Dispose();
            LogAssert.ignoreFailingMessages = false;
            foreach (var go in _garbage) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _garbage.Clear();
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root.gameObject);
            if (_prefab != null) UnityEngine.Object.DestroyImmediate(_prefab);
            if (_prefab2 != null) UnityEngine.Object.DestroyImmediate(_prefab2);
        }

        private GameObject Acquire() => _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);

        private void AssertCountsReconcile()
        {
            Assert.AreEqual(_provider.ActiveCount + _provider.PooledCount, _provider.TotalInstanceCount,
                "tracked instances must equal acquired + pooled");
            Assert.AreEqual(_provider.GetActiveCount("goblin"), _provider.ActiveCount);
            Assert.AreEqual(_provider.GetPooledCount("goblin"), _provider.PooledCount);
        }

        // ---- identity ----

        [Test]
        public void DuplicateRelease_NeverLetsTwoAcquiresShareOneInstance()
        {
            _provider.PrewarmAsync("goblin", 1);
            var a = Acquire();

            _provider.ReleaseInstance(a);
            _provider.ReleaseInstance(a); // the bug: this used to enqueue `a` a second time

            Assert.AreEqual(1, _provider.DuplicateReleaseCount);
            Assert.AreEqual(1, _provider.GetPooledCount("goblin"), "one object, pooled once");

            var b = Acquire();
            var c = Acquire();

            Assert.AreSame(a, b, "the pooled instance is reused");
            Assert.AreNotSame(b, c, "the second acquire must get a different object");
            Assert.AreEqual(2, _provider.ActiveCount);
            AssertCountsReconcile();
        }

        [Test]
        public void Rename_DoesNotChangeOwnershipOrKey()
        {
            _provider.PrewarmAsync("goblin", 1);
            var a = Acquire();

            a.name = "something the pool never wrote";

            Assert.IsTrue(_provider.IsOwned(a));
            Assert.IsTrue(_provider.TryGetKey(a, out var key));
            Assert.AreEqual("goblin", key);

            _provider.ReleaseInstance(a);

            Assert.IsFalse(a == null, "an owned instance is pooled, not destroyed, whatever its name");
            Assert.AreEqual(1, _provider.GetPooledCount("goblin"));
            Assert.AreEqual(0, _provider.GetActiveCount("goblin"), "per-key counts come from the lease, not the name");
        }

        [Test]
        public void ForeignInstance_IsLeftUntouched()
        {
            var foreign = new GameObject("NotOurs [pool:goblin]"); // even a name that mimics ours
            _garbage.Add(foreign);

            _provider.ReleaseInstance(foreign);

            Assert.IsFalse(foreign == null, "never destroy what we did not create");
            Assert.IsTrue(foreign.activeSelf, "never deactivate it either");
            Assert.IsNull(foreign.transform.parent, "and never re-parent it");
            Assert.AreEqual(1, _provider.ForeignReleaseCount);
            Assert.AreEqual(0, _provider.PooledCount);
            Assert.IsFalse(_provider.IsOwned(foreign));
        }

        // ---- roots and disposal ----

        [Test]
        public void CallerRoot_SurvivesDispose_EmptiedOfPooledInstances()
        {
            _provider.PrewarmAsync("goblin", 3);
            Assert.AreEqual(3, _root.childCount);
            Assert.IsFalse(_provider.OwnsPoolRoot);

            _provider.Dispose();

            Assert.IsFalse(_root == null, "caller-owned root must survive");
            Assert.AreEqual(0, _root.childCount, "but our instances under it are gone");
        }

        [Test]
        public void OwnedRoot_IsDestroyedOnDispose()
        {
            var owning = new PooledViewAssetProvider(null, defaultPoolSize: 1, maxPoolSize: 1);
            owning.RegisterPrefab("goblin", _prefab);
            owning.PrewarmAsync("goblin", 1);
            var root = owning.PoolRoot;
            Assert.IsTrue(owning.OwnsPoolRoot);
            Assert.IsFalse(root == null);

            owning.Dispose();

            Assert.IsTrue(root == null, "a root the provider created goes with it");
        }

        [Test]
        public void Dispose_DestroysOutstandingLeases_ByDefault_AndSaysSo()
        {
            _provider.PrewarmAsync("goblin", 2);
            var a = Acquire();
            var b = Acquire();
            LogAssert.Expect(LogType.Warning, new Regex("2 instance\\(s\\) still acquired"));

            _provider.Dispose();

            Assert.IsTrue(a == null);
            Assert.IsTrue(b == null);
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.PooledCount);
            Assert.AreEqual(0, _provider.TotalInstanceCount);
        }

        [Test]
        public void Dispose_WithDetachPolicy_LeavesOutstandingInstancesToTheHolder()
        {
            var detaching = new PooledViewAssetProvider(_root, 1, 1, outstandingLeasePolicy: OutstandingLeasePolicy.Detach);
            detaching.RegisterPrefab("goblin", _prefab);
            var a = detaching.Acquire("goblin", Vector3.zero, Quaternion.identity);
            _garbage.Add(a);
            LogAssert.Expect(LogType.Warning, new Regex("1 instance\\(s\\) still acquired"));

            detaching.Dispose();

            Assert.IsFalse(a == null, "detached: the holder owns it now");
            Assert.IsTrue(a.activeSelf);
            Assert.AreEqual(0, detaching.TotalInstanceCount, "but the provider no longer tracks it");
        }

        [Test]
        public void Dispose_IsIdempotent_AndUseAfterDisposeIsAClearError()
        {
            _provider.PrewarmAsync("goblin", 1);
            var a = Acquire();
            LogAssert.Expect(LogType.Warning, new Regex("still acquired"));

            _provider.Dispose();
            Assert.DoesNotThrow(() => _provider.Dispose());
            Assert.IsTrue(_provider.IsDisposed);

            Assert.Throws<ObjectDisposedException>(() => Acquire());
            Assert.Throws<ObjectDisposedException>(() => _provider.RegisterPrefab("x", _prefab));
            Assert.Throws<ObjectDisposedException>(() => _provider.PrewarmAsync("goblin", 1));
            Assert.Throws<ObjectDisposedException>(() => _provider.AcquireAsync("goblin", Vector3.zero, Quaternion.identity));

            // Teardown ordering must not throw: a despawn system running after the pool is gone.
            Assert.DoesNotThrow(() => _provider.ReleaseInstance(a));
            Assert.DoesNotThrow(() => _provider.Release("goblin"));
            Assert.IsFalse(_provider.IsWarm("goblin"));
        }

        [Test]
        public void Counts_ReconcileThroughChurn_AndAfterTeardown()
        {
            _provider.PrewarmAsync("goblin", 2);
            var a = Acquire();
            var b = Acquire();
            var c = Acquire(); // on demand
            AssertCountsReconcile();
            Assert.AreEqual(3, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.PooledCount);

            _provider.ReleaseInstance(b);
            AssertCountsReconcile();
            Assert.AreEqual(2, _provider.ActiveCount);
            Assert.AreEqual(1, _provider.PooledCount);

            _provider.ReleaseInstance(a);
            _provider.ReleaseInstance(c);
            AssertCountsReconcile();
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(3, _provider.PooledCount);

            _provider.Dispose();
            Assert.AreEqual(0, _provider.TotalInstanceCount);
            Assert.AreEqual(0, _root.childCount);
        }

        // ---- cancellation ----

        [Test]
        public void CancelledPrewarm_CreatesNoInstances()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = _provider.PrewarmAsync("goblin", 8, cts.Token);

            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(0, _provider.PooledCount);
            Assert.AreEqual(0, _provider.TotalInstanceCount);
            Assert.AreEqual(0, _root.childCount);
            Assert.IsFalse(_provider.IsWarm("goblin"), "a cancelled prewarm does not make the key warm");
        }

        [Test]
        public void CancelledAcquireAsync_CreatesNoInstances()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = _provider.AcquireAsync("goblin", Vector3.zero, Quaternion.identity, null, cts.Token);

            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.TotalInstanceCount);
        }

        // ---- external destruction ----

        [Test]
        public void DestroyedPooledInstance_IsSkippedOnAcquire_AndCountsReconcile()
        {
            _provider.PrewarmAsync("goblin", 2);
            var pooled = _root.GetChild(0).gameObject;
            Assert.IsTrue(_provider.IsOwned(pooled));

            UnityEngine.Object.DestroyImmediate(pooled);

            var a = Acquire();
            var b = Acquire();

            Assert.IsFalse(a == null);
            Assert.IsFalse(b == null);
            Assert.AreEqual(1, _provider.ExternallyDestroyedCount);
            Assert.AreEqual(2, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.PooledCount);
            AssertCountsReconcile();
        }

        [Test]
        public void DestroyedActiveInstance_ReleaseDropsTheLease()
        {
            _provider.PrewarmAsync("goblin", 1);
            var a = Acquire();
            Assert.AreEqual(1, _provider.ActiveCount);

            UnityEngine.Object.DestroyImmediate(a);
            _provider.ReleaseInstance(a);

            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.PooledCount, "nothing to park");
            Assert.AreEqual(1, _provider.ExternallyDestroyedCount);
            Assert.IsFalse(_provider.IsOwned(a));
            AssertCountsReconcile();
        }

        [Test]
        public void SweepDestroyed_ReconcilesPooledAndActive()
        {
            _provider.PrewarmAsync("goblin", 3);
            var a = Acquire();
            var pooled = _root.GetChild(0).gameObject;

            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(pooled);
            Assert.AreEqual(1, _provider.ActiveCount, "not yet noticed");

            var swept = _provider.SweepDestroyed();

            Assert.AreEqual(2, swept);
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(1, _provider.PooledCount);
            Assert.AreEqual(2, _provider.ExternallyDestroyedCount);
            AssertCountsReconcile();

            var b = Acquire();
            Assert.IsFalse(b == null, "the surviving pooled instance still works");
        }

        // ---- re-registration ----

        [Test]
        public void ReplacingAKeysPrefab_DropsOldPool_AndDestroysOldInstancesOnReturn()
        {
            _provider.PrewarmAsync("goblin", 2);
            var live = Acquire();
            Assert.AreEqual(1, _provider.PooledCount);

            _provider.RegisterPrefab("goblin", _prefab2);

            Assert.AreEqual(0, _provider.PooledCount, "old-prefab instances would render the wrong thing");
            Assert.IsFalse(_provider.IsWarm("goblin"), "the new prefab has not been warmed");
            Assert.IsFalse(live == null, "acquired instances keep running");
            Assert.AreEqual(1, _provider.ActiveCount);

            _provider.ReleaseInstance(live);

            Assert.IsTrue(live == null, "a stale-prefab instance is destroyed, not pooled");
            Assert.AreEqual(0, _provider.PooledCount);
            Assert.AreEqual(0, _provider.ActiveCount);

            var fresh = Acquire();
            StringAssert.Contains("GoblinPrefabV2", fresh.name, "new acquires come from the new prefab");
            AssertCountsReconcile();
        }

        [Test]
        public void ReregisteringTheSamePrefab_IsANoOp()
        {
            _provider.PrewarmAsync("goblin", 2);

            _provider.RegisterPrefab("goblin", _prefab);

            Assert.AreEqual(2, _provider.PooledCount);
            Assert.IsTrue(_provider.IsWarm("goblin"));
        }

        [Test]
        public void ReleaseKey_DropsPool_AndLiveInstancesAreDestroyedOnReturn()
        {
            _provider.PrewarmAsync("goblin", 2);
            var live = Acquire();

            _provider.Release("goblin");

            Assert.AreEqual(0, _provider.PooledCount);
            Assert.IsFalse(_provider.IsWarm("goblin"));
            Assert.AreEqual(1, _provider.RegisteredKeyCount, "Release drops instances, not the registration");
            Assert.IsFalse(live == null);

            _provider.ReleaseInstance(live);

            Assert.IsTrue(live == null);
            Assert.AreEqual(0, _provider.ActiveCount);
            AssertCountsReconcile();
        }

        // ---- pool cap vs admission budget ----

        [Test]
        public void MaxPoolSize_BoundsInactiveInstancesOnly()
        {
            // maxPoolSize is 4; nothing stops 6 being acquired at once.
            var live = new List<GameObject>();
            for (var i = 0; i < 6; i++) live.Add(Acquire());

            Assert.AreEqual(6, _provider.ActiveCount);
            Assert.AreEqual(6, _provider.TotalInstanceCount);
            Assert.Greater(_provider.TotalInstanceCount, _provider.MaxPoolSize);
            Assert.AreEqual(0, _provider.AdmissionRejectedCount);

            foreach (var go in live) _provider.ReleaseInstance(go);

            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.PooledCount, "no pool was ever created for the key: returns beyond the pool are destroyed");
        }

        [Test]
        public void MaxPoolSize_CapsReturnsIntoAnExistingPool()
        {
            _provider.PrewarmAsync("goblin", 2);
            var live = new List<GameObject>();
            for (var i = 0; i < 6; i++) live.Add(Acquire());

            foreach (var go in live) _provider.ReleaseInstance(go);

            Assert.AreEqual(4, _provider.PooledCount, "capped at maxPoolSize; the other two were destroyed");
            AssertCountsReconcile();
        }

        [Test]
        public void MaxActivePerKey_IsTheAdmissionBudget()
        {
            var budgeted = new PooledViewAssetProvider(_root, 1, 8, maxActivePerKey: 2);
            budgeted.RegisterPrefab("goblin", _prefab);

            var a = budgeted.Acquire("goblin", Vector3.zero, Quaternion.identity);
            var b = budgeted.Acquire("goblin", Vector3.zero, Quaternion.identity);
            var c = budgeted.Acquire("goblin", Vector3.zero, Quaternion.identity);

            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.IsNull(c, "not admitted");
            Assert.AreEqual(1, budgeted.AdmissionRejectedCount);
            Assert.AreEqual(2, budgeted.TotalInstanceCount, "a refused acquire instantiates nothing");

            budgeted.ReleaseInstance(a);
            var d = budgeted.Acquire("goblin", Vector3.zero, Quaternion.identity);
            Assert.IsNotNull(d, "budget freed by the return");

            budgeted.ReleaseInstance(b);
            budgeted.ReleaseInstance(d);
            budgeted.Dispose();
        }

        // ---- lifecycle hook ----

        [Test]
        public void LifecycleHook_SeesAcquireAfterActivation_AndReleaseBeforeDeactivation()
        {
            var hook = new RecordingLifecycle();
            var hooked = new PooledViewAssetProvider(_root, 1, 4, lifecycle: hook);
            hooked.RegisterPrefab("goblin", _prefab);
            hooked.PrewarmAsync("goblin", 1);

            var a = hooked.Acquire("goblin", Vector3.zero, Quaternion.identity);
            Assert.AreEqual(1, hook.Acquired.Count);
            Assert.AreEqual("goblin", hook.Acquired[0].key);
            Assert.AreSame(a, hook.Acquired[0].go);
            Assert.IsTrue(hook.Acquired[0].activeAtCall, "activated before the hook runs");

            hooked.ReleaseInstance(a);
            Assert.AreEqual(1, hook.Released.Count);
            Assert.AreSame(a, hook.Released[0].go);
            Assert.IsTrue(hook.Released[0].activeAtCall, "still active when the hook runs, so components can be reset");
            Assert.IsFalse(a.activeSelf, "deactivated afterwards");

            hooked.ReleaseInstance(a); // duplicate: no second hook call
            Assert.AreEqual(1, hook.Released.Count);

            hooked.Dispose();
        }
    }
}
