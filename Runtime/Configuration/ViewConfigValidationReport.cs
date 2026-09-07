using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Every issue one validation pass found, with the summary questions a caller actually asks.
    /// </summary>
    public sealed class ViewConfigValidationReport
    {
        private readonly List<ViewConfigIssue> _issues = new List<ViewConfigIssue>();

        public IReadOnlyList<ViewConfigIssue> Issues => _issues;

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        /// <summary>No errors. Warnings do not make a report invalid.</summary>
        public bool IsValid => ErrorCount == 0;

        public bool HasErrors => ErrorCount > 0;

        public void Add(ViewConfigIssue issue)
        {
            _issues.Add(issue);
            if (issue.Severity == ViewConfigIssueSeverity.Error) ErrorCount++;
            else WarningCount++;
        }

        /// <summary>Whether any issue carries <paramref name="code"/>.</summary>
        public bool Has(string code)
        {
            foreach (var issue in _issues)
            {
                if (issue.Code == code) return true;
            }

            return false;
        }

        /// <summary>Whether an issue carries <paramref name="code"/> for <paramref name="subject"/>.</summary>
        public bool Has(string code, string subject)
        {
            foreach (var issue in _issues)
            {
                if (issue.Code == code && issue.Subject == subject) return true;
            }

            return false;
        }

        /// <summary>Writes every issue to the Unity console at its own severity.</summary>
        public void Log()
        {
            foreach (var issue in _issues)
            {
                var line = "[Cuvara.DOTS] " + issue;
                if (issue.Severity == ViewConfigIssueSeverity.Error) Debug.LogError(line);
                else Debug.LogWarning(line);
            }
        }

        /// <summary>Throws a <see cref="ViewConfigValidationException"/> listing every issue when there are errors.</summary>
        public void ThrowIfInvalid()
        {
            if (HasErrors) throw new ViewConfigValidationException(this);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(ErrorCount).Append(" error(s), ").Append(WarningCount).Append(" warning(s)");
            foreach (var issue in _issues) builder.Append("\n  ").Append(issue);
            return builder.ToString();
        }
    }

    /// <summary>Raised by <see cref="ViewConfigValidationReport.ThrowIfInvalid"/> and <see cref="ViewConfigCatalog.BuildOrThrow"/>.</summary>
    public sealed class ViewConfigValidationException : Exception
    {
        public ViewConfigValidationReport Report { get; }

        public ViewConfigValidationException(ViewConfigValidationReport report)
            : base("[Cuvara.DOTS] View configuration is invalid: " + report)
        {
            Report = report;
        }
    }
}
