using System.Collections.Generic;
using Cuvara.DOTS.Configuration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Every invalid configuration the validator promises to catch, each with its stable code, and
    /// the gate that keeps an invalid library out of a catalog.
    /// </summary>
    public sealed class ViewConfigValidatorTests
    {
        private readonly List<Object> _assets = new List<Object>();

        private ViewConfig Config(string key, int pool = 1, float scale = 1f, Vector3 position = default, Vector3 rotation = default, string assetName = null)
        {
            var config = ScriptableObject.CreateInstance<ViewConfig>();
            config.name = assetName ?? key;
            config.Configure(key, pool, scale, position, rotation);
            _assets.Add(config);
            return config;
        }

        private ViewArchetypeLibrary Library(params ViewArchetypeLibrary.Entry[] entries)
        {
            var library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            library.name = "lib";
            library.Configure(entries);
            _assets.Add(library);
            return library;
        }

        private static ViewArchetypeLibrary.Entry Entry(string name, ViewConfig config) =>
            new ViewArchetypeLibrary.Entry { Name = name, Config = config };

        private EntityArchetypePreset Preset()
        {
            var preset = ScriptableObject.CreateInstance<EntityArchetypePreset>();
            preset.name = "preset";
            preset.entityType = "mob";
            preset.viewKey = "goblin";
            _assets.Add(preset);
            return preset;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in _assets) Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        [Test]
        public void ValidLibrary_HasNoIssues()
        {
            var library = Library(Entry("goblin", Config("goblin", 8, 1.5f)), Entry("torch", Config("torch")));

            var report = ViewConfigValidator.ValidateLibrary(library);

            Assert.IsTrue(report.IsValid, report.ToString());
            Assert.AreEqual(0, report.Issues.Count);
        }

        [Test]
        public void EmptyName_DuplicateName_MissingConfig_AreErrors_NamingTheRow()
        {
            var goblin = Config("goblin");
            var library = Library(
                Entry("", goblin),
                Entry("orphan", null),
                Entry("goblin", goblin),
                Entry("goblin", goblin));

            var report = ViewConfigValidator.ValidateLibrary(library);

            Assert.IsTrue(report.Has(ViewConfigIssue.EmptyName, "lib[0]"), report.ToString());
            Assert.IsTrue(report.Has(ViewConfigIssue.MissingConfig, "orphan"));
            Assert.IsTrue(report.Has(ViewConfigIssue.DuplicateName, "goblin"));
            Assert.AreEqual(3, report.ErrorCount, "one pass reports everything, not the first thing");
        }

        [Test]
        public void EmptyViewKey_IsAnError()
        {
            var report = ViewConfigValidator.ValidateLibrary(Library(Entry("ghost", Config(""))));

            Assert.IsTrue(report.Has(ViewConfigIssue.EmptyViewKey, "ghost"), report.ToString());
            StringAssert.Contains("empty view key", report.Issues[0].Message);
        }

        [Test]
        public void OverlongViewKey_IsAnError_AtTheFixedStringLimit()
        {
            var ok = new string('k', ViewConfigValidator.MaxViewKeyBytes);
            var tooLong = new string('k', ViewConfigValidator.MaxViewKeyBytes + 1);
            var multiByte = new string('é', 31); // 62 bytes in UTF-8, 31 chars

            Assert.IsTrue(ViewConfigValidator.ValidateConfig(Config(ok)).IsValid);
            Assert.IsTrue(ViewConfigValidator.ValidateConfig(Config(tooLong)).Has(ViewConfigIssue.ViewKeyTooLong));
            Assert.IsTrue(ViewConfigValidator.ValidateConfig(Config(multiByte)).Has(ViewConfigIssue.ViewKeyTooLong), "the limit is bytes, not characters");
        }

        [Test]
        public void MissingPrefab_IsAnError_OnlyWhenALookupIsProvided()
        {
            var library = Library(Entry("goblin", Config("goblin")), Entry("wyvern", Config("wyvern")));
            var known = new HashSet<string> { "goblin" };

            var withLookup = ViewConfigValidator.ValidateLibrary(library, known.Contains);
            var withoutLookup = ViewConfigValidator.ValidateLibrary(library);

            Assert.IsTrue(withLookup.Has(ViewConfigIssue.MissingPrefab, "wyvern"), withLookup.ToString());
            Assert.IsFalse(withLookup.Has(ViewConfigIssue.MissingPrefab, "goblin"));
            Assert.IsTrue(withoutLookup.IsValid, "the core cannot ask a provider; no lookup means no prefab check");
        }

        [Test]
        public void NonFiniteScaleAndOffsets_AreErrors()
        {
            // Configure clamps a non-positive scale, so NaN is the way an invalid scale reaches a
            // built config: NaN <= 0 is false and slips through the clamp.
            var nanScale = Config("a", scale: float.NaN);
            var nanOffset = Config("b", position: new Vector3(0f, float.NaN, 0f));
            var infRotation = Config("c", rotation: new Vector3(float.PositiveInfinity, 0f, 0f));

            Assert.IsTrue(ViewConfigValidator.ValidateConfig(nanScale).Has(ViewConfigIssue.InvalidScale));
            Assert.IsTrue(ViewConfigValidator.ValidateConfig(nanOffset).Has(ViewConfigIssue.InvalidOffset));
            Assert.IsTrue(ViewConfigValidator.ValidateConfig(infRotation).Has(ViewConfigIssue.InvalidOffset));
        }

        [Test]
        public void Mappings_UnknownArchetype_EmptyType_DuplicateType_AreErrors()
        {
            var library = Library(Entry("goblin", Config("goblin")));
            var mappings = new[]
            {
                new KeyValuePair<string, string>("mob", "goblin"),
                new KeyValuePair<string, string>("player", "hero"),
                new KeyValuePair<string, string>("", "goblin"),
                new KeyValuePair<string, string>("mob", "goblin"),
            };

            var report = ViewConfigValidator.ValidateMappings(library, mappings);

            Assert.IsTrue(report.Has(ViewConfigIssue.UnknownArchetype, "player"), report.ToString());
            Assert.IsTrue(report.Has(ViewConfigIssue.EmptyEntityType));
            Assert.IsTrue(report.Has(ViewConfigIssue.DuplicateEntityType, "mob"));
            Assert.IsFalse(report.Has(ViewConfigIssue.UnknownArchetype, "mob"));
        }

        [Test]
        public void Preset_ValidAgainstLibrary_HasNoIssues()
        {
            var library = Library(Entry("goblin", Config("goblin")));

            var report = ViewConfigValidator.ValidatePreset(Preset(), library);

            Assert.IsTrue(report.IsValid, report.ToString());
        }

        [Test]
        public void Preset_UnknownViewKey_EmptyType_BadHealth_BadTtl_AreReported()
        {
            var library = Library(Entry("goblin", Config("goblin")));
            var preset = Preset();
            preset.entityType = "";
            preset.viewKey = "wyvern";
            preset.defaultHp = 150;
            preset.defaultMaxHp = 100;
            preset.hasTimeToLive = true;
            preset.timeToLive = -1f;

            var report = ViewConfigValidator.ValidatePreset(preset, library);

            Assert.IsTrue(report.Has(ViewConfigIssue.EmptyEntityType), report.ToString());
            Assert.IsTrue(report.Has(ViewConfigIssue.UnknownViewKey));
            Assert.IsTrue(report.Has(ViewConfigIssue.InvalidHealth));
            Assert.IsTrue(report.Has(ViewConfigIssue.InvalidTimeToLive));
        }

        [Test]
        public void Preset_ZeroTtlWithTtlEnabled_IsAWarning_NotAnError()
        {
            var preset = Preset();
            preset.hasTimeToLive = true;
            preset.timeToLive = 0f;

            var report = ViewConfigValidator.ValidatePreset(preset);

            Assert.IsTrue(report.IsValid);
            Assert.IsTrue(report.Has(ViewConfigIssue.IneffectiveTimeToLive));
            Assert.AreEqual(1, report.WarningCount);
        }

        [Test]
        public void TryBuild_RefusesAnInvalidLibrary_AndLeavesThePreviousTableInstalled()
        {
            var good = Library(Entry("goblin", Config("goblin", 3)));
            var bad = Library(Entry("goblin", Config("goblin")), Entry("goblin", Config("torch")));
            using var catalog = new ViewConfigCatalog();
            Assert.IsTrue(catalog.TryBuild(good, out _));
            var versionBefore = catalog.Version;

            var built = catalog.TryBuild(bad, out var report);

            Assert.IsFalse(built);
            Assert.IsTrue(report.Has(ViewConfigIssue.DuplicateName, "goblin"));
            Assert.AreEqual(versionBefore, catalog.Version, "a refused build is not a build");
            Assert.AreEqual(3, catalog[0].PoolSize, "the previous content is still what the catalog holds");
        }

        [Test]
        public void BuildOrThrow_ListsEveryIssueInTheException()
        {
            var bad = Library(Entry("", Config("goblin")), Entry("ghost", Config("")));
            using var catalog = new ViewConfigCatalog();

            var error = Assert.Throws<ViewConfigValidationException>(() => catalog.BuildOrThrow(bad));

            StringAssert.Contains(ViewConfigIssue.EmptyName, error.Message);
            StringAssert.Contains(ViewConfigIssue.EmptyViewKey, error.Message);
            Assert.AreEqual(2, error.Report.ErrorCount);
            Assert.AreEqual(0, catalog.Version, "nothing was built");
        }

        [Test]
        public void Report_Log_WritesAtTheIssuesSeverity()
        {
            var report = new ViewConfigValidationReport();
            report.Add(ViewConfigIssue.Error(ViewConfigIssue.EmptyViewKey, "a", "message a"));
            report.Add(ViewConfigIssue.Warning(ViewConfigIssue.IneffectiveTimeToLive, "b", "message b"));

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("EmptyViewKey 'a'"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("IneffectiveTimeToLive 'b'"));
            report.Log();
        }
    }
}
