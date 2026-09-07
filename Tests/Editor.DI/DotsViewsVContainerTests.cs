using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.DI;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;

namespace Cuvara.DOTS.Tests.DI
{
    /// <summary>
    /// The VContainer registration path: what <c>RegisterDotsViews</c> resolves, what it installs,
    /// and that disposing the scope tears the module down. Compiled only when VContainer resolves
    /// — the same gate as <c>Cuvara.DOTS.DI</c> — so an absent VContainer shows here as
    /// <c>Cuvara.DOTS.Tests.DI == 0</c>, never as a silent pass.
    /// </summary>
    public sealed class DotsViewsVContainerTests
    {
        private sealed class StubProvider : IViewAssetProvider
        {
            private readonly HashSet<string> _warm = new HashSet<string>();
            public int Released;
            public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default) { _warm.Add(key); return Task.CompletedTask; }
            public bool IsWarm(string key) => true;
            public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null) => new GameObject(key);
            public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, CancellationToken cancellationToken = default) => Task.FromResult(Acquire(key, position, rotation, parent));
            public void ReleaseInstance(GameObject instance) { Released++; if (instance != null) UnityEngine.Object.DestroyImmediate(instance); }
            public void Release(string key) => _warm.Remove(key);
        }

        private World _world;
        private StubProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _world = new World("DotsViewsVContainerTests");
            _provider = new StubProvider();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated)
            {
                DotsModules.UninstallAll(_world);
                _world.Dispose();
            }
        }

        private IObjectResolver Build(World world)
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance<IViewAssetProvider>(_provider);
            builder.RegisterDotsViews(null, world);
            return builder.Build();
        }

        [Test]
        public void Build_InstallsTheViewModule_WithTheContainersRegistry()
        {
            using var container = Build(_world);

            Assert.IsTrue(DotsViewBootstrap.IsInstalled(_world));
            Assert.AreSame(container.Resolve<EntityViewRegistry>(), DotsViewBootstrap.InstalledRegistry(_world), "one registry, container-owned, world-borrowed");
            Assert.AreSame(container.Resolve<EntityViewRegistry>(), container.Resolve<EntityViewRegistry>(), "singleton");
            Assert.IsTrue(DotsModules.IsInstalled(_world, DotsViewBootstrap.ModuleName));
            Assert.IsTrue(DotsModules.TryGetScope(_world, DotsViewBootstrap.ModuleName, out var scope));
            Assert.AreEqual(DotsModuleScope.Root, scope, "the DI registration is the root-scope owner");
            Assert.AreSame(_world, container.Resolve<DotsViewsLifetime>().World);
        }

        [Test]
        public void ProvisionerAndCascade_Resolve_AsSingletons()
        {
            using var container = Build(_world);

            var provisioner = container.Resolve<ChunkViewProvisioner>();
            Assert.IsNotNull(provisioner);
            Assert.AreSame(provisioner, container.Resolve<ChunkViewProvisioner>());
            Assert.IsInstanceOf<EntityViewCascade>(container.Resolve<IViewCascadeSink>());
        }

        [Test]
        public void Messaging_ResolvesForEveryPackageMessage()
        {
            using var container = Build(_world);

            var spawned = container.Resolve<IDotsPublisher<ViewSpawned>>();
            Assert.IsNotNull(spawned);
            Assert.IsNotNull(container.Resolve<IDotsPublisher<ViewDespawned>>());
            Assert.IsNotNull(container.Resolve<IDotsPublisher<ChunkWarmed>>());
            Assert.IsNotNull(container.Resolve<IDotsPublisher<ChunkReleased>>());
            Assert.IsNotNull(container.Resolve<IDotsPublisher<ChunkCascadeReleased>>());
#if !CUVARA_DOTS_MESSAGEPIPE
            Assert.AreSame(NullDotsPublisher<ViewSpawned>.Instance, spawned, "without MessagePipe the publishers are the no-op ones");
#endif
            Assert.DoesNotThrow(() => spawned.Publish(new ViewSpawned(1, "goblin")));
        }

        [Test]
        public void DisposingTheContainer_UninstallsTheViews_AndRecyclesThem()
        {
            var container = Build(_world);
            var registry = container.Resolve<EntityViewRegistry>();
            var viewId = registry.Spawn("goblin", Unity.Mathematics.float3.zero);
            Assert.AreNotEqual(0, viewId);
            Assert.AreEqual(1, registry.Count);

            container.Dispose();

            Assert.IsFalse(DotsViewBootstrap.IsInstalled(_world), "the scope owned the module");
            Assert.IsFalse(DotsModules.IsInstalled(_world, DotsViewBootstrap.ModuleName));
            Assert.AreEqual(0, registry.Count);
            Assert.AreEqual(1, _provider.Released, "every live view went back to the provider");
        }

        [Test]
        public void WorldDisposedBeforeTheContainer_DisposesCleanly()
        {
            var container = Build(_world);
            _world.Dispose();

            Assert.DoesNotThrow(() => container.Dispose());
        }

        [Test]
        public void NoWorld_WarnsAndInstallsNothing()
        {
            // Neither an explicit world nor a default injection world exists in an edit-mode test.
            var previous = World.DefaultGameObjectInjectionWorld;
            World.DefaultGameObjectInjectionWorld = null;
            try
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No DOTS world available"));
                using var container = Build(null);

                Assert.IsNull(container.Resolve<DotsViewsLifetime>().World);
                Assert.IsFalse(DotsViewBootstrap.IsInstalled(_world));
            }
            finally
            {
                World.DefaultGameObjectInjectionWorld = previous;
            }
        }
    }
}
