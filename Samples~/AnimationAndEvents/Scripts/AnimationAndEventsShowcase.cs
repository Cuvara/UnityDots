using System.Collections.Generic;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.Protocol.Messages;
using Cuvara.Netcode.Snapshot;
using Cuvara.Netcode.View;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace Cuvara.DOTS.Samples.AnimationAndEvents
{
    /// <summary>
    /// The pose and event paths through the DOTS netcode adapter, with no server and no network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scripted <see cref="DotsEntityView"/> stands in for a connection: it is driven with the
    /// same <c>IEntityView</c> / <c>IEntityPoseView</c> calls the binder makes against a live
    /// server, and everything downstream — the drain, <see cref="EntityPose"/>, the
    /// <see cref="NetworkGameEvent"/> buffer, <see cref="EntityPoseViewSystem"/> — is the
    /// production path.
    /// </para>
    /// <para>
    /// <b>Two buttons carry the argument.</b> <i>Stop sending action_seq</i>: the attacker keeps
    /// attacking and the swing counter freezes, because <c>action</c> never changes and there is no
    /// edge in it to find. <i>Stop sending events</i>: the victim's HP keeps falling and the damage
    /// log stops, because HP is state and a hit is an occurrence.
    /// </para>
    /// <para>
    /// The attacker's view carries a <see cref="SwingFlash"/>, which is all an
    /// <see cref="IEntityAnimationReceiver"/> has to be: it is told the action and whether it was
    /// RE-entered, and does not compare counters, remember what it last drew, or know that the
    /// counter wraps.
    /// </para>
    /// </remarks>
    public sealed class AnimationAndEventsShowcase : MonoBehaviour
    {
        private const string Attacker = "attacker";
        private const string Victim = "victim";
        private const string PlayerType = "player";
        private const string Archetype = "player-remote";

        [SerializeField] private float secondsPerSwing = 0.8f;
        [SerializeField] private int damagePerHit = 25;

        private World _world;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;
        private DotsEntityView _view;
        private Entity _viewEntity;

        private Label _poseLine;
        private Label _swingLine;
        private Label _eventLine;
        private Label _hpLine;
        private Button _toggleSeq;
        private Button _toggleEvents;

        private bool _sendActionSeq = true;
        private bool _sendEvents = true;
        private float _nextSwingAt;
        private uint _serverActionSeq;
        private int _victimHp = 400;
        private const int VictimMaxHp = 400;
        private readonly List<string> _log = new List<string>();

        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;

            // One definition, keyed to match the ViewConfig below. The provider makes a Unity
            // primitive when no prefab is given, which is what keeps this sample free of any
            // asset dependency at all.
            var provider = new PrimitiveViewAssetProvider(
                new[]
                {
                    new PrimitiveViewDefinition
                    {
                        Key = PlayerType,
                        Primitive = PrimitiveType.Capsule,
                        Color = Color.white,
                    },
                },
                poolRoot: null,
                verbose: false);

            // WARM IT FIRST. The provider reports IsWarm(key) false until this runs, and the spawn
            // system defers an entity whose key is cold — so without this the entities exist, carry
            // the right pose, and simply never get a view. Nothing errors; the seam has no
            // GameObject to talk to and the scene looks like a working scene doing nothing. That is
            // exactly how this sample shipped its first build, and why the package now has a test
            // asserting the receiver is CALLED rather than only that the pose arrived.
            provider.PrewarmAsync(PlayerType, 2);

            _registry = new EntityViewRegistry(provider);
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure(PlayerType);
            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(new ViewArchetypeLibrary.Entry { Name = Archetype, Config = _config });
            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            _view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver(Archetype, null, new TypeArchetypeResolver.Rule(PlayerType, Archetype)),
                SnapshotSpaceMapping.XZPlane);
            _viewEntity = DotsNetcodeBootstrap.Install(_world, _view);

            BindUi();

            // Exactly the calls the binder makes.
            ((IEntityView)_view).Spawn(Attacker, isLocal: false, type: PlayerType);
            ((IEntityView)_view).Spawn(Victim, isLocal: false, type: PlayerType);
            ((IEntityView)_view).SetState(Attacker, -2f, 0f, 400, 400);
            ((IEntityView)_view).SetState(Victim, 2f, 0f, _victimHp, VictimMaxHp);
        }

        private void BindUi()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _poseLine = root.Q<Label>("pose-line") ?? new Label();
            _swingLine = root.Q<Label>("swing-line") ?? new Label();
            _eventLine = root.Q<Label>("event-line") ?? new Label();
            _hpLine = root.Q<Label>("hp-line") ?? new Label();
            _toggleSeq = root.Q<Button>("toggle-seq");
            _toggleEvents = root.Q<Button>("toggle-events");

            if (_toggleSeq != null)
            {
                _toggleSeq.clicked += () =>
                {
                    _sendActionSeq = !_sendActionSeq;
                    _toggleSeq.text = _sendActionSeq ? "Stop sending action_seq" : "Resume action_seq";
                };
            }

            if (_toggleEvents != null)
            {
                _toggleEvents.clicked += () =>
                {
                    _sendEvents = !_sendEvents;
                    _toggleEvents.text = _sendEvents ? "Stop sending events" : "Resume events";
                };
            }
        }

        private void Update()
        {
            if (_view == null) return;

            // Attach the receiver BEFORE the first swing, not after it.
            //
            // Views are provisioned over a frame or two, and this sample's receiver is attached to
            // the instance when it first appears rather than baked into a prefab — so swinging on
            // frame 0 reported an action to a view that had no receiver yet, and the first swing
            // was never drawn. A built player showed 9 swings played against 10 sent, which was
            // briefly and wrongly diagnosed as a defect in EntityPoseViewSystem. It was this.
            //
            // A real game attaches the component in the prefab and has no such window.
            if (FindFlash() == null) return;

            if (Time.time >= _nextSwingAt)
            {
                _nextSwingAt = Time.time + Mathf.Max(0.1f, secondsPerSwing);
                Swing();
            }

            Redraw();

            // One self-check, once. See the probe sample for why a built player needs this: a
            // scene that failed to bind looks exactly like one that is working and idle.
            if (!_selfChecked && Time.time > 8f)
            {
                _selfChecked = true;
                var flash = FindFlash();
                Debug.Log($"[AnimationAndEvents] SELFCHECK serverSeq={_serverActionSeq} " +
                          $"swingsPlayed={(flash == null ? -1 : flash.Swings)} " +
                          $"eventsLogged={_log.Count} victimHp={_victimHp}");
            }
        }

        private bool _selfChecked;

        private void Swing()
        {
            // The server advances the counter on every ENTRY into an action, and Attacking is
            // retriggerable — so a second swing moves it even though the action is unchanged.
            _serverActionSeq = _serverActionSeq == uint.MaxValue ? 1u : _serverActionSeq + 1u;
            _victimHp = Mathf.Max(0, _victimHp - damagePerHit);

            ((IEntityView)_view).SetState(Attacker, -2f, 0f, 400, 400);
            ((IEntityPoseView)_view).SetPose(
                Attacker, facingBrad: 1u, action: SimAction.Attacking,
                actionSeq: _sendActionSeq ? _serverActionSeq : 0u);

            ((IEntityView)_view).SetState(Victim, 2f, 0f, _victimHp, VictimMaxHp);
            ((IEntityPoseView)_view).SetPose(
                Victim, facingBrad: 1u,
                action: _victimHp == 0 ? SimAction.Dead : SimAction.Idle,
                actionSeq: 0u);

            if (_sendEvents)
            {
                var events = new List<ResolvedGameEvent>
                {
                    new ResolvedGameEvent(
                        GameEventType.Damage, Attacker, Victim, damagePerHit, 0, GameEventFlags.None),
                };

                if (_victimHp == 0)
                {
                    events.Add(new ResolvedGameEvent(
                        GameEventType.Death, Attacker, Victim, 0, 0, GameEventFlags.None));
                }

                _view.EnqueueGameEvents(events);
            }

            if (_victimHp == 0) _victimHp = VictimMaxHp;
        }

        private void Redraw()
        {
            var em = _world.EntityManager;

            uint seq = 0;
            var action = SimAction.Unspecified;
            var attacker = Find(em, Attacker);
            if (attacker != Entity.Null && em.HasComponent<EntityPose>(attacker))
            {
                var pose = em.GetComponentData<EntityPose>(attacker);
                seq = pose.ActionSeq;
                action = pose.Action;
            }

            _poseLine.text = $"attacker action={action}  action_seq={seq}";

            var flash = FindFlash();
            _swingLine.text = flash == null
                ? "no IEntityAnimationReceiver on the view"
                : (_sendActionSeq
                    ? $"swings played: {flash.Swings}  — one per attack"
                    : $"swings played: {flash.Swings}  — frozen: action never changes, so there is no edge");

            // Read the buffer the drain filled THIS frame. It is cleared every drain, so a frame
            // with nothing in it is a frame in which nothing happened.
            if (em.HasBuffer<NetworkGameEvent>(_viewEntity))
            {
                var buffer = em.GetBuffer<NetworkGameEvent>(_viewEntity);
                for (var i = 0; i < buffer.Length; i++)
                {
                    var e = buffer[i];
                    _log.Insert(0, $"{e.Type} {e.SourceId} -> {e.TargetId} amount={e.Amount} " +
                                   $"(source resolved: {e.HasSource})");
                    if (_log.Count > 4) _log.RemoveAt(_log.Count - 1);
                }
            }

            _eventLine.text = _log.Count == 0 ? "(no events)" : string.Join("\n", _log);
            _hpLine.text = _sendEvents
                ? $"victim hp {_victimHp} / {VictimMaxHp}"
                : $"victim hp {_victimHp} / {VictimMaxHp}  — falling with no events to explain it";
        }

        private SwingFlash FindFlash()
        {
            var em = _world.EntityManager;
            var attacker = Find(em, Attacker);
            if (attacker == Entity.Null || !em.HasComponent<EntityViewLink>(attacker)) return null;

            var go = _registry.Get(em.GetComponentData<EntityViewLink>(attacker).ViewId);
            if (go == null) return null;

            // Attached on first sight rather than baked into a prefab: this sample's views are
            // primitives created at runtime, and the point is the interface, not the prefab.
            return go.GetComponent<SwingFlash>() ?? go.AddComponent<SwingFlash>();
        }

        private static Entity Find(EntityManager em, string id)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);
            for (var i = 0; i < entities.Length; i++)
            {
                if (em.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted)) return entities[i];
            }

            return Entity.Null;
        }

        private void OnDestroy()
        {
            if (_world == null || !_world.IsCreated) return;

            DotsNetcodeBootstrap.Uninstall(_world);
            DotsViewBootstrap.Uninstall(_world);
            _catalog?.Dispose();
            if (_library != null) Destroy(_library);
            if (_config != null) Destroy(_config);
        }
    }

    /// <summary>
    /// The entire consumer side of the animation seam.
    /// </summary>
    /// <remarks>
    /// It is told what the entity is doing and whether that is a NEW occurrence. It does not
    /// compare counters, does not remember what it last drew, and does not know that the counter
    /// wraps — all three of which are the package's job, and all three of which are what a view
    /// script gets wrong when it is handed the raw number instead.
    /// </remarks>
    public sealed class SwingFlash : MonoBehaviour, IEntityAnimationReceiver
    {
        public int Swings { get; private set; }

        private float _flashUntil;
        private Renderer _renderer;
        private Color _base;

        private void Awake()
        {
            _renderer = GetComponentInChildren<Renderer>();
            if (_renderer != null && _renderer.material != null) _base = _renderer.material.color;
        }

        public void OnAction(SimAction action, bool retriggered)
        {
            // A real consumer would call animator.SetTrigger here on `retriggered`, and
            // animator.SetInteger on the action itself.
            if (action != SimAction.Attacking) return;

            Swings++;
            _flashUntil = Time.time + 0.15f;
        }

        private void Update()
        {
            if (_renderer == null || _renderer.material == null) return;
            _renderer.material.color = Time.time < _flashUntil ? Color.red : _base;
        }
    }
}
