using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Provisioning;
using UnityEngine;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Wraps another <see cref="IViewAssetProvider"/> and makes <see cref="PrewarmAsync"/> take
    /// real time, so a chunk can be released while it is still warming.
    /// </summary>
    /// <remarks>
    /// <see cref="PooledViewAssetProvider.PrewarmAsync"/> instantiates synchronously and hands back
    /// an already-completed task. That is the right behaviour for a pool over primitives, but it
    /// makes the interesting case unreachable from a sample: by the time the click handler can call
    /// <c>ReleaseChunk</c>, the warm has already finished, so the epoch guard in
    /// <c>ChunkViewProvisioner</c> is never exercised. A production provider backed by Addressables
    /// really does take frames, so this decorator stands in for one.
    /// <para>
    /// Only <see cref="PrewarmAsync"/> is delayed. Everything else forwards untouched.
    /// </para>
    /// </remarks>
    public sealed class DelayedViewAssetProvider : IViewAssetProvider
    {
        private readonly IViewAssetProvider _inner;

        public DelayedViewAssetProvider(IViewAssetProvider inner)
        {
            _inner = inner;
        }

        /// <summary>Artificial per-prewarm delay in milliseconds. Zero forwards straight through.</summary>
        public int DelayMilliseconds { get; set; }

        public async Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            if (DelayMilliseconds > 0)
            {
                await Task.Delay(DelayMilliseconds, cancellationToken);
            }

            await _inner.PrewarmAsync(key, count, cancellationToken);
        }

        public bool IsWarm(string key) => _inner.IsWarm(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null)
            => _inner.Acquire(key, position, rotation, parent);

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation,
            Transform parent = null, CancellationToken cancellationToken = default)
            => _inner.AcquireAsync(key, position, rotation, parent, cancellationToken);

        public void ReleaseInstance(GameObject instance) => _inner.ReleaseInstance(instance);

        public void Release(string key) => _inner.Release(key);
    }
}
