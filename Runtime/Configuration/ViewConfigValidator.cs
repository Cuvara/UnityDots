using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Runtime validation of view configuration: libraries, configs, archetype presets and
    /// entity-type mappings. Pure functions over the authoring types — no Editor, no world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Runtime, because the configs arrive at runtime.</b> This package's consumers build
    /// catalogs from server vocabulary and, today, from code (<c>ViewConfig.Configure</c>) — there
    /// is no inspector in that path to clamp a value or flag a duplicate. An <c>OnValidate</c> is
    /// the wrong layer; the check has to run where the catalog is built, against whatever was
    /// handed over, and it has to run before the first spawn so the failure is a stack trace at
    /// session start rather than an invisible entity ten minutes in.
    /// </para>
    /// <para>
    /// Every check reports rather than throws, and every report names the row, so one pass lists
    /// everything wrong with an asset instead of one thing per attempt.
    /// <see cref="ViewConfigCatalog.TryBuild"/> is the gate that turns a report with errors into a
    /// refusal to build.
    /// </para>
    /// </remarks>
    public static class ViewConfigValidator
    {
        /// <summary>
        /// Longest view key that fits <see cref="Unity.Collections.FixedString64Bytes"/> without
        /// truncation, in UTF-8 bytes. Matches what <see cref="ViewConfig.ToRecord"/> truncates at.
        /// </summary>
        public const int MaxViewKeyBytes = 61;

        /// <summary>
        /// Validates a library: names, configs, and — when <paramref name="prefabExists"/> is given —
        /// that every view key resolves to an asset.
        /// </summary>
        /// <param name="prefabExists">
        /// Answers "does the asset provider know this key". Null skips the check, since the core has
        /// no way to ask an <c>IViewAssetProvider</c>; a consumer passes its own lookup, typically
        /// the pooled provider's registered-prefab set or an Addressables key check.
        /// </param>
        public static ViewConfigValidationReport ValidateLibrary(ViewArchetypeLibrary library, Func<string, bool> prefabExists = null)
        {
            var report = new ViewConfigValidationReport();
            ValidateLibrary(library, prefabExists, report);
            return report;
        }

        /// <summary>Appends library issues to an existing report.</summary>
        public static void ValidateLibrary(ViewArchetypeLibrary library, Func<string, bool> prefabExists, ViewConfigValidationReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (library == null)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingConfig, "<library>", "The library is null. Assign a ViewArchetypeLibrary."));
                return;
            }

            var libraryName = library.name;
            var seen = new HashSet<string>();
            var entries = library.Entries;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var subject = string.IsNullOrEmpty(entry.Name) ? $"{libraryName}[{i}]" : entry.Name;

                if (string.IsNullOrEmpty(entry.Name))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.EmptyName, subject,
                        $"Entry {i} of '{libraryName}' has no name. Every archetype needs the name the server refers to it by."));
                }
                else if (!seen.Add(entry.Name))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.DuplicateName, subject,
                        $"'{libraryName}' defines archetype '{entry.Name}' more than once; which config wins would depend on list order. Remove one."));
                }

                if (entry.Config == null)
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingConfig, subject,
                        $"Archetype '{subject}' in '{libraryName}' has no ViewConfig assigned."));
                    continue;
                }

                ValidateConfig(entry.Config, subject, prefabExists, report);
            }
        }

        /// <summary>Validates one config on its own. <paramref name="subject"/> names it in the issues.</summary>
        public static ViewConfigValidationReport ValidateConfig(ViewConfig config, string subject = null, Func<string, bool> prefabExists = null)
        {
            var report = new ViewConfigValidationReport();
            ValidateConfig(config, subject, prefabExists, report);
            return report;
        }

        /// <summary>Appends one config's issues to an existing report.</summary>
        public static void ValidateConfig(ViewConfig config, string subject, Func<string, bool> prefabExists, ViewConfigValidationReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (config == null)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingConfig, subject ?? "<null>", "The ViewConfig is null."));
                return;
            }

            subject = string.IsNullOrEmpty(subject) ? config.name : subject;
            var key = config.ViewKey;

            if (string.IsNullOrEmpty(key))
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.EmptyViewKey, subject,
                    $"ViewConfig '{config.name}' has an empty view key; nothing can be spawned for it. Set the asset/pool key of the prefab."));
            }
            else
            {
                var bytes = Encoding.UTF8.GetByteCount(key);
                if (bytes > MaxViewKeyBytes)
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.ViewKeyTooLong, subject,
                        $"ViewConfig '{config.name}' has a {bytes}-byte view key; the limit is {MaxViewKeyBytes} UTF-8 bytes. " +
                        "It would be truncated and never match the pool. Shorten the key."));
                }
                else if (prefabExists != null && !prefabExists(key))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingPrefab, subject,
                        $"ViewConfig '{config.name}' names view key '{key}', which the asset provider does not know. " +
                        "Register the prefab under that key or fix the key."));
                }
            }

            if (config.PoolSize < 0)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.NegativePoolSize, subject,
                    $"ViewConfig '{config.name}' has pool size {config.PoolSize}; it must be >= 0."));
            }

            if (!IsFinite(config.Scale) || config.Scale <= 0f)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidScale, subject,
                    $"ViewConfig '{config.name}' has scale {config.Scale}; it must be finite and > 0. A zero scale is an invisible view that looks like a spawn failure."));
            }

            if (!IsFinite(config.PositionOffset))
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidOffset, subject,
                    $"ViewConfig '{config.name}' has a non-finite position offset {config.PositionOffset}."));
            }

            if (!IsFinite(config.RotationOffsetEuler))
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidOffset, subject,
                    $"ViewConfig '{config.name}' has a non-finite rotation offset {config.RotationOffsetEuler}."));
            }
        }

        /// <summary>
        /// Validates server entity-type → archetype-name mappings against a library: every archetype
        /// named must exist, every entity type must be non-empty and mapped once.
        /// </summary>
        /// <remarks>
        /// This is the check the netcode adapter's resolver cannot do for itself — it learns the
        /// kinds from the wire, one at a time, and logs each unknown archetype when the first entity
        /// of that kind arrives. Run here, at session start, the same mistake is caught before the
        /// connection is opened.
        /// </remarks>
        public static ViewConfigValidationReport ValidateMappings(
            ViewArchetypeLibrary library,
            IEnumerable<KeyValuePair<string, string>> entityTypeToArchetype)
        {
            var report = new ViewConfigValidationReport();
            if (library == null)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingConfig, "<library>", "The library is null. Assign a ViewArchetypeLibrary."));
                return report;
            }

            if (entityTypeToArchetype == null) return report;

            var names = new HashSet<string>();
            foreach (var entry in library.Entries)
            {
                if (!string.IsNullOrEmpty(entry.Name)) names.Add(entry.Name);
            }

            var seenTypes = new HashSet<string>();
            foreach (var pair in entityTypeToArchetype)
            {
                var subject = string.IsNullOrEmpty(pair.Key) ? "<empty>" : pair.Key;

                if (string.IsNullOrEmpty(pair.Key))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.EmptyEntityType, subject,
                        $"A mapping to archetype '{pair.Value}' has an empty entity type."));
                }
                else if (!seenTypes.Add(pair.Key))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.DuplicateEntityType, subject,
                        $"Entity type '{pair.Key}' is mapped more than once."));
                }

                if (string.IsNullOrEmpty(pair.Value) || !names.Contains(pair.Value))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.UnknownArchetype, subject,
                        $"Entity type '{subject}' maps to archetype '{pair.Value}', which '{library.name}' does not define. " +
                        "Add the archetype to the library or fix the mapping."));
                }
            }

            return report;
        }

        /// <summary>
        /// Validates an archetype preset: identity, initial values, and — when a library is given —
        /// that its view key is one some config in the library actually uses.
        /// </summary>
        public static ViewConfigValidationReport ValidatePreset(EntityArchetypePreset preset, ViewArchetypeLibrary library = null)
        {
            var report = new ViewConfigValidationReport();
            if (preset == null)
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.MissingConfig, "<preset>", "The preset is null."));
                return report;
            }

            var subject = string.IsNullOrEmpty(preset.entityType) ? preset.name : preset.entityType;

            if (string.IsNullOrEmpty(preset.entityType))
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.EmptyEntityType, subject,
                    $"Preset '{preset.name}' has no entity type; it cannot be matched to a server kind."));
            }

            if (!string.IsNullOrEmpty(preset.viewKey))
            {
                var bytes = Encoding.UTF8.GetByteCount(preset.viewKey);
                if (bytes > MaxViewKeyBytes)
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.ViewKeyTooLong, subject,
                        $"Preset '{preset.name}' has a {bytes}-byte view key; the limit is {MaxViewKeyBytes} UTF-8 bytes."));
                }
                else if (library != null && !LibraryUsesKey(library, preset.viewKey))
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.UnknownViewKey, subject,
                        $"Preset '{preset.name}' requests view key '{preset.viewKey}', which no ViewConfig in '{library.name}' uses. " +
                        "The entity would wait for a key nothing warms."));
                }
            }

            if (preset.hasHealth)
            {
                if (preset.defaultMaxHp <= 0 || preset.defaultHp < 0 || preset.defaultHp > preset.defaultMaxHp)
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidHealth, subject,
                        $"Preset '{preset.name}' has hp {preset.defaultHp}/{preset.defaultMaxHp}; needs 0 <= hp <= max and max > 0. " +
                        "HealthDeathSystem would destroy the entity on its first tick."));
                }
            }

            if (preset.hasTimeToLive)
            {
                if (!IsFinite(preset.timeToLive) || preset.timeToLive < 0f)
                {
                    report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidTimeToLive, subject,
                        $"Preset '{preset.name}' has time to live {preset.timeToLive}; it must be finite and >= 0."));
                }
                else if (preset.timeToLive == 0f)
                {
                    report.Add(ViewConfigIssue.Warning(ViewConfigIssue.IneffectiveTimeToLive, subject,
                        $"Preset '{preset.name}' enables time to live with a value of 0; ArchetypeFactory adds no TimeToLive component for 0. " +
                        "Set a positive value or turn hasTimeToLive off."));
                }
            }

            if (preset.hasOverlayAnchor && !IsFinite(preset.overlayOffset))
            {
                report.Add(ViewConfigIssue.Error(ViewConfigIssue.InvalidOffset, subject,
                    $"Preset '{preset.name}' has a non-finite overlay offset {preset.overlayOffset}."));
            }

            return report;
        }

        private static bool LibraryUsesKey(ViewArchetypeLibrary library, string key)
        {
            foreach (var entry in library.Entries)
            {
                if (entry.Config != null && entry.Config.ViewKey == key) return true;
            }

            return false;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
