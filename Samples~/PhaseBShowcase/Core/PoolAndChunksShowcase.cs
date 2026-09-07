using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Cuvara.DOTS.Provisioning;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Drives <see cref="PooledViewAssetProvider"/> and <see cref="ChunkViewProvisioner"/> by hand
    /// (plan items D02 and D03) so every accounting path they added is visible on screen.
    /// </summary>
    /// <remarks>
    /// Offline by design: no backend, no netcode, no Addressables. Prefabs are primitives built at
    /// <c>Start</c>. The point of the scene is the counters — duplicate release, foreign release,
    /// external destruction and admission rejection are all *silent* in production, which is
    /// exactly why they were given counters and why a scene that shows them earns its place.
    /// </remarks>
    public sealed class PoolAndChunksShowcase : MonoBehaviour
    {
        private const string Cube = "cube";
        private const string Sphere = "sphere";
        private const string Capsule = "capsule";

        private const string ChunkA = "chunk-a";
        private const string ChunkB = "chunk-b";
        private const string ChunkC = "chunk-c";

        /// <summary>Admission cap used for every key, so the rejection path is one click away.</summary>
        private const int MaxActivePerKey = 4;

        private static readonly string[] ChunkAKeys = { Cube, Sphere };
        private static readonly string[] ChunkBKeys = { Sphere, Capsule };
        private static readonly string[] ChunkCKeys = { Capsule };

        private PooledViewAssetProvider _provider;
        private DelayedViewAssetProvider _slowProvider;
        private ChunkViewProvisioner _provisioner;

        private Transform _viewRoot;
        private Transform _templateRoot;
        private readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();

        // Acquired instances, per key, in acquisition order. The sample releases the most recent
        // one so repeated clicking is predictable.
        private readonly Dictionary<string, List<GameObject>> _acquired = new Dictionary<string, List<GameObject>>();

        private readonly StringBuilder _counters = new StringBuilder();
        private ShowcaseUi.RollingLog _log;
        private Label _countersLabel;

        private int _placementCounter;

        // The three chunk demos are async void and share one provider delay, so they must
        // not overlap: a second click's finally would clear the delay the first is still
        // relying on, and the release-while-warming demo would silently stop demonstrating
        // anything.
        private bool _chunkOperationInFlight;

        private void Start()
        {
            _templateRoot = new GameObject("Templates").transform;
            _templateRoot.SetParent(transform, false);

            _viewRoot = new GameObject("Views").transform;
            _viewRoot.SetParent(transform, false);

            _templates[Cube] = PrimitiveTemplates.Create(Cube, PrimitiveType.Cube, new Color(0.85f, 0.35f, 0.25f), _templateRoot);
            _templates[Sphere] = PrimitiveTemplates.Create(Sphere, PrimitiveType.Sphere, new Color(0.25f, 0.6f, 0.9f), _templateRoot);
            _templates[Capsule] = PrimitiveTemplates.Create(Capsule, PrimitiveType.Capsule, new Color(0.4f, 0.8f, 0.4f), _templateRoot);

            _provider = new PooledViewAssetProvider(
                poolRoot: _viewRoot,
                defaultPoolSize: 2,
                maxPoolSize: 6,
                lifecycle: null,
                outstandingLeasePolicy: OutstandingLeasePolicy.Destroy,
                maxActivePerKey: MaxActivePerKey);

            foreach (var pair in _templates)
            {
                _provider.RegisterPrefab(pair.Key, pair.Value);
                _acquired[pair.Key] = new List<GameObject>();
            }

            // The chunk provisioner drives the pool through the decorator so "release while warming"
            // has a window to happen in. NullViewCascadeSink because this scene has no view layer to
            // cascade into — the provisioner requires a sink rather than accepting null.
            _slowProvider = new DelayedViewAssetProvider(_provider);
            _provisioner = new ChunkViewProvisioner(_slowProvider, NullViewCascadeSink.Instance);
            _provisioner.OnChunkStateChanged += OnChunkStateChanged;

            BuildUi();
            Log($"Ready. Pool caps: maxActivePerKey={_provider.MaxActivePerKey}, maxPoolSize={_provider.MaxPoolSize}.");

            if (ShowcaseAutorun.Requested) StartCoroutine(Autorun());
        }

        private void OnDestroy()
        {
            if (_provisioner != null) _provisioner.OnChunkStateChanged -= OnChunkStateChanged;
            _provider?.Dispose();
        }

        private void BuildUi()
        {
            var root = ShowcaseUi.Root(this);
            if (root == null) return;

            _countersLabel = ShowcaseUi.Label(root, "counters");
            _log = new ShowcaseUi.RollingLog(ShowcaseUi.Label(root, "log"), 16);

            ShowcaseUi.OnClick(root, "acquire-cube", () => Acquire(Cube));
            ShowcaseUi.OnClick(root, "acquire-sphere", () => Acquire(Sphere));
            ShowcaseUi.OnClick(root, "release-cube", () => ReleaseLast(Cube));
            ShowcaseUi.OnClick(root, "release-sphere", () => ReleaseLast(Sphere));

            ShowcaseUi.OnClick(root, "duplicate-release", DuplicateRelease);
            ShowcaseUi.OnClick(root, "foreign-release", ForeignRelease);
            ShowcaseUi.OnClick(root, "external-destroy", ExternalDestroy);
            ShowcaseUi.OnClick(root, "sweep-destroyed", SweepDestroyed);
            ShowcaseUi.OnClick(root, "hit-admission-cap", HitAdmissionCap);

            ShowcaseUi.OnClick(root, "dispose-destroy", () => DisposePolicy(OutstandingLeasePolicy.Destroy));
            ShowcaseUi.OnClick(root, "dispose-detach", () => DisposePolicy(OutstandingLeasePolicy.Detach));

            ShowcaseUi.OnClick(root, "warm-chunk-a", () => WarmChunk(ChunkA, ChunkAKeys));
            ShowcaseUi.OnClick(root, "warm-chunk-b", () => WarmChunk(ChunkB, ChunkBKeys));
            ShowcaseUi.OnClick(root, "release-chunk-a", () => ReleaseChunk(ChunkA));
            ShowcaseUi.OnClick(root, "release-chunk-b", () => ReleaseChunk(ChunkB));
            ShowcaseUi.OnClick(root, "release-while-warming", ReleaseWhileWarming);
            ShowcaseUi.OnClick(root, "cycle-chunks", CycleChunks);

            ShowcaseUi.OnClick(root, "reset-all", ResetAll);
        }

        private void Update() => RenderCounters();

        // ---------------------------------------------------------------- pool

        private Vector3 NextPlacement()
        {
            // A slowly filling lattice, so repeated acquires do not stack in one spot.
            var index = _placementCounter++;
            var x = (index % 6) * 1.6f - 4f;
            var z = ((index / 6) % 4) * 1.6f - 2.4f;
            return new Vector3(x, 0.5f, z);
        }

        private void Acquire(string key)
        {
            var instance = _provider.Acquire(key, NextPlacement(), Quaternion.identity, _viewRoot);
            if (instance == null)
            {
                Log($"Acquire('{key}') returned null — admission cap {MaxActivePerKey} reached. " +
                    $"AdmissionRejectedCount={_provider.AdmissionRejectedCount}.");
                return;
            }

            _acquired[key].Add(instance);
            Log($"Acquired '{key}'. active={_provider.GetActiveCount(key)} pooled={_provider.GetPooledCount(key)}");
        }

        private void ReleaseLast(string key)
        {
            var list = _acquired[key];
            if (list.Count == 0)
            {
                Log($"Nothing acquired for '{key}' to release.");
                return;
            }

            var instance = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            _provider.ReleaseInstance(instance);
            Log($"Released '{key}'. active={_provider.GetActiveCount(key)} pooled={_provider.GetPooledCount(key)}");
        }

        /// <summary>
        /// Releases the same instance twice. The second call is swallowed and counted rather than
        /// double-enqueued, which is what stops one bad despawn poisoning a pool forever.
        /// </summary>
        private void DuplicateRelease()
        {
            var instance = _provider.Acquire(Cube, NextPlacement(), Quaternion.identity, _viewRoot);
            if (instance == null)
            {
                Log("Duplicate-release demo needs a free slot; release a cube first.");
                return;
            }

            var before = _provider.DuplicateReleaseCount;
            _provider.ReleaseInstance(instance);
            _provider.ReleaseInstance(instance);
            Log($"Released the same instance twice. DuplicateReleaseCount {before} -> {_provider.DuplicateReleaseCount}. " +
                $"pooled('{Cube}')={_provider.GetPooledCount(Cube)} (one entry, not two).");
        }

        /// <summary>
        /// Hands the provider an instance it never issued. It is counted and left completely
        /// untouched — the pool must not adopt, deactivate or destroy someone else's object.
        /// </summary>
        private void ForeignRelease()
        {
            var stranger = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stranger.name = "[foreign] not from the pool";
            stranger.transform.SetParent(_viewRoot, false);
            stranger.transform.position = new Vector3(0f, 2.5f, 0f);
            PrimitiveTemplates.Tint(stranger, new Color(0.9f, 0.9f, 0.2f));

            var before = _provider.ForeignReleaseCount;
            _provider.ReleaseInstance(stranger);
            Log($"Released a foreign instance. ForeignReleaseCount {before} -> {_provider.ForeignReleaseCount}. " +
                $"Still active in the scene: {stranger.activeSelf} (the pool left it alone).");

            Destroy(stranger, 2f);
        }

        /// <summary>Destroys a live acquired instance behind the provider's back.</summary>
        private void ExternalDestroy()
        {
            GameObject victim = null;
            string victimKey = null;
            foreach (var pair in _acquired)
            {
                if (pair.Value.Count <= 0) continue;
                victimKey = pair.Key;
                victim = pair.Value[pair.Value.Count - 1];
                pair.Value.RemoveAt(pair.Value.Count - 1);
                break;
            }

            if (victim == null)
            {
                Log("Acquire something first, then destroy it externally.");
                return;
            }

            // DestroyImmediate so the object is gone before SweepDestroyed can be clicked; in play
            // mode Destroy defers to end of frame, which would make the demo depend on click timing.
            DestroyImmediate(victim);
            Log($"Destroyed an acquired '{victimKey}' instance externally. The provider does not know yet: " +
                $"active('{victimKey}')={_provider.GetActiveCount(victimKey)}. Now click SweepDestroyed.");
        }

        private void SweepDestroyed()
        {
            var before = _provider.ExternallyDestroyedCount;
            var found = _provider.SweepDestroyed();
            Log($"SweepDestroyed() returned {found}. ExternallyDestroyedCount {before} -> {_provider.ExternallyDestroyedCount}. " +
                $"Active counts are reconciled.");
        }

        /// <summary>Acquires past <see cref="MaxActivePerKey"/> so admission rejection is visible.</summary>
        private void HitAdmissionCap()
        {
            var granted = 0;
            var rejected = 0;
            for (var i = 0; i < MaxActivePerKey + 2; i++)
            {
                var instance = _provider.Acquire(Sphere, NextPlacement(), Quaternion.identity, _viewRoot);
                if (instance == null)
                {
                    rejected++;
                    continue;
                }

                granted++;
                _acquired[Sphere].Add(instance);
            }

            Log($"Asked for {MaxActivePerKey + 2} spheres: {granted} granted, {rejected} rejected (null returns). " +
                $"AdmissionRejectedCount={_provider.AdmissionRejectedCount}.");
        }

        /// <summary>
        /// Builds a throwaway provider, leaves one lease outstanding and disposes it under the
        /// chosen policy. Destroy kills the instance; Detach leaves it alive and orphaned.
        /// Both policies log a warning naming the outstanding count — that warning is expected here.
        /// </summary>
        /// <returns>Whether the outstanding instance was still alive after Dispose.</returns>
        private bool DisposePolicy(OutstandingLeasePolicy policy)
        {
            var root = new GameObject($"DisposeDemo-{policy}").transform;
            root.SetParent(transform, false);

            var provider = new PooledViewAssetProvider(
                poolRoot: root,
                defaultPoolSize: 1,
                maxPoolSize: 2,
                lifecycle: null,
                outstandingLeasePolicy: policy);

            provider.RegisterPrefab(Capsule, _templates[Capsule]);
            var leased = provider.Acquire(Capsule, new Vector3(6f, 0.5f, 0f), Quaternion.identity, root);
            provider.Dispose();

            // Unity's overloaded equality reports a destroyed object as null.
            var stillAlive = leased != null;
            Log($"Dispose with {policy}: outstanding instance alive afterwards = {stillAlive}. " +
                (stillAlive
                    ? "Detach forgets the lease and the holder now owns the object."
                    : "Destroy reclaims it."));

            if (stillAlive) Destroy(leased);
            Destroy(root.gameObject, 0.1f);
            return stillAlive;
        }

        // --------------------------------------------------------------- chunks

        private void OnChunkStateChanged(string chunkId, ChunkState state)
            => Log($"chunk '{chunkId}' -> {state}");

        /// <summary>Refuses to start a second chunk operation while one is in flight.</summary>
        private bool BeginChunkOperation(string what)
        {
            if (!_chunkOperationInFlight)
            {
                _chunkOperationInFlight = true;
                return true;
            }

            Log($"'{what}' ignored: a chunk operation is already running.");
            return false;
        }

        /// <summary>
        /// True once the component has been destroyed, which an async continuation can reach after
        /// OnDestroy has already disposed the provider.
        /// </summary>
        private bool Gone => this == null;

        private async void WarmChunk(string chunkId, string[] keys)
        {
            if (!BeginChunkOperation("Warm " + chunkId)) return;

            try
            {
                await _provisioner.PrewarmChunkAsync(chunkId, keys, countPerKey: 2);
                if (Gone) return;
                Log($"Warmed '{chunkId}' [{string.Join(", ", keys)}]. loaded={_provisioner.IsChunkLoaded(chunkId)}");
            }
            catch (Exception e)
            {
                Log($"Warm of '{chunkId}' failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _chunkOperationInFlight = false;
            }
        }

        private void ReleaseChunk(string chunkId)
        {
            var result = _provisioner.ReleaseChunk(chunkId);
            if (!result.WasTracked)
            {
                Log($"ReleaseChunk('{chunkId}'): not tracked (already released, or never warmed).");
                return;
            }

            Log($"Released '{chunkId}': keysReleased={result.KeysReleased} viewsDespawned={result.ViewsDespawned}. " +
                $"refs(sphere)={_provisioner.GetReferenceCount(Sphere)} — a key another chunk still holds is kept.");
        }

        /// <summary>
        /// Starts a deliberately slow warm and releases the chunk before it finishes. The
        /// provisioner stamps each prewarm with an epoch; the late completion finds its epoch stale,
        /// so no ChunkWarmed is published and the chunk does not come back from the dead.
        /// </summary>
        private async void ReleaseWhileWarming()
        {
            if (!BeginChunkOperation("Release while warming")) return;

            _slowProvider.DelayMilliseconds = 1200;
            try
            {
                Log("Starting a 1200 ms warm of 'chunk-c', then releasing it immediately...");
                var warming = _provisioner.PrewarmChunkAsync(ChunkC, ChunkCKeys, countPerKey: 2);

                var result = _provisioner.ReleaseChunk(ChunkC);
                Log($"Released mid-warm: wasTracked={result.WasTracked} keysReleased={result.KeysReleased}.");

                await warming;
                if (Gone) return;
                Log($"The slow warm finished. tracked('{ChunkC}')={_provisioner.IsChunkTracked(ChunkC)} — " +
                    "stale epoch, so the completion was ignored and no ChunkWarmed fired.");
            }
            catch (Exception e)
            {
                Log($"Warm of '{ChunkC}' threw after release: {e.GetType().Name}.");
            }
            finally
            {
                _slowProvider.DelayMilliseconds = 0;
                _chunkOperationInFlight = false;
            }
        }

        /// <summary>Warms and releases both chunks five times, then reports the baseline.</summary>
        private async void CycleChunks()
        {
            if (!BeginChunkOperation("Cycle x5")) return;

            try
            {
                for (var i = 0; i < 5; i++)
                {
                    await _provisioner.PrewarmChunkAsync(ChunkA, ChunkAKeys, countPerKey: 2);
                    if (Gone) return;
                    await _provisioner.PrewarmChunkAsync(ChunkB, ChunkBKeys, countPerKey: 2);
                    if (Gone) return;
                    _provisioner.ReleaseChunk(ChunkA);
                    _provisioner.ReleaseChunk(ChunkB);
                }

                Log($"5 warm/release cycles done. chunks={_provisioner.ChunkCount} trackedKeys={_provisioner.TrackedKeyCount} " +
                    $"refs(sphere)={_provisioner.GetReferenceCount(Sphere)} — back to baseline.");
            }
            catch (Exception e)
            {
                Log($"Cycle failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _chunkOperationInFlight = false;
            }
        }

        private void ResetAll()
        {
            foreach (var pair in _acquired)
            {
                foreach (var instance in pair.Value) _provider.ReleaseInstance(instance);
                pair.Value.Clear();
            }

            var despawned = _provisioner.ReleaseAll(includeSession: true);
            _provider.SweepDestroyed();
            _placementCounter = 0;
            Log($"Reset. ReleaseAll despawned {despawned}. Counters are cumulative and deliberately not cleared.");
        }

        // ------------------------------------------------------------- rendering

        private void Log(string line)
        {
            // Start() logs before BuildUi() has run in no path today, but a null-guard keeps a
            // UXML typo from turning every later click into a NullReferenceException.
            _log?.Add(line);
        }

        private void RenderCounters()
        {
            if (_countersLabel == null || _provider == null) return;

            _counters.Clear();
            _counters.Append("PooledViewAssetProvider\n");
            _counters.Append($"  active={_provider.ActiveCount}  pooled={_provider.PooledCount}  total={_provider.TotalInstanceCount}\n");
            _counters.Append($"  DuplicateReleaseCount ..... {_provider.DuplicateReleaseCount}\n");
            _counters.Append($"  ForeignReleaseCount ....... {_provider.ForeignReleaseCount}\n");
            _counters.Append($"  ExternallyDestroyedCount .. {_provider.ExternallyDestroyedCount}\n");
            _counters.Append($"  AdmissionRejectedCount .... {_provider.AdmissionRejectedCount}\n");

            foreach (var key in new[] { Cube, Sphere, Capsule })
            {
                _counters.Append($"  {key,-8} active={_provider.GetActiveCount(key)} pooled={_provider.GetPooledCount(key)} " +
                                 $"warm={_provider.IsWarm(key)} refs={_provisioner.GetReferenceCount(key)}\n");
            }

            _counters.Append($"\nChunkViewProvisioner\n");
            _counters.Append($"  chunks={_provisioner.ChunkCount}  warm={_provisioner.WarmChunkCount}  trackedKeys={_provisioner.TrackedKeyCount}\n");
            foreach (var pair in _provisioner.ChunkStates)
            {
                _counters.Append($"  {pair.Key} = {pair.Value}\n");
            }

            _countersLabel.text = _counters.ToString();
        }

        // ------------------------------------------------------------- autorun

        /// <summary>
        /// Clicks this scene's buttons in order and asserts the outcomes the README promises.
        /// Ordering matters: the admission-cap step needs a key with free capacity, and the
        /// shared-key steps need chunk A warmed before chunk B.
        /// </summary>
        private IEnumerator Autorun()
        {
            var run = new ShowcaseAutorun("PoolAndChunks");
            var wait = new WaitForSeconds(ShowcaseAutorun.StepDelay);
            yield return wait;

            Acquire(Cube);
            run.Check("acquire-cube", "active(cube)", 1, _provider.GetActiveCount(Cube));
            yield return wait;

            ReleaseLast(Cube);
            run.Check("release-cube", "active(cube)", 0, _provider.GetActiveCount(Cube));
            run.CheckAtLeast("release-cube-pooled", "pooled(cube)", 1, _provider.GetPooledCount(Cube));
            yield return wait;

            DuplicateRelease();
            run.Check("duplicate-release", "DuplicateReleaseCount", 1, _provider.DuplicateReleaseCount);
            yield return wait;

            ForeignRelease();
            run.Check("foreign-release", "ForeignReleaseCount", 1, _provider.ForeignReleaseCount);
            yield return wait;

            Acquire(Cube);
            ExternalDestroy();
            SweepDestroyed();
            run.CheckAtLeast("external-destroy-sweep", "ExternallyDestroyedCount", 1, _provider.ExternallyDestroyedCount);
            run.Check("external-destroy-reconciled", "active(cube)", 0, _provider.GetActiveCount(Cube));
            yield return wait;

            HitAdmissionCap();
            run.Check("admission-cap", "AdmissionRejectedCount", 2, _provider.AdmissionRejectedCount);
            run.Check("admission-cap-granted", "active(sphere)", MaxActivePerKey, _provider.GetActiveCount(Sphere));
            yield return wait;

            run.Check("dispose-destroy", "instanceAliveAfterDispose", false, DisposePolicy(OutstandingLeasePolicy.Destroy));
            yield return wait;

            run.Check("dispose-detach", "instanceAliveAfterDispose", true, DisposePolicy(OutstandingLeasePolicy.Detach));
            yield return wait;

            ResetAll();
            run.Check("reset-all", "active", 0, _provider.ActiveCount);
            yield return wait;

            WarmChunk(ChunkA, ChunkAKeys);
            yield return new WaitUntil(() => !_chunkOperationInFlight);
            run.CheckTrue("warm-chunk-a", "loaded(chunk-a)", _provisioner.IsChunkLoaded(ChunkA));
            run.Check("warm-chunk-a-refs", "refs(sphere)", 1, _provisioner.GetReferenceCount(Sphere));
            yield return wait;

            WarmChunk(ChunkB, ChunkBKeys);
            yield return new WaitUntil(() => !_chunkOperationInFlight);
            run.Check("warm-chunk-b-shared-key", "refs(sphere)", 2, _provisioner.GetReferenceCount(Sphere));
            yield return wait;

            ReleaseChunk(ChunkA);
            run.Check("release-chunk-a-shared-key-survives", "refs(sphere)", 1, _provisioner.GetReferenceCount(Sphere));
            run.CheckTrue("release-chunk-a-b-still-warm", "loaded(chunk-b)", _provisioner.IsChunkLoaded(ChunkB));
            yield return wait;

            ReleaseChunk(ChunkB);
            run.Check("release-chunk-b", "refs(sphere)", 0, _provisioner.GetReferenceCount(Sphere));
            run.Check("release-chunk-b-chunks", "chunks", 0, _provisioner.ChunkCount);
            yield return wait;

            ReleaseWhileWarming();
            yield return new WaitUntil(() => !_chunkOperationInFlight);
            run.Check("release-while-warming", "tracked(chunk-c)", false, _provisioner.IsChunkTracked(ChunkC));
            yield return wait;

            CycleChunks();
            yield return new WaitUntil(() => !_chunkOperationInFlight);
            run.Check("cycle-chunks", "chunks", 0, _provisioner.ChunkCount);
            run.Check("cycle-chunks-keys", "trackedKeys", 0, _provisioner.TrackedKeyCount);
            yield return wait;

            run.Finish();
        }
    }
}
