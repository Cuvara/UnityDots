using System;
using System.Collections.Generic;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Provisioning;
using Unity.Mathematics;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// The managed side-table of the hybrid: view handle -&gt; live GameObject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything managed about the hybrid lives here, so the systems around it stay
    /// <c>ISystem</c> structs. Plain constructor, no DI types, no statics — an instance is handed
    /// to the world through <see cref="EntityViewRegistryReference"/>.
    /// </para>
    /// <para>
    /// Handles are dense positive integers and are <b>not</b> reused after despawn. Reuse would
    /// make a stale <see cref="EntityViewLink"/> silently address someone else's view, which is a
    /// bug that reads as a rendering glitch. Wrapping at <see cref="int.MaxValue"/> is not
    /// defended against; a session would have to spawn two billion views to reach it.
    /// </para>
    /// <para>
    /// It records which key each handle came from, which is what lets
    /// <see cref="EntityViewCascade"/> find the views standing on an unloading chunk's keys and take
    /// them down before the assets go.
    /// </para>
    /// </remarks>
    public sealed class EntityViewRegistry
    {
        private readonly IViewAssetProvider _provider;
        private readonly Dictionary<int, GameObject> _views = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Transform> _transforms = new Dictionary<int, Transform>();

        /// <summary>Handle -&gt; the key it was spawned from, so a despawn can decrement the right count.</summary>
        private readonly Dictionary<int, string> _keys = new Dictionary<int, string>();

        /// <summary>Key -&gt; number of live views currently standing on it.</summary>
        private readonly Dictionary<string, int> _liveByKey = new Dictionary<string, int>();

        /// <summary>Key -&gt; consecutive frames a spawn has been deferred waiting for it to warm.</summary>
        private readonly Dictionary<string, int> _deferrals = new Dictionary<string, int>();

        /// <summary>Keys already warned about, so the warning is once per key rather than per frame.</summary>
        private readonly HashSet<string> _warnedKeys = new HashSet<string>();

        private readonly IDotsPublisher<ViewSpawned> _spawnedPublisher;
        private readonly IDotsPublisher<ViewDespawned> _despawnedPublisher;
        private readonly Transform _root;
        private int _nextViewId = 1;

        public EntityViewRegistry(
            IViewAssetProvider provider,
            Transform root = null,
            IDotsPublisher<ViewSpawned> spawnedPublisher = null,
            IDotsPublisher<ViewDespawned> despawnedPublisher = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _root = root;

            // Never null internally, so no publish site has to branch on whether messaging exists.
            _spawnedPublisher = spawnedPublisher ?? NullDotsPublisher<ViewSpawned>.Instance;
            _despawnedPublisher = despawnedPublisher ?? NullDotsPublisher<ViewDespawned>.Instance;
        }

        /// <summary>Live view count. Used by tests and diagnostics.</summary>
        public int Count => _views.Count;

        /// <summary>Whether the pool can serve <paramref name="key"/> without a synchronous load.</summary>
        public bool IsWarm(string key) => _provider.IsWarm(key);

        /// <summary>
        /// Frames a spawn is deferred before the key is reported as probably never arriving. About
        /// five seconds at 60fps — long enough that an ordinary streaming load never trips it.
        /// </summary>
        public const int DeferralWarningThreshold = 300;

        /// <summary>
        /// Records that a spawn was deferred because its key is not warm, and warns once per key
        /// after <see cref="DeferralWarningThreshold"/> attempts.
        /// </summary>
        /// <remarks>
        /// Deferring rather than force-loading is the right policy, but a key that will never be
        /// warmed — a typo, a prefab missing from a chunk manifest — is indistinguishable from a
        /// slow load if nothing ever says so. One warning per key, not one per frame, because the
        /// per-frame version is noise that gets filtered and then ignored.
        /// </remarks>
        /// <returns>True if this call emitted the warning.</returns>
        public bool NoteDeferredSpawn(string key)
        {
            if (string.IsNullOrEmpty(key) || _warnedKeys.Contains(key)) return false;

            _deferrals.TryGetValue(key, out var count);
            count++;
            _deferrals[key] = count;

            if (count < DeferralWarningThreshold) return false;

            _warnedKeys.Add(key);
            Debug.LogWarning(
                $"[Cuvara.DOTS] View key '{key}' has been waiting to spawn for {count} frames and is still not " +
                "warm. Either no chunk prewarmed it or the key does not exist. Entities requesting it stay " +
                "invisible until it arrives. This warns once per key.");
            return true;
        }

        /// <summary>Live view instances currently spawned from <paramref name="key"/>.</summary>
        public int CountLiveViews(string key) =>
            key != null && _liveByKey.TryGetValue(key, out var count) ? count : 0;

        /// <summary>
        /// The key a handle was spawned from. False for an unknown or already-despawned handle.
        /// </summary>
        public bool TryGetKey(int viewId, out string key) => _keys.TryGetValue(viewId, out key);

        /// <summary>
        /// Spawns a view for the key and returns its handle, or 0 if the provider gave nothing back.
        /// </summary>
        public int Spawn(string key, float3 position) => Spawn(key, position, quaternion.identity);

        /// <summary>
        /// Spawns a view at a position and rotation.
        /// </summary>
        /// <remarks>
        /// The rotation overload exists because spawning at <c>identity</c> and letting the first
        /// sync fix it means one frame of visibly wrong facing on every spawn — most obvious on
        /// anything spawned already moving.
        /// </remarks>
        public int Spawn(string key, float3 position, quaternion rotation)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            var instance = _provider.Acquire(key, position, rotation, _root);
            if (instance == null) return 0;

            var viewId = _nextViewId++;
            _views[viewId] = instance;
            _transforms[viewId] = instance.transform;
            _keys[viewId] = key;
            _liveByKey.TryGetValue(key, out var live);
            _liveByKey[key] = live + 1;
            _deferrals.Remove(key); // it arrived; the wait no longer counts against it

            _spawnedPublisher.Publish(new ViewSpawned(viewId, key));
            return viewId;
        }

        /// <summary>Recycles the view behind the handle. Unknown or already-released handles are a no-op.</summary>
        public bool Despawn(int viewId)
        {
            if (!_views.TryGetValue(viewId, out var instance)) return false;

            _views.Remove(viewId);
            _transforms.Remove(viewId);

            _keys.TryGetValue(viewId, out var key);
            _keys.Remove(viewId);
            DecrementLive(key);

            if (instance != null) _provider.ReleaseInstance(instance);
            _despawnedPublisher.Publish(new ViewDespawned(viewId, key));
            return true;
        }

        /// <summary>Resolves a handle. Null when unknown or the instance was destroyed behind our back.</summary>
        public GameObject Get(int viewId)
        {
            return _views.TryGetValue(viewId, out var instance) ? instance : null;
        }

        /// <summary>
        /// Writes one sampled ECS transform onto its GameObject. Called in a tight main-thread loop
        /// by <see cref="EntityViewTransformSyncSystem"/>, hence the cached
        /// <see cref="Transform"/> lookup — <c>GameObject.transform</c> is a native call each time.
        /// </summary>
        public void ApplyTransform(in ViewTransformSample sample)
        {
            if (!_transforms.TryGetValue(sample.ViewId, out var transform) || transform == null) return;

            transform.SetPositionAndRotation(sample.Position, sample.Rotation);
            var scale = sample.Scale;
            transform.localScale = new Vector3(scale, scale, scale);
        }

        /// <summary>Recycles every live view. For session teardown.</summary>
        public void Clear()
        {
            foreach (var pair in _views)
            {
                if (pair.Value != null) _provider.ReleaseInstance(pair.Value);
                _keys.TryGetValue(pair.Key, out var key);
                _despawnedPublisher.Publish(new ViewDespawned(pair.Key, key));
            }

            _views.Clear();
            _transforms.Clear();
            _keys.Clear();
            _liveByKey.Clear();
        }

        /// <summary>
        /// Detects views whose <c>Transform</c> was destroyed externally (scene unload,
        /// manual <c>Object.Destroy</c>, editor reset) and despawns them. Returns the
        /// number of stale entries cleaned up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A stale link is an invisible entity: the ECS side thinks it has a view but the
        /// GameObject is gone. <see cref="ApplyTransform"/> silently skips it, and nothing
        /// else ever removes the entry. This sweep turns that silent failure into an
        /// explicit despawn so the entity can respawn if desired.
        /// </para>
        /// <para>
        /// Called by <see cref="EntityViewDespawnSystem"/> once per frame, before the
        /// entity-destroyed query. The cost is one dictionary scan — no allocation, no
        /// Unity API call per healthy view (only <c>!= null</c> on cached references).
        /// </para>
        /// </remarks>
        public int SweepDestroyed()
        {
            if (_transforms.Count == 0) return 0;

            // Collect stale IDs first — cannot modify _transforms while iterating it.
            List<int> stale = null;
            foreach (var pair in _transforms)
            {
                if (pair.Value == null)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null) return 0;

            for (int i = 0; i < stale.Count; i++)
            {
                var viewId = stale[i];
                _views.Remove(viewId);
                _transforms.Remove(viewId);
                _keys.TryGetValue(viewId, out var key);
                _keys.Remove(viewId);
                DecrementLive(key);
                _despawnedPublisher.Publish(new ViewDespawned(viewId, key));
            }

            if (stale.Count > 0)
            {
                Debug.LogWarning(
                    $"[Cuvara.DOTS] SweepDestroyed cleaned up {stale.Count} view(s) whose " +
                    "GameObject was destroyed externally. Entities holding these links are now " +
                    "invisible and may re-enter the spawn queue.");
            }

            return stale.Count;
        }

        /// <summary>
        /// Returns the view IDs of all views whose <c>Transform</c> is null (destroyed externally).
        /// Used by <see cref="EntityViewDespawnSystem"/> to remove the <see cref="EntityViewLink"/>
        /// from the owning entity so it can respawn.
        /// </summary>
        public void GetSweepedViewIds(List<int> result)
        {
            result.Clear();
            foreach (var pair in _transforms)
            {
                if (pair.Value == null) result.Add(pair.Key);
            }
        }

        /// <summary>Total number of live views.</summary>
        public int TotalViews => _views.Count;

        /// <summary>Number of distinct warm keys with at least one live view.</summary>
        public int TotalKeys => _liveByKey.Count;

        /// <summary>Live view counts per key. Read-only view for diagnostics.</summary>
        public IReadOnlyDictionary<string, int> LiveCountsByKey => _liveByKey;

        /// <summary>Deferred spawn frame counts per key. Read-only view for diagnostics.</summary>
        public IReadOnlyDictionary<string, int> DeferralsByKey => _deferrals;

        private void DecrementLive(string key)
        {
            if (key == null || !_liveByKey.TryGetValue(key, out var live)) return;

            if (live <= 1) _liveByKey.Remove(key);
            else _liveByKey[key] = live - 1;
        }
    }
}
