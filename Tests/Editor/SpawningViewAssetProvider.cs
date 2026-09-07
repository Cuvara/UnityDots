using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Provisioning;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Edit-mode counterpart of the play-mode <c>FakeViewAssetProvider</c>: hands out real empty
    /// GameObjects, destroys them on release, counts both. <see cref="RecordingViewAssetProvider"/>
    /// returns null from <c>Acquire</c> and so cannot drive the spawn path; this one can.
    /// </summary>
    internal sealed class SpawningViewAssetProvider : IViewAssetProvider
    {
        private readonly HashSet<string> _warm = new HashSet<string>();

        public int AcquireCount;
        public int ReleaseInstanceCount;
        public bool WarmEverything = true;

        /// <summary>Instances handed out and not yet released — what "no duplicate views" is asserted on.</summary>
        public int LiveInstances => AcquireCount - ReleaseInstanceCount;

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            _warm.Add(key);
            return Task.CompletedTask;
        }

        public bool IsWarm(string key) => WarmEverything || _warm.Contains(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            AcquireCount++;
            var instance = new GameObject(key);
            instance.transform.SetPositionAndRotation(position, rotation);
            if (parent != null) instance.transform.SetParent(parent, true);
            return instance;
        }

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Acquire(key, position, rotation, parent));

        public void ReleaseInstance(GameObject instance)
        {
            ReleaseInstanceCount++;
            if (instance != null) Object.DestroyImmediate(instance);
        }

        public void Release(string key) => _warm.Remove(key);
    }
}
