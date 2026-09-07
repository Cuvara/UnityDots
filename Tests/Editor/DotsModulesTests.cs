using System;
using System.Collections.Generic;
using Cuvara.DOTS.Modules;
using NUnit.Framework;
using Unity.Entities;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// The per-world module record: idempotent registration, scope ownership, ordered teardown, and
    /// that two worlds never see each other's records.
    /// </summary>
    public sealed class DotsModulesTests
    {
        private World _world;
        private World _other;

        [SetUp]
        public void SetUp()
        {
            _world = new World("DotsModulesTests.A");
            _other = new World("DotsModulesTests.B");
        }

        [TearDown]
        public void TearDown()
        {
            if (_world.IsCreated) _world.Dispose();
            if (_other.IsCreated) _other.Dispose();
        }

        [Test]
        public void Register_Twice_IsOneRecord_WithInstallCountTwo()
        {
            DotsModules.Register(_world, "M", DotsModuleScope.Root, _ => { });
            DotsModules.Register(_world, "M", DotsModuleScope.Root, _ => { });

            Assert.IsTrue(DotsModules.IsInstalled(_world, "M"));
            Assert.AreEqual(1, DotsModules.Installed(_world).Count, "a second Register must not add a second record");
            Assert.AreEqual(2, DotsModules.InstallCount(_world, "M"));
        }

        [Test]
        public void Register_UnderADifferentScope_IsRefusedWithAnActionableMessage()
        {
            DotsModules.Register(_world, "M", DotsModuleScope.Root, _ => { });

            var error = Assert.Throws<InvalidOperationException>(
                () => DotsModules.Register(_world, "M", DotsModuleScope.Session, _ => { }));
            StringAssert.Contains("'M'", error.Message);
            StringAssert.Contains("Root", error.Message);
            StringAssert.Contains("Session", error.Message);
        }

        [Test]
        public void Unregister_Twice_IsSafe_AndReportsNotInstalled()
        {
            DotsModules.Register(_world, "M", DotsModuleScope.Root, _ => { });

            Assert.IsTrue(DotsModules.Unregister(_world, "M"));
            Assert.IsFalse(DotsModules.Unregister(_world, "M"));
            Assert.IsFalse(DotsModules.IsInstalled(_world, "M"));
            Assert.AreEqual(0, DotsModules.InstallCount(_world, "M"));
        }

        [Test]
        public void UninstallScope_RunsOnlyThatScopesUninstallers_InReverseInstallOrder()
        {
            var order = new List<string>();
            DotsModules.Register(_world, "root", DotsModuleScope.Root, _ => order.Add("root"));
            DotsModules.Register(_world, "s1", DotsModuleScope.Session, _ => order.Add("s1"));
            DotsModules.Register(_world, "s2", DotsModuleScope.Session, _ => order.Add("s2"));

            var count = DotsModules.UninstallScope(_world, DotsModuleScope.Session);

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new[] { "s2", "s1" }, order, "dependents come down before what they depend on");
            Assert.IsTrue(DotsModules.IsInstalled(_world, "root"), "the root module survives a session teardown");
            Assert.IsFalse(DotsModules.IsInstalled(_world, "s1"));
            Assert.IsFalse(DotsModules.IsInstalled(_world, "s2"));
        }

        [Test]
        public void UninstallAll_RemovesEveryRecord_EvenWhenAnUninstallerForgetsTo()
        {
            DotsModules.Register(_world, "a", DotsModuleScope.Root, _ => { });
            DotsModules.Register(_world, "b", DotsModuleScope.Session, _ => { });

            Assert.AreEqual(2, DotsModules.UninstallAll(_world));
            Assert.AreEqual(0, DotsModules.Installed(_world).Count);
        }

        [Test]
        public void Uninstaller_ThatUnregistersItself_DoesNotBreakTheSweep()
        {
            DotsModules.Register(_world, "self", DotsModuleScope.Session, w => DotsModules.Unregister(w, "self"));

            Assert.DoesNotThrow(() => DotsModules.UninstallAll(_world));
            Assert.IsFalse(DotsModules.IsInstalled(_world, "self"));
        }

        [Test]
        public void TwoWorlds_KeepSeparateRecords()
        {
            var uninstalledIn = new List<string>();
            DotsModules.Register(_world, "M", DotsModuleScope.Session, w => uninstalledIn.Add(w.Name));
            DotsModules.Register(_other, "M", DotsModuleScope.Session, w => uninstalledIn.Add(w.Name));

            DotsModules.UninstallAll(_world);

            CollectionAssert.AreEqual(new[] { "DotsModulesTests.A" }, uninstalledIn);
            Assert.IsFalse(DotsModules.IsInstalled(_world, "M"));
            Assert.IsTrue(DotsModules.IsInstalled(_other, "M"), "one world's teardown must not reach the other");
        }

        [Test]
        public void DisposedWorld_ReportsNothingInstalled_AndNothingThrows()
        {
            DotsModules.Register(_world, "M", DotsModuleScope.Root, _ => { });
            _world.Dispose();

            Assert.IsFalse(DotsModules.IsInstalled(_world, "M"));
            Assert.AreEqual(0, DotsModules.UninstallAll(_world));
            Assert.IsFalse(DotsModules.Unregister(_world, "M"));
            Assert.IsFalse(DotsModules.TryGetScope(_world, "M", out _));
        }

        [Test]
        public void RequireSingleton_NamesModuleTypeCountAndHint()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => DotsModules.RequireSingleton<DotsModuleRecord>(_world, "Camera", "Call X.Install first."));

            StringAssert.Contains("Camera", error.Message);
            StringAssert.Contains(nameof(DotsModuleRecord), error.Message);
            StringAssert.Contains("found 0", error.Message);
            StringAssert.Contains("Call X.Install first.", error.Message);
        }

        [Test]
        public void RequireSystem_ReturnsTheSystem_OrNamesWhatIsMissing()
        {
            var group = _world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            Assert.AreSame(group, DotsModules.RequireSystem<SimulationSystemGroup>(_world, "M", "hint"));

            var error = Assert.Throws<InvalidOperationException>(
                () => DotsModules.RequireSystem<PresentationSystemGroup>(_world, "M", "Install the view bootstrap."));
            StringAssert.Contains(nameof(PresentationSystemGroup), error.Message);
            StringAssert.Contains("Install the view bootstrap.", error.Message);
        }

        [Test]
        public void RequireFinite_AndRequireAtLeast_RejectNaNInfinityAndOutOfRange()
        {
            Assert.DoesNotThrow(() => DotsModules.RequireFinite(1f, "M", "f"));
            Assert.Throws<ArgumentException>(() => DotsModules.RequireFinite(float.NaN, "M", "f"));
            Assert.Throws<ArgumentException>(() => DotsModules.RequireFinite(float.PositiveInfinity, "M", "f"));

            Assert.DoesNotThrow(() => DotsModules.RequireAtLeast(0f, 0f, "M", "f"));
            var error = Assert.Throws<ArgumentException>(() => DotsModules.RequireAtLeast(-0.1f, 0f, "M", "SmoothTime"));
            StringAssert.Contains("SmoothTime", error.Message);
            StringAssert.Contains(">= 0", error.Message);
        }
    }
}
