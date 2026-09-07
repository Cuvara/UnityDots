using UnityEngine;

namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// Narrow host-facing hook <see cref="PooledViewAssetProvider"/> calls when an instance changes
    /// hands, so reusable state (animator, particles, trails, event subscriptions, cached
    /// transforms) can be reset without the provider knowing what a view contains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately two methods and nothing else. The provider owns activation, parenting and
    /// placement; the host owns what is <i>inside</i> the instance. Anything a hook does beyond
    /// resetting the instance it was handed — spawning, acquiring, releasing other instances —
    /// re-enters the pool mid-operation and is unsupported.
    /// </para>
    /// <para>
    /// Both calls happen on the main thread. Exceptions propagate to the caller of
    /// <c>Acquire</c>/<c>ReleaseInstance</c>; the provider's own bookkeeping is already consistent
    /// by the time a hook runs, so a throwing hook does not corrupt the pool.
    /// </para>
    /// </remarks>
    public interface IPooledViewLifecycle
    {
        /// <summary>
        /// Called after the instance is parented, placed and activated, before it is returned to
        /// the caller of <c>Acquire</c>. Freshly instantiated and recycled instances both pass here.
        /// </summary>
        void OnAcquired(string key, GameObject instance);

        /// <summary>
        /// Called when an acquired instance is returned, <b>before</b> it is deactivated and parked
        /// under the pool root. Stop particles, unsubscribe, clear animator state here. Also called
        /// for an instance that will be destroyed rather than pooled (pool full, stale prefab
        /// registration), so a hook can always rely on seeing every acquired instance once more.
        /// </summary>
        void OnReleased(string key, GameObject instance);
    }
}
