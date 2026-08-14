using System;
using System.Threading;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Netcode.Prediction;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.Auth;
using Cuvara.Netcode.Client;
using Cuvara.Netcode.Codec;
using Cuvara.Netcode.Diagnostics;
using Cuvara.Netcode.Prediction;
using Cuvara.Netcode.Transport;
using Cuvara.Netcode.View;
using Shared.GameLogic.Components;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Cuvara.DOTS.Samples.NetworkedPrediction
{
    /// <summary>
    /// Drop this on one GameObject in an otherwise empty scene, point it at a running backend, and
    /// press play. Replicated entities become ECS entities with pooled primitive views, and the
    /// local player is client-side predicted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the first thing that exercises the adapter and the prediction driver against a real
    /// server.</b> Their unit tests assert wiring — which coordinates reach the predictor, when
    /// <see cref="PredictedTransform"/> moves, that the mapping is shared — with hand-built snapshots.
    /// None of that proves a server's actual entity types resolve, that its coordinates arrive
    /// intact, or that exactly one thing writes the local transform every frame under real traffic.
    /// The overlay exists to make those four things observable rather than inferred.
    /// </para>
    /// <para>
    /// <b>Distinct from netcode's own DOTS sample</b>, which uses its own <c>DOTSEntityView</c> and
    /// its own combat systems. Nothing here touches that scene.
    /// </para>
    /// </remarks>
    public sealed class NetworkedPredictionSample : MonoBehaviour
    {
        [Header("Backend")]
        [Tooltip("Gateway host. The gateway only redirects; gameplay traffic goes to the game server it names.")]
        [SerializeField] private string gatewayHost = "127.0.0.1";

        [SerializeField] private int gatewayPort = 8000;

        [SerializeField] private string mapId = "map_01";

        [Header("Dev auth")]
        [Tooltip("Any stable string. Becomes the entity id, so it is also what isLocal is decided by.")]
        [SerializeField] private string userId = "dots-sample-1";

        [Tooltip("Must equal the server's JOIN_TOKEN_SECRET. Source backend/deploy/.env; the server refuses to start without it.")]
        [SerializeField] private string joinTokenSecret = "";

        [Header("Prediction")]
        [Tooltip("Off makes the adapter the only writer of LocalTransform — the A/B this sample exists for.")]
        [SerializeField] private bool predictionEnabled = true;

        [Tooltip("Must match the server's Locomotion.Speed for this entity.")]
        [SerializeField] private float moveSpeed = 5f;

        private World _world;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig[] _configs;
        private PrimitiveViewProvider _provider;
        private DotsEntityView _view;
        private WorldViewBinder _binder;
        private NetworkClient _client;
        private LocalMovePredictor _predictor;
        private CancellationTokenSource _cancellation;

        private long _inputTick;
        private string _status = "connecting";
        private EntityQuery _mirrors;
        private EntityQuery _predicted;

        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null)
            {
                _status = "no default ECS world";
                enabled = false;
                return;
            }

            BuildCatalog();

            _provider = new PrimitiveViewProvider(transform);
            _registry = new EntityViewRegistry(_provider);
            DotsViewBootstrap.Install(_world, _registry);
            _catalog.Install(_world);

            // Prewarm from the catalog's own pool sizes rather than a number typed here, which is
            // what PoolSizesByKey exists for.
            foreach (var pair in _catalog.PoolSizesByKey())
            {
                _provider.PrewarmAsync(pair.Key, pair.Value).GetAwaiter().GetResult();
            }

            // The server's entity kinds map to archetypes here. No catch-all: an unmapped kind is
            // refused and logged once, so a server that grows a new type says so instead of
            // rendering it as a player.
            var resolver = new TypeArchetypeResolver(
                localArchetype: "player-local",
                unknownArchetype: null,
                new TypeArchetypeResolver.Rule("player", "player-remote"),
                new TypeArchetypeResolver.Rule("mob", "mob"));

            _view = new DotsEntityView(_catalog, resolver, SnapshotSpaceMapping.XZPlane);
            DotsNetcodeBootstrap.Install(_world, _view);

            _binder = new WorldViewBinder(_view);

            _client = new NetworkClient(
                new NetworkSettings { GatewayHost = gatewayHost, GatewayPort = gatewayPort },
                new DefaultTransportFactory(),
                new ProtobufWireCodec(),
                new UnityNetLog(),
                new DevAuthProvider(userId, joinTokenSecret, TimeSpan.FromMinutes(30)));

            // Bounds and tick rate come from GameConstants — the same source the server compiled
            // against — rather than being restated here. A literal copy compiles, passes, and then
            // disagrees with the server the moment the shared package moves.
            _predictor = new LocalMovePredictor(new PredictionSettings(
                GameConstants.DefaultTickRate,
                predictionEnabled ? moveSpeed : 0f,
                new MapBounds(0f, 0f, GameConstants.DefaultMapWidth, GameConstants.DefaultMapHeight)));

            DotsPredictionBootstrap.Install(_world, _predictor, _client.World);

            _mirrors = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            _predicted = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PredictedTransform>());

            _cancellation = new CancellationTokenSource();
            Connect();
        }

        private async void Connect()
        {
            try
            {
                _status = "connecting";
                await _client.ConnectAsync(mapId, _cancellation.Token);
                _status = "connected";
            }
            catch (Exception exception)
            {
                _status = "failed: " + exception.Message;
                Debug.LogException(exception);
            }
        }

        private void Update()
        {
            if (_client == null) return;

            // Input is sampled and SENT here, not inside the prediction driver. The tick recorded
            // must be the tick that went to the server; a driver inventing its own would build a
            // buffer the server never saw, and replay against it diverges by construction.
            var moveX = Input.GetAxisRaw("Horizontal");
            var moveY = Input.GetAxisRaw("Vertical");

            if (_client.State == NetworkClientState.InWorld && (moveX != 0f || moveY != 0f))
            {
                _inputTick++;
                _client.Session?.SendInput(_inputTick, moveX, moveY);
                _predictor.RecordInput(_inputTick, moveX, moveY);
            }

            // Polls rather than subscribing, which is what WorldViewBinder is built for: the merged
            // world is already the answer, and despawn falls out of absence.
            _binder.Tick(_client.World, _client.UserId);
        }

        private void OnDestroy()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _client?.Dispose();

            if (_world is { IsCreated: true })
            {
                DotsPredictionBootstrap.Uninstall(_world);
                DotsNetcodeBootstrap.Uninstall(_world);
                DotsViewBootstrap.Uninstall(_world);
            }

            _catalog?.Dispose();
            if (_library != null) Destroy(_library);
            if (_configs != null)
            {
                foreach (var config in _configs)
                {
                    if (config != null) Destroy(config);
                }
            }
        }

        /// <summary>
        /// Builds the catalog in code so the sample needs no authored assets. A real project
        /// authors <see cref="ViewConfig"/> assets and lists them in a
        /// <see cref="ViewArchetypeLibrary"/>; this is the same data, typed.
        /// </summary>
        private void BuildCatalog()
        {
            ViewConfig Config(string key, float scale, float lift)
            {
                var config = ScriptableObject.CreateInstance<ViewConfig>();
                config.name = key;
                // The lift is the ART's half-height, authored as a config offset. It is deliberately
                // NOT folded into the space mapping — the entity stays on the plane the server
                // simulates on, and only the visual is raised.
                config.Configure(key, pool: 8, uniformScale: scale, position: new Vector3(0f, lift, 0f));
                return config;
            }

            _configs = new[]
            {
                Config("player-local", 1.2f, 1f),
                Config("player-remote", 1f, 1f),
                Config("mob", 0.8f, 0.5f),
            };

            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(
                new ViewArchetypeLibrary.Entry { Name = "player-local", Config = _configs[0] },
                new ViewArchetypeLibrary.Entry { Name = "player-remote", Config = _configs[1] },
                new ViewArchetypeLibrary.Entry { Name = "mob", Config = _configs[2] });

            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
        }

        /// <summary>
        /// The four things this sample exists to make observable, none of which a unit test can
        /// assert against a real server.
        /// </summary>
        private void OnGUI()
        {
            if (_client == null) return;

            var box = new Rect(10, 10, 460, 250);
            GUI.Box(box, "Networked prediction sample");

            var y = 32f;
            void Line(string text)
            {
                GUI.Label(new Rect(20, y, 440, 20), text);
                y += 18f;
            }

            Line($"state: {_client.State}   ({_status})");
            Line($"tick {_client.World.Tick}   ack {_client.World.AckTick}   entities {_client.World.Count}");

            // (1) the adapter spawned from real snapshots, and (3) exactly one entity is predicted.
            Line($"mirror entities: {_mirrors.CalculateEntityCount()}    predicted: {_predicted.CalculateEntityCount()}");

            var live = "";
            foreach (var pair in _provider.Live) live += $"{pair.Key}={pair.Value}  ";
            Line("pooled views: " + (live.Length == 0 ? "(none)" : live));

            Line($"prediction: {(_predictor.IsEnabled ? "on" : "OFF")}   pending {_predictor.PendingCount}" +
                 $"   replayed {_predictor.ReplayedSteps}");
            Line($"corrections: last {_predictor.LastCorrection:F3}   snaps {_predictor.Snaps}" +
                 $"   smoothed {_predictor.SmoothedCorrections}");
            Line($"dropped {_predictor.DroppedInputs}   rejected {_predictor.RejectedInputs}");

            // (2) what the server actually sent, beside where it was drawn. If these two ever stop
            // corresponding through the mapping, it is visible here rather than as drift.
            var local = LocalMirror();
            if (local != Entity.Null)
            {
                var manager = _world.EntityManager;
                var anchor = manager.GetComponentData<ReconciliationAnchor>(local);
                var transform = manager.GetComponentData<LocalTransform>(local);
                var isPredicted = manager.HasComponent<PredictedTransform>(local);

                Line($"server  ({anchor.ServerPosition.x:F2}, {anchor.ServerPosition.y:F2})" +
                     $"   anchor world ({anchor.Position.x:F2}, {anchor.Position.z:F2})");
                Line($"drawn   ({transform.Position.x:F2}, {transform.Position.z:F2})" +
                     $"   writer: {(isPredicted ? "predictor" : "adapter")}");
            }
            else
            {
                Line("no local mirror entity yet");
            }
        }

        private Entity LocalMirror()
        {
            using var entities = _mirrors.ToEntityArray(Unity.Collections.Allocator.Temp);
            var manager = _world.EntityManager;
            for (var i = 0; i < entities.Length; i++)
            {
                if (manager.GetComponentData<NetworkEntity>(entities[i]).IsLocal) return entities[i];
            }

            return Entity.Null;
        }
    }
}
