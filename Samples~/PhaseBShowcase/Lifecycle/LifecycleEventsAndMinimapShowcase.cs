using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.View;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Scene 3: a scripted, offline snapshot sequence through <see cref="DotsEntityView"/> and the
    /// drain — full → delta → AOI exit / re-entry → reconnect reset → teardown — with the
    /// <see cref="NetworkEntityLifecycle"/> event log, a UI Toolkit minimap fed by
    /// <see cref="MinimapBootstrap"/>, name-plate labels through <see cref="ViewOverlayReconciler{T}"/>,
    /// and camera follow with target switch and teleport.
    /// </summary>
    /// <remarks>
    /// No backend: the buttons call the three <c>IEntityView</c> methods exactly as
    /// <c>WorldViewBinder.Tick</c> would for each wire event, so the event log shows the documented
    /// sequence from <c>Documentation~/NETWORK-LIFECYCLE.md</c> verbatim.
    /// </remarks>
    public sealed class LifecycleEventsAndMinimapShowcase : MonoBehaviour
    {
        private const string Me = "uuid-me";
        private static readonly string[] Remotes = { "uuid-a", "uuid-b" };
        private const string Mob = "mob-1";

        private World _world;
        private Transform _templates;
        private PooledViewAssetProvider _provider;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private readonly List<ScriptableObject> _assets = new List<ScriptableObject>();
        private CameraFollowConfig _camera;

        private DotsEntityView _view;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly Dictionary<string, float2> _positions = new Dictionary<string, float2>();
        private readonly List<NetworkEntitySpawned> _pendingSpawns = new List<NetworkEntitySpawned>();

        // Every lifecycle event this run has seen, as "spawn:<id>" / "despawn:<id>:<reason>".
        // Kept across adapter reinstalls, so the self-test can assert on reasons raised by a
        // view that has since been replaced.
        private readonly List<string> _events = new List<string>();
        private bool _adapterInstalled;

        private ViewOverlayReconciler<Label> _plates;
        private VisualElement _plateLayer;
        private VisualElement _minimapPanel;
        private readonly List<VisualElement> _dots = new List<VisualElement>();
        private uint _minimapVersion;

        private Label _counters;
        private ShowcaseUi.RollingLog _log;
        private readonly StringBuilder _text = new StringBuilder();
        private Coroutine _autoplay;

        /// <summary>False until Initialise has completed; every per-frame method checks it.</summary>
        private bool _ready;

        private void Start()
        {
            try
            {
                Initialise();

                // Not simply true: Initialise disables the component instead of throwing when
                // the world or the UI document is missing, and that is not a ready scene.
                _ready = enabled;
            }
            catch (Exception exception)
            {
                // A half-initialised bootstrap would otherwise NullReference every frame while the
                // headless run hangs to its outer timeout. Stop the component and fail loudly.
                Debug.LogException(exception);
                enabled = false;
                if (ShowcaseAutorun.Requested) ShowcaseAutorun.Abort("LifecycleEventsAndMinimap", exception);
            }
        }

        /// <summary>Scene setup. Any throw here is caught by <see cref="Start"/>.</summary>
        private void Initialise()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null) { Debug.LogError("[PhaseBShowcase] No default world."); enabled = false; return; }

            var root = ShowcaseUi.Root(this);
            if (root == null) { enabled = false; return; }
            _counters = ShowcaseUi.Label(root, "counters");
            _log = new ShowcaseUi.RollingLog(ShowcaseUi.Label(root, "log"), 18);
            _plateLayer = root.Q<VisualElement>("plates");
            _minimapPanel = root.Q<VisualElement>("minimap");

            _templates = new GameObject("Templates").transform;
            _provider = new PooledViewAssetProvider(defaultPoolSize: 4, maxPoolSize: 16);
            _provider.RegisterPrefab("capsule-local", PrimitiveTemplates.Create("capsule-local", PrimitiveType.Capsule, new Color(1f, 0.85f, 0.2f), _templates));
            _provider.RegisterPrefab("capsule", PrimitiveTemplates.Create("capsule", PrimitiveType.Capsule, new Color(0.25f, 0.6f, 0.9f), _templates));
            _provider.RegisterPrefab("cube", PrimitiveTemplates.Create("cube", PrimitiveType.Cube, new Color(0.85f, 0.35f, 0.25f), _templates));
            _registry = new EntityViewRegistry(_provider);
            // The package's default scope, deliberately. Views is Root-scoped because it is a
            // service that outlives a scene, and the Default World this scene runs in may already
            // have it installed that way by the host project - asking for Session then throws
            // "already installed as Root-scoped and cannot be re-installed as Session-scoped".
            // Teardown below is targeted per module instead, which needs no scope agreement.
            DotsViewBootstrap.Install(_world, _registry);

            BuildCatalog();
            // PooledViewAssetProvider.PrewarmAsync instantiates synchronously and hands back a
            // completed task, so discarding it loses nothing today; the continuation is still
            // attached so a future provider that really does fault cannot fail in silence.
            foreach (var pair in _catalog.PoolSizesByKey())
            {
                var key = pair.Key;
                _ = _provider.PrewarmAsync(key, pair.Value).ContinueWith(
                    task => Debug.LogError($"[PhaseBShowcase] Prewarm of '{key}' failed: {task.Exception}"),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }

            MinimapBootstrap.Install(_world, MinimapPlane.XZ);
            _camera = new CameraFollowConfig { Camera = Camera.main, Offset = new float3(0f, 10f, -9f), SmoothTime = 0.25f, TeleportDistance = 15f };
            CameraFollowBootstrap.Install(_world, _camera);

            _plates = new ViewOverlayReconciler<Label>(new PlatePresenter(_plateLayer, root));
            InstallAdapter();

            ShowcaseUi.OnClick(root, "full", FullSnapshot);
            ShowcaseUi.OnClick(root, "delta", Delta);
            ShowcaseUi.OnClick(root, "aoi-exit", () => AoiExit("uuid-b"));
            ShowcaseUi.OnClick(root, "aoi-reentry", () => AoiReentry("uuid-b"));
            ShowcaseUi.OnClick(root, "reset", ReconnectReset);
            ShowcaseUi.OnClick(root, "external-destroy", ExternalDestroy);
            ShowcaseUi.OnClick(root, "teardown", Teardown);
            ShowcaseUi.OnClick(root, "reinstall", InstallAdapter);
            ShowcaseUi.OnClick(root, "autoplay", ToggleAutoplay);
            ShowcaseUi.OnClick(root, "follow-me", () => Follow(Me));
            ShowcaseUi.OnClick(root, "follow-a", () => Follow("uuid-a"));
            ShowcaseUi.OnClick(root, "teleport", Teleport);
            ShowcaseUi.OnClick(root, "follow-none", FollowNothing);
            ShowcaseUi.OnClick(root, "follow-two", FollowTwo);
            ShowcaseUi.OnClick(root, "reset-smoothing", ResetSmoothing);
            ShowcaseUi.OnClick(root, "toggle-switch", ToggleSwitchPolicy);
            ShowcaseUi.OnClick(root, "toggle-multi", ToggleMultiTargetPolicy);
            ShowcaseUi.OnClick(root, "clear-log", _log.Clear);

            _log.Add("ready — press Full snapshot, or Autoplay for the whole sequence");

            if (ShowcaseAutorun.Requested) StartCoroutine(Autorun());
        }

        private void OnDestroy()
        {
            if (_autoplay != null) StopCoroutine(_autoplay);
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _plates?.Clear();
            if (_world != null && _world.IsCreated)
            {
                DotsNetcodeBootstrap.Uninstall(_world, destroyMirrors: true);

                // Named modules, not UninstallAll and not a scope sweep. This is the Default World
                // and the host project shares it: a blanket teardown would take its modules too,
                // and a Session sweep would miss Root-scoped Views. Each Uninstall is safe on a
                // world that never had the module, and safe twice.
                CameraFollowBootstrap.Uninstall(_world);
                MinimapBootstrap.Uninstall(_world);
                DotsViewBootstrap.Uninstall(_world);
            }

            _catalog?.Dispose();
            if (_provider != null && !_provider.IsDisposed) _provider.Dispose();
            foreach (var asset in _assets) if (asset != null) Destroy(asset);
            if (_templates != null) Destroy(_templates.gameObject);
        }

        // ---------------------------------------------------------------- setup

        private ViewConfig Config(string key, float scale, float lift)
        {
            var config = ScriptableObject.CreateInstance<ViewConfig>();
            config.name = key;
            config.Configure(key, pool: 4, uniformScale: scale, position: new Vector3(0f, lift, 0f));
            _assets.Add(config);
            return config;
        }

        private void BuildCatalog()
        {
            var library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            library.Configure(
                new ViewArchetypeLibrary.Entry { Name = "player-local", Config = Config("capsule-local", 1f, 1f) },
                new ViewArchetypeLibrary.Entry { Name = "player-remote", Config = Config("capsule", 1f, 1f) },
                new ViewArchetypeLibrary.Entry { Name = "mob", Config = Config("cube", 0.8f, 0.4f) });
            _assets.Add(library);
            _catalog = new ViewConfigCatalog();
            _catalog.BuildOrThrow(library, key => _provider.IsRegistered(key));
            _catalog.Install(_world);
        }

        /// <summary>A fresh adapter: new view, new lifecycle subscriptions. After a teardown the old view still believes its ids are live, so it is replaced rather than reused.</summary>
        private void InstallAdapter()
        {
            if (_adapterInstalled) { _log.Add("adapter already installed"); return; }
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver("player-local", null,
                    new TypeArchetypeResolver.Rule("player", "player-remote"),
                    new TypeArchetypeResolver.Rule("mob", "mob")),
                SnapshotSpaceMapping.XZPlane,
                minimap: new TypeMinimapCategoryResolver(0,
                    new TypeMinimapCategoryResolver.Rule("player", 1),
                    new TypeMinimapCategoryResolver.Rule("mob", 2)));

            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntitySpawned e) =>
            {
                _log.Add($"+ Spawned {e.EntityId} ({e.EntityType}{(e.IsLocal ? ", local" : "")}) {e.Entity}");
                _events.Add($"spawn:{e.EntityId}");

                // The event is raised from inside the drain system's update. Adding a component
                // here would be a structural change mid-update, so the work is queued and applied
                // from LateUpdate instead, where the world is between system updates.
                _pendingSpawns.Add(e);
            }));
            _subscriptions.Add(_view.Lifecycle.Subscribe((NetworkEntityDespawned e) =>
            {
                _log.Add($"- Despawned {e.EntityId} reason {e.Reason} {e.Entity}");
                _events.Add($"despawn:{e.EntityId}:{e.Reason}");
            }));

            DotsNetcodeBootstrap.Install(_world, _view);
            _adapterInstalled = true;
            _positions.Clear();
            _log.Add("adapter installed: fresh DotsEntityView + NetworkEntityLifecycle");
        }

        // ---------------------------------------------------------------- scripted wire events

        private IEntityView Wire => _view;

        private bool Ready()
        {
            if (_adapterInstalled) return true;
            _log.Add("adapter torn down — Reinstall first");
            return false;
        }

        private void FullSnapshot()
        {
            if (!Ready()) return;
            _log.Add("== full snapshot (keyframe): Spawn + SetState for every listed id");
            SpawnAt(Me, true, "player", new float2(0f, 0f));
            SpawnAt(Remotes[0], false, "player", new float2(4f, 2f));
            SpawnAt(Remotes[1], false, "player", new float2(-4f, 3f));
            SpawnAt(Mob, false, "mob", new float2(2f, -4f));
        }

        private void SpawnAt(string id, bool isLocal, string type, float2 at)
        {
            Wire.Spawn(id, isLocal, type);
            _positions[id] = at;
            Wire.SetState(id, at.x, at.y, 100, 100);
        }

        private void Delta()
        {
            if (!Ready()) return;
            _log.Add("== delta: SetState only — no lifecycle event expected");
            var keys = new List<string>(_positions.Keys);
            foreach (var id in keys)
            {
                var next = _positions[id] + new float2(UnityEngine.Random.Range(-1.5f, 1.5f), UnityEngine.Random.Range(-1.5f, 1.5f));
                _positions[id] = next;
                Wire.SetState(id, next.x, next.y, 100, 100);
            }
        }

        private void AoiExit(string id)
        {
            if (!Ready()) return;
            if (!_positions.ContainsKey(id)) { _log.Add($"{id} is not present"); return; }
            _log.Add($"== AOI exit {id}: the world stops listing it → Despawn (not a death)");
            Wire.Despawn(id);
            _positions.Remove(id);
        }

        private void AoiReentry(string id)
        {
            if (!Ready()) return;
            if (_positions.ContainsKey(id)) { _log.Add($"{id} is already present"); return; }
            _log.Add($"== AOI re-entry {id}: Spawn again → a new Entity for the same id");
            SpawnAt(id, false, "player", new float2(-4f, 3f));
        }

        private void ReconnectReset()
        {
            if (!Ready()) return;
            _log.Add("== reconnect: BeginGeneration drops the old session, then the new keyframe arrives");

            // BeginGeneration first, with no despawn loop before it. Despawning every id first would
            // leave nothing alive by the time the drain reached the Reset, so the reset would have
            // nothing to report and SessionReset would never appear - the one reason this step
            // exists to show. A real reconnect is this shape anyway: the session is dropped, and a
            // new keyframe arrives.
            var generation = _view.BeginGeneration();
            _positions.Clear();
            _log.Add($"BeginGeneration() -> generation {generation}: live mirrors despawn with SessionReset, " +
                     "and any command still queued from the old session is dropped");
            FullSnapshot();
        }

        private void Teardown()
        {
            if (!Ready()) return;
            _log.Add("== teardown: Uninstall(destroyMirrors: true) → one Despawned(Teardown) per present id");
            DotsNetcodeBootstrap.Uninstall(_world, destroyMirrors: true);
            _adapterInstalled = false;
            _positions.Clear();
        }

        private void ToggleAutoplay()
        {
            if (_autoplay != null) { StopCoroutine(_autoplay); _autoplay = null; _log.Add("autoplay stopped"); return; }
            _autoplay = StartCoroutine(Autoplay());
        }

        private IEnumerator Autoplay()
        {
            var wait = new WaitForSeconds(1.5f);
            if (!_adapterInstalled) InstallAdapter();
            FullSnapshot(); yield return wait;
            Delta(); yield return wait;
            Delta(); yield return wait;
            AoiExit("uuid-b"); yield return wait;
            AoiReentry("uuid-b"); yield return wait;
            ReconnectReset(); yield return wait;
            Follow("uuid-a"); yield return wait;
            Follow(Me); yield return wait;
            Teleport(); yield return wait;
            Teardown(); yield return wait;
            InstallAdapter();
            _autoplay = null;
            _log.Add("autoplay finished");
        }

        /// <summary>
        /// Destroys a mirror entity behind the adapter's back. The view still believes the id is
        /// live, so the drain notices the entity is gone and reports ExternalDestruction — the one
        /// despawn reason no wire message produces.
        /// </summary>
        private void ExternalDestroy()
        {
            if (!Ready()) return;

            string victim = null;
            foreach (var id in _positions.Keys)
            {
                if (id == Me) continue;
                victim = id;
                break;
            }

            if (victim == null) { _log.Add("spawn a remote entity first"); return; }

            var entity = Mirror(victim);
            if (entity == Entity.Null)
            {
                _log.Add($"no mirror for {victim} yet — the drain applies queued spawns on the next world update");
                return;
            }

            _world.EntityManager.DestroyEntity(entity);

            // Detection is command-driven, and that is the whole point of the reason. The drain has
            // no callback on entity destruction; it notices the mirror is gone only when the next
            // command for that id arrives - ApplySpawn and ApplyState both check IsLiveMirror - or
            // at teardown. A real server has no idea the client destroyed anything and keeps
            // sending state for the id, so the scene must keep talking about it too. Going quiet
            // here is what made this step look like the reason never fires: with no further command
            // there is simply nothing for the drain to notice.
            var last = _positions[victim];
            Wire.SetState(victim, last.x, last.y, 100, 100);
            _positions.Remove(victim);

            _log.Add($"== destroyed the mirror entity of {victim} directly, then sent one more state " +
                     "for that id -> the drain reports Despawned(ExternalDestruction)");
        }

        // ---------------------------------------------------------------- camera

        private Entity Mirror(string id)
        {
            var manager = _world.EntityManager;
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);
            for (var i = 0; i < entities.Length; i++)
            {
                if (manager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted)) return entities[i];
            }

            return Entity.Null;
        }

        private void Follow(string id)
        {
            var target = Mirror(id);
            if (target == Entity.Null) { _log.Add($"no mirror for {id}"); return; }
            var manager = _world.EntityManager;
            using (var tagged = manager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowTarget>()))
            {
                if (!tagged.IsEmpty) manager.RemoveComponent<CameraFollowTarget>(tagged);
            }

            manager.AddComponent<CameraFollowTarget>(target);
            _log.Add($"camera target → {id} (switch policy {_camera.TargetSwitch})");
        }

        private void Teleport()
        {
            if (!Ready() || !_positions.ContainsKey(Me)) { _log.Add("spawn first"); return; }
            var next = _positions[Me] + new float2(30f, 0f);
            _positions[Me] = next;
            Wire.SetState(Me, next.x, next.y, 100, 100);
            _log.Add($"server teleport of {Me} by 30 units → beyond TeleportDistance {_camera.TeleportDistance}: camera snaps, velocity resets");
        }

        private void ToggleSwitchPolicy()
        {
            _camera.TargetSwitch = _camera.TargetSwitch == CameraFollowSwitchPolicy.Snap ? CameraFollowSwitchPolicy.Smooth : CameraFollowSwitchPolicy.Snap;
            _log.Add($"camera TargetSwitch = {_camera.TargetSwitch}");
        }

        private void ToggleMultiTargetPolicy()
        {
            _camera.MultipleTargets = _camera.MultipleTargets == CameraFollowMultiTargetPolicy.HoldAndReport
                ? CameraFollowMultiTargetPolicy.FollowLowestIndex
                : CameraFollowMultiTargetPolicy.HoldAndReport;
            _log.Add($"camera MultipleTargets = {_camera.MultipleTargets}");
        }

        /// <summary>Removes every target tag. With nothing to follow the camera holds its last pose.</summary>
        private void ClearTargets()
        {
            var manager = _world.EntityManager;
            using var tagged = manager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowTarget>());
            if (!tagged.IsEmpty) manager.RemoveComponent<CameraFollowTarget>(tagged);
        }

        private void FollowNothing()
        {
            ClearTargets();
            _log.Add("camera target cleared — with no target the camera holds its last pose");
        }

        /// <summary>
        /// Tags two entities at once. Under HoldAndReport the camera refuses to pick and holds;
        /// under FollowLowestIndex it follows the lower entity index.
        /// </summary>
        private void FollowTwo()
        {
            var first = Mirror(Me);
            var second = Mirror(Remotes[0]);
            if (first == Entity.Null || second == Entity.Null)
            {
                _log.Add("need both the local player and uuid-a present — run a full snapshot first");
                return;
            }

            ClearTargets();
            var manager = _world.EntityManager;
            manager.AddComponent<CameraFollowTarget>(first);
            manager.AddComponent<CameraFollowTarget>(second);
            _log.Add($"two camera targets tagged; policy {_camera.MultipleTargets}");
        }

        /// <summary>Zeroes the smoothing velocity so the next frame starts from rest.</summary>
        private void ResetSmoothing()
        {
            CameraFollowBootstrap.ResetSmoothing(_world);
            _log.Add("CameraFollowBootstrap.ResetSmoothing(world) — smoothing velocity zeroed");
        }

        // ---------------------------------------------------------------- per-frame UI

        private void LateUpdate()
        {
            if (!_ready) return;

            ApplyPendingSpawns();
            _plates.Sync(OverlayBuffer(), Camera.main, maxDistance: 80f);
            RenderMinimap();
            RenderCounters();
        }

        /// <summary>
        /// Applies the structural changes queued by the spawn subscriber. Entities that have already
        /// gone away again (a despawn in the same batch) are skipped.
        /// </summary>
        private void ApplyPendingSpawns()
        {
            if (_pendingSpawns.Count == 0) return;

            var manager = _world.EntityManager;
            foreach (var spawned in _pendingSpawns)
            {
                if (!manager.Exists(spawned.Entity)) continue;

                if (!manager.HasComponent<ViewOverlayAnchor>(spawned.Entity))
                {
                    manager.AddComponentData(spawned.Entity, new ViewOverlayAnchor { WorldOffset = new float3(0f, 2.2f, 0f) });
                }

                if (spawned.IsLocal && !manager.HasComponent<CameraFollowTarget>(spawned.Entity))
                {
                    manager.AddComponent<CameraFollowTarget>(spawned.Entity);
                }
            }

            _pendingSpawns.Clear();
        }

        private ViewOverlayBuffer OverlayBuffer()
        {
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewOverlayBuffer>());
            return query.IsEmpty ? null : _world.EntityManager.GetComponentObject<ViewOverlayBuffer>(query.GetSingletonEntity());
        }

        private void RenderMinimap()
        {
            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            if (_minimapPanel == null || buffer == null || !buffer.Entries.IsCreated) return;
            if (buffer.Version == _minimapVersion) return;
            _minimapVersion = buffer.Version;

            const float extent = 40f;
            var size = _minimapPanel.resolvedStyle.width > 0f ? _minimapPanel.resolvedStyle.width : 160f;
            var scale = size / extent;

            // Centre on the local player when present, like a real minimap.
            var centre = float2.zero;
            for (var i = 0; i < buffer.Entries.Length; i++) if (buffer.Entries[i].IsLocal) centre = buffer.Entries[i].Position;

            while (_dots.Count < buffer.Entries.Length)
            {
                var dot = new VisualElement();
                dot.AddToClassList("dot");
                _minimapPanel.Add(dot);
                _dots.Add(dot);
            }

            for (var i = 0; i < _dots.Count; i++)
            {
                var dot = _dots[i];
                if (i >= buffer.Entries.Length) { dot.style.display = DisplayStyle.None; continue; }
                var entry = buffer.Entries[i];
                var offset = (entry.Position - centre) * scale;
                dot.style.display = DisplayStyle.Flex;
                dot.style.left = size * 0.5f + offset.x - 4f;
                dot.style.top = size * 0.5f - offset.y - 4f;
                dot.EnableInClassList("dot-local", entry.IsLocal);
                dot.EnableInClassList("dot-player", !entry.IsLocal && entry.Category == 1);
                dot.EnableInClassList("dot-mob", entry.Category == 2);
            }
        }

        private void RenderCounters()
        {
            if (_counters == null) return;
            var lifecycle = _view?.Lifecycle;
            var minimap = MinimapBootstrap.InstalledBuffer(_world);
            var follow = _world.GetExistingSystemManaged<CameraFollowSystem>();

            _text.Clear();
            _text.AppendLine($"adapter installed {_adapterInstalled}   ids the view holds {_view?.Count ?? 0}   pending commands {_view?.PendingCommands ?? 0}");
            _text.AppendLine($"lifecycle: spawned {lifecycle?.SpawnedCount ?? 0}   despawned {lifecycle?.DespawnedCount ?? 0}   subscribers {lifecycle?.SubscriberCount ?? 0}");
            _text.AppendLine($"mirror entities {MirrorCount()}   live views {_registry.Count}");
            _text.AppendLine($"minimap entries {minimap?.Count ?? 0}   version {minimap?.Version ?? 0}");
            _text.AppendLine($"plates: elements {_plates.ElementCount}   visible {_plates.VisibleCount}   hidden {_plates.HiddenCount}");
            _text.AppendLine($"camera: target {(follow != null ? follow.CurrentTarget.ToString() : "n/a")}   velocity {(follow != null ? math.length(follow.Velocity) : 0f):F2}   switch {_camera.TargetSwitch}   teleport > {_camera.TeleportDistance}");
            _counters.text = _text.ToString();
        }

        private int MirrorCount()
        {
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            return query.CalculateEntityCount();
        }

        // ---------------------------------------------------------------- overlay presenter (UI Toolkit)

        private sealed class PlatePresenter : IViewOverlayPresenter<Label>
        {
            private readonly VisualElement _layer;
            private readonly VisualElement _root;
            private readonly Stack<Label> _pool = new Stack<Label>();

            public PlatePresenter(VisualElement layer, VisualElement root)
            {
                _layer = layer;
                _root = root;
            }

            public Label Acquire(in ViewOverlayData data)
            {
                var label = _pool.Count > 0 ? _pool.Pop() : new Label();
                label.AddToClassList("plate");
                label.text = $"view {data.ViewId}";
                _layer?.Add(label);
                return label;
            }

            public void Place(Label label, in ViewOverlayData data, in ViewOverlayPlacement placement)
            {
                // WorldToScreenPoint is bottom-left origin; the panel is top-left. Flip, then map
                // screen pixels to panel units so the label lands right under any panel scaling.
                var screen = new Vector2(placement.Screen.x, Screen.height - placement.Screen.y);
                var panel = _root.panel != null ? RuntimePanelUtils.ScreenToPanel(_root.panel, screen) : screen;
                label.style.display = DisplayStyle.Flex;
                label.style.left = panel.x - 40f;
                label.style.top = panel.y - 12f;
                label.text = $"view {data.ViewId}  {placement.Distance:F0} m";
            }

            public void Hide(Label label, in ViewOverlayData data, in ViewOverlayPlacement placement)
            {
                label.style.display = DisplayStyle.None;
            }

            public void Release(Label label)
            {
                label.RemoveFromHierarchy();
                _pool.Push(label);
            }
        }

        // ------------------------------------------------------------- autorun

        private bool Saw(string entry) => _events.Contains(entry);

        /// <summary>
        /// Waits for a condition, but never forever, so a semantic that stops holding fails the run
        /// instead of hanging it.
        /// </summary>
        private static IEnumerator WaitUntilOrTimeout(Func<bool> condition, float timeoutSeconds)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        private int CountReason(NetworkDespawnReason reason)
        {
            var suffix = ":" + reason;
            var total = 0;
            foreach (var entry in _events)
            {
                if (entry.StartsWith("despawn:", StringComparison.Ordinal) && entry.EndsWith(suffix, StringComparison.Ordinal)) total++;
            }

            return total;
        }

        /// <summary>
        /// Drives the scripted wire sequence and asserts the lifecycle events it must produce.
        /// Each step waits a full step delay so the drain system gets world updates to apply the
        /// queued commands - the events are published by the drain, not by the Spawn call.
        /// </summary>
        private IEnumerator Autorun()
        {
            var run = new ShowcaseAutorun("LifecycleEventsAndMinimap");
            var wait = new WaitForSeconds(ShowcaseAutorun.StepDelay);
            yield return wait;

            FullSnapshot();
            yield return wait;
            run.Check("full-snapshot", "spawnedIds", 4, _events.FindAll(e => e.StartsWith("spawn:", StringComparison.Ordinal)).Count);
            run.Check("full-snapshot-mirrors", "mirrorEntities", 4, MirrorCount());

            var beforeDelta = _events.Count;
            Delta();
            yield return wait;
            run.Check("delta-raises-no-lifecycle-events", "eventsAfterDelta", beforeDelta, _events.Count);

            var beforeExit = Mirror("uuid-b");
            AoiExit("uuid-b");
            yield return wait;
            run.CheckTrue("aoi-exit", "despawn(uuid-b,Despawned)", Saw("despawn:uuid-b:Despawned"));

            AoiReentry("uuid-b");
            yield return wait;
            var afterReentry = Mirror("uuid-b");
            run.CheckTrue("aoi-reentry", "reentryMakesANewEntity", afterReentry != Entity.Null && afterReentry != beforeExit);

            var generationBefore = _view.Generation;
            ReconnectReset();
            yield return wait;
            run.CheckTrue("reconnect-generation-advanced", "generationAdvanced", _view.Generation > generationBefore);
            run.CheckAtLeast("reconnect-session-reset", "despawnsWithSessionReset", 1, CountReason(NetworkDespawnReason.SessionReset));

            // Bounded wait rather than a fixed pause: the event is raised when the drain processes
            // the follow-up state command, which is a world update away, not a wall-clock delay.
            ExternalDestroy();
            yield return WaitUntilOrTimeout(() => CountReason(NetworkDespawnReason.ExternalDestruction) > 0, 5f);
            run.CheckAtLeast("external-destroy", "despawnsWithExternalDestruction", 1, CountReason(NetworkDespawnReason.ExternalDestruction));

            Teardown();
            yield return wait;
            run.CheckAtLeast("teardown", "despawnsWithTeardown", 1, CountReason(NetworkDespawnReason.Teardown));
            run.Check("teardown-mirrors-destroyed", "mirrorEntities", 0, MirrorCount());

            InstallAdapter();
            yield return wait;
            FullSnapshot();
            yield return wait;
            run.Check("reinstall-adapter", "mirrorEntities", 4, MirrorCount());

            Follow(Me);
            yield return wait;
            var follow = _world.GetExistingSystemManaged<CameraFollowSystem>();
            run.CheckTrue("camera-follow-target", "hasTarget", follow != null && follow.CurrentTarget != Entity.Null);

            FollowNothing();
            yield return wait;
            run.Check("camera-no-target", "taggedTargets", 0, TargetCount());

            FollowTwo();
            yield return wait;
            run.Check("camera-two-targets", "taggedTargets", 2, TargetCount());

            // Read on the same frame: the follow system runs every update and would have
            // re-accumulated velocity by the time a step delay elapsed, so waiting first would
            // assert on a number ResetSmoothing never promised to hold.
            ResetSmoothing();
            var velocityAfterReset = follow != null ? math.length(follow.Velocity) : 0f;
            run.Check("camera-reset-smoothing", "velocityMagnitude", 0f, velocityAfterReset);
            yield return wait;

            var minimap = MinimapBootstrap.InstalledBuffer(_world);
            run.CheckAtLeast("minimap-entries", "minimapEntries", 1, minimap?.Count ?? 0);

            run.Finish();
        }

        private int TargetCount()
        {
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CameraFollowTarget>());
            return query.CalculateEntityCount();
        }
    }
}
