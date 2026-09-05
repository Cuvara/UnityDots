using Cuvara.DOTS.Provisioning;
using NUnit.Framework;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    public sealed class PooledViewAssetProviderTests
    {
        private PooledViewAssetProvider _provider;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("TestPrefab");
            _prefab.SetActive(false); // prefabs are inactive
            var poolRoot = new UnityEngine.GameObject("[TestPoolRoot]").transform;
            _provider = new PooledViewAssetProvider(poolRoot, defaultPoolSize: 4, maxPoolSize: 8);
            _provider.RegisterPrefab("goblin", _prefab);
        }

        [TearDown]
        public void TearDown()
        {
            _provider.Dispose();
            if (_prefab != null) Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void RegisterPrefab_MakesKeyAvailable()
        {
            Assert.AreEqual(1, _provider.RegisteredKeyCount);
        }

        [Test]
        public void PrewarmAsync_CreatesPooledInstances()
        {
            _provider.PrewarmAsync("goblin", 4);
            Assert.IsTrue(_provider.IsWarm("goblin"));
            Assert.AreEqual(4, _provider.GetPooledCount("goblin"));
            Assert.AreEqual(4, _provider.PooledCount);
        }

        [Test]
        public void Acquire_ReusesPooledInstance()
        {
            _provider.PrewarmAsync("goblin", 4);
            Assert.AreEqual(4, _provider.GetPooledCount("goblin"));

            var instance = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);

            Assert.IsNotNull(instance);
            Assert.IsTrue(instance.activeSelf);
            Assert.AreEqual(3, _provider.GetPooledCount("goblin")); // one taken from pool
            Assert.AreEqual(1, _provider.ActiveCount);
        }

        [Test]
        public void ReleaseInstance_ReturnsToPool()
        {
            _provider.PrewarmAsync("goblin", 4);
            var instance = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);
            Assert.AreEqual(1, _provider.ActiveCount);

            _provider.ReleaseInstance(instance);

            Assert.IsFalse(instance.activeSelf);
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(4, _provider.GetPooledCount("goblin")); // back to 4
        }

        [Test]
        public void Acquire_WithoutPrewarm_InstantiatesOnDemand()
        {
            // Not prewarmed — should still work by instantiating
            var instance = _provider.Acquire("goblin", Vector3.one, Quaternion.identity);
            Assert.IsNotNull(instance);
            Assert.IsTrue(instance.activeSelf);

            _provider.ReleaseInstance(instance);
        }

        [Test]
        public void ReleaseInstance_BeyondMaxPoolSize_DestroysExcess()
        {
            var root2 = new UnityEngine.GameObject("[TestPoolRoot2]").transform;
            _provider = new PooledViewAssetProvider(root2, defaultPoolSize: 2, maxPoolSize: 3);
            _provider.RegisterPrefab("goblin", _prefab);
            _provider.PrewarmAsync("goblin", 3);

            // Acquire all 3, then release 4 (1 extra created on demand)
            var a = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);
            var b = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);
            var c = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);
            var d = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity); // on demand

            _provider.ReleaseInstance(a);
            _provider.ReleaseInstance(b);
            _provider.ReleaseInstance(c);
            _provider.ReleaseInstance(d); // 4th release — pool is at max 3, so this one is destroyed

            Assert.AreEqual(3, _provider.GetPooledCount("goblin"));
        }

        [Test]
        public void Release_DestroysAllPooledInstances()
        {
            _provider.PrewarmAsync("goblin", 4);
            Assert.AreEqual(4, _provider.GetPooledCount("goblin"));

            _provider.Release("goblin");

            Assert.AreEqual(0, _provider.GetPooledCount("goblin"));
            Assert.IsFalse(_provider.IsWarm("goblin"));
        }

        [Test]
        public void AcquireReleaseCycle_NoInstantiateAfterWarm()
        {
            _provider.PrewarmAsync("goblin", 4);

            // Cycle 10 times — should reuse the same 2 instances
            for (int i = 0; i < 10; i++)
            {
                var instance = _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);
                Assert.IsNotNull(instance);
                _provider.ReleaseInstance(instance);
            }

            Assert.AreEqual(4, _provider.GetPooledCount("goblin"));
        }

        [Test]
        public void Acquire_SetsPositionAndRotation()
        {
            _provider.PrewarmAsync("goblin", 1);
            var pos = new Vector3(10, 5, 3);
            var rot = Quaternion.Euler(0, 90, 0);

            var instance = _provider.Acquire("goblin", pos, rot);

            Assert.AreEqual(pos.x, instance.transform.position.x, 0.01f);
            Assert.AreEqual(pos.y, instance.transform.position.y, 0.01f);
            Assert.AreEqual(rot.eulerAngles.y, instance.transform.rotation.eulerAngles.y, 0.1f);

            _provider.ReleaseInstance(instance);
        }

        [Test]
        public void Dispose_CleansEverything()
        {
            _provider.PrewarmAsync("goblin", 4);
            _provider.Acquire("goblin", Vector3.zero, Quaternion.identity);

            _provider.Dispose();

            Assert.AreEqual(0, _provider.PooledCount);
            Assert.AreEqual(0, _provider.ActiveCount);
            Assert.AreEqual(0, _provider.RegisteredKeyCount);
        }

        [Test]
        public void UnregisteredKey_AcquireReturnsNull()
        {
            var result = _provider.Acquire("unknown", Vector3.zero, Quaternion.identity);
            Assert.IsNull(result);
        }

        [Test]
        public void ReleaseInstance_Null_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _provider.ReleaseInstance(null));
        }
    }
}
