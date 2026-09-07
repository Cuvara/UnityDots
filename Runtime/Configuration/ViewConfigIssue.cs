namespace Cuvara.DOTS.Configuration
{
    /// <summary>How bad one <see cref="ViewConfigIssue"/> is.</summary>
    public enum ViewConfigIssueSeverity
    {
        /// <summary>The config would work but probably not as intended. Building proceeds.</summary>
        Warning = 0,

        /// <summary>The config cannot produce a correct view. <see cref="ViewConfigCatalog.TryBuild"/> refuses it.</summary>
        Error = 1,
    }

    /// <summary>
    /// One thing wrong with a <see cref="ViewConfig"/>, <see cref="ViewArchetypeLibrary"/> entry,
    /// <see cref="EntityArchetypePreset"/> or entity-type mapping, in a form a log line or a test can
    /// match on.
    /// </summary>
    /// <remarks>
    /// <see cref="Code"/> is the stable, machine-readable part — a test asserts on it, a consumer
    /// switches on it. <see cref="Message"/> is the human part and may be reworded. Codes are the
    /// constants on this type.
    /// </remarks>
    public readonly struct ViewConfigIssue
    {
        public const string EmptyName = "EmptyName";
        public const string DuplicateName = "DuplicateName";
        public const string MissingConfig = "MissingConfig";
        public const string EmptyViewKey = "EmptyViewKey";
        public const string ViewKeyTooLong = "ViewKeyTooLong";
        public const string NegativePoolSize = "NegativePoolSize";
        public const string InvalidScale = "InvalidScale";
        public const string InvalidOffset = "InvalidOffset";
        public const string MissingPrefab = "MissingPrefab";
        public const string UnknownArchetype = "UnknownArchetype";
        public const string EmptyEntityType = "EmptyEntityType";
        public const string DuplicateEntityType = "DuplicateEntityType";
        public const string UnknownViewKey = "UnknownViewKey";
        public const string InvalidHealth = "InvalidHealth";
        public const string InvalidTimeToLive = "InvalidTimeToLive";
        public const string IneffectiveTimeToLive = "IneffectiveTimeToLive";

        public readonly ViewConfigIssueSeverity Severity;

        /// <summary>One of the constants on this type.</summary>
        public readonly string Code;

        /// <summary>What was being validated: an archetype name, an asset name, an entity type.</summary>
        public readonly string Subject;

        /// <summary>What is wrong and what to do about it.</summary>
        public readonly string Message;

        public ViewConfigIssue(ViewConfigIssueSeverity severity, string code, string subject, string message)
        {
            Severity = severity;
            Code = code;
            Subject = subject ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public static ViewConfigIssue Error(string code, string subject, string message) =>
            new ViewConfigIssue(ViewConfigIssueSeverity.Error, code, subject, message);

        public static ViewConfigIssue Warning(string code, string subject, string message) =>
            new ViewConfigIssue(ViewConfigIssueSeverity.Warning, code, subject, message);

        public override string ToString() => $"{Severity} {Code} '{Subject}': {Message}";
    }
}
