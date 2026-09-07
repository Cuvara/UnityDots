using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Groups;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Netcode;
using Cuvara.DOTS.Views;
using Cuvara.Netcode.View;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// The minimap on the netcode path: only mirrors the resolver said yes to are marked, the marker
    /// carries the category and locality, and an area-of-interest exit removes the entry the same
    /// frame — so the map can show only what the server replicated.
    /// </summary>
    public sealed class NetworkMinimapTests
    {
        private const string PlayerType = "player";
        private const string MobType = "mob";

        private World _world;
        private EntityManager _entityManager;
        private EntityViewRegistry _registry;
        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _library;
        private ViewConfig _config;

        [SetUp]
        public void SetUp()
        {
            _world = new World("Cuvara.DOTS.NetworkMinimapTests");
            _entityManager = _world.EntityManager;

            _registry = new EntityViewRegistry(new StubViewAssetProvider());
            DotsViewBootstrap.Install(_world, _registry);

            _config = ScriptableObject.CreateInstance<ViewConfig>();
            _config.Configure("player");
            _library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            _library.Configure(
                new ViewArchetypeLibrary.Entry { Name = "player-local", Config = _config },
                new ViewArchetypeLibrary.Entry { Name = "player-remote", Config = _config },
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = _config });
            _catalog = new ViewConfigCatalog();
            _catalog.Build(_library);
            _catalog.Install(_world);

            MinimapBootstrap.Install(_world, MinimapPlane.XZ);
        }

        [TearDown]
        public void TearDown()
        {
            DotsNetcodeBootstrap.Uninstall(_world);
            _catalog.Dispose();
            Object.DestroyImmediate(_library);
            Object.DestroyImmediate(_config);
            DotsModules.UninstallAll(_world);
            _world.Dispose();
        }

        private DotsEntityView NewView(IMinimapCategoryResolver minimap)
        {
            var view = new DotsEntityView(
                _catalog,
                new TypeArchetypeResolver("player-local", null,
                    new TypeArchetypeResolver.Rule(PlayerType, "player-remote"),
                    new TypeArchetypeResolver.Rule(MobType, "goblin")),
                SnapshotSpaceMapping.XZPlane,
                writeHealth: false,
                lifecycle: null,
                minimap: minimap);
            DotsNetcodeBootstrap.Install(_world, view);
            return view;
        }

        private void Tick()
        {
            _world.GetExistingSystemManaged<NetcodeSystemGroup>().Update();
            _world.GetExistingSystemManaged<ViewSystemGroup>().Update();
        }

        private Entity Find(string id)
        {
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkEntity>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var wanted = new FixedString64Bytes(id);
            for (var i = 0; i < entities.Length; i++)
            {
                if (_entityManager.GetComponentData<NetworkEntity>(entities[i]).Id.Equals(wanted)) return entities[i];
            }

            return Entity.Null;
        }

        [Test]
        public void Resolver_MarksOnlyTheKindsItNames_WithCategoryAndLocality()
        {
            var view = (IEntityView)NewView(new TypeMinimapCategoryResolver(
                localCategory: 9,
                new TypeMinimapCategoryResolver.Rule(PlayerType, 1)));

            view.Spawn("uuid-me", isLocal: true, type: PlayerType);
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            view.Spawn("uuid-e1", isLocal: false, type: MobType);
            Tick();

            var me = _entityManager.GetComponentData<MinimapMarker>(Find("uuid-me"));
            Assert.AreEqual(9, me.Category, "the local override wins over the kind rule");
            Assert.IsTrue(me.IsLocal);

            var remote = _entityManager.GetComponentData<MinimapMarker>(Find("uuid-a"));
            Assert.AreEqual(1, remote.Category);
            Assert.IsFalse(remote.IsLocal);

            Assert.IsFalse(_entityManager.HasComponent<MinimapMarker>(Find("uuid-e1")), "an unmapped kind is off the map, silently");
            Assert.AreEqual(2, MinimapBootstrap.InstalledBuffer(_world).Count);
        }

        [Test]
        public void NoResolver_MarksNothing()
        {
            var view = (IEntityView)NewView(null);
            view.Spawn("uuid-a", isLocal: false, type: PlayerType);
            Tick();

            Assert.IsFalse(_entityManager.HasComponent<MinimapMarker>(Find("uuid-a")));
            Assert.AreEqual(0, MinimapBootstrap.InstalledBuffer(_world).Count);
        }

        [Test]
        public void MinimapFollowsTheServersPosition_AndAoiExitRemovesTheEntryThatFrame()
        {
            var view = (IEntityView)NewView(new TypeMinimapCategoryResolver(null, new TypeMinimapCategoryResolver.Rule(MobType, 2)));
            view.Spawn("uuid-e1", isLocal: false, type: MobType);
            view.SetState("uuid-e1", 3f, 7f, 30, 30);
            Tick();

            var buffer = MinimapBootstrap.InstalledBuffer(_world);
            Assert.AreEqual(1, buffer.Count);
            Assert.AreEqual(new Unity.Mathematics.float2(3f, 7f), buffer.Entries[0].Position, "server (x, y) on the XZ plane");
            Assert.AreEqual(Find("uuid-e1"), buffer.Entries[0].Entity);
            Assert.AreEqual(-1f, buffer.Entries[0].HealthFraction, "replicated hp is on NetworkEntityState, not on the core Health the map reads");

            // AOI exit: the wire despawns it, the mirror goes, the map is empty the same frame.
            view.Despawn("uuid-e1");
            Tick();
            Assert.AreEqual(0, buffer.Count, "nothing the server no longer lists stays on the map");
        }

        [Test]
        public void AnEntityNeverReplicated_CannotAppear()
        {
            // Nothing in the adapter creates a marker except ApplySpawn, and ApplySpawn runs only for
            // ids the wire spawned. A view that never spawns an id produces a world with no markers.
            NewView(new TypeMinimapCategoryResolver(0, new TypeMinimapCategoryResolver.Rule(PlayerType, 1)));
            Tick();

            using var markers = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<MinimapMarker>());
            Assert.AreEqual(0, markers.CalculateEntityCount());
            Assert.AreEqual(0, MinimapBootstrap.InstalledBuffer(_world).Count);
        }
    }
}
