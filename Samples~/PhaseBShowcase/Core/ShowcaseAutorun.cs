using System;
using System.Globalization;
using UnityEngine;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Command-line self-test driver shared by the four showcase scenes.
    /// </summary>
    /// <remarks>
    /// A headless run can build a scene and see its panel, but it cannot click, so the scenarios
    /// behind the buttons — the part actually worth proving — go unexercised. With
    /// <c>-showcaseAutorun</c> each bootstrap drives its own buttons in a scripted order, asserts
    /// the outcomes its README promises, and quits with 0 when every assertion held and 1 otherwise.
    /// <para>
    /// The log lines are a CI contract, so they are fixed and greppable:
    /// <code>
    /// [PhaseB] PoolAndChunks step=duplicate-release expected=DuplicateReleaseCount:1 actual=1 PASS
    /// [PhaseB] PoolAndChunks: 12 passed, 0 failed
    /// </code>
    /// Do not reword them without updating whatever asserts on them.
    /// </para>
    /// <para>
    /// Without the flag none of this runs and the scenes behave exactly as they do interactively.
    /// </para>
    /// </remarks>
    public sealed class ShowcaseAutorun
    {
        /// <summary>Prefix on every autorun line. Stable; CI greps it.</summary>
        public const string Prefix = "[PhaseB]";

        private const string RunFlag = "-showcaseAutorun";
        private const string DelayFlag = "-showcaseAutorunDelay";
        private const float DefaultDelaySeconds = 1f;

        private readonly string _scene;

        public ShowcaseAutorun(string scene)
        {
            _scene = scene;
        }

        public int Passed { get; private set; }
        public int Failed { get; private set; }

        /// <summary>True when <c>-showcaseAutorun</c> was passed on the command line.</summary>
        public static bool Requested
        {
            get
            {
                foreach (var argument in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(argument, RunFlag, StringComparison.OrdinalIgnoreCase)) return true;
                }

                return false;
            }
        }

        /// <summary>Seconds between steps, from <c>-showcaseAutorunDelay</c>; defaults to 1.</summary>
        public static float StepDelay
        {
            get
            {
                var arguments = Environment.GetCommandLineArgs();
                for (var i = 0; i < arguments.Length - 1; i++)
                {
                    if (!string.Equals(arguments[i], DelayFlag, StringComparison.OrdinalIgnoreCase)) continue;

                    // Invariant culture on purpose: a build agent with a comma decimal separator
                    // would otherwise silently reject "0.5" and run at the default pace.
                    if (float.TryParse(arguments[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds >= 0f)
                    {
                        return seconds;
                    }

                    Debug.LogWarning($"{Prefix} {DelayFlag} '{arguments[i + 1]}' is not a number; using {DefaultDelaySeconds}.");
                    return DefaultDelaySeconds;
                }

                return DefaultDelaySeconds;
            }
        }

        /// <summary>Asserts equality, comparing rendered text so the log and the verdict agree.</summary>
        public bool Check(string step, string label, object expected, object actual)
        {
            var expectedText = Render(expected);
            var actualText = Render(actual);
            return Record(step, $"{label}:{expectedText}", actualText, expectedText == actualText);
        }

        /// <summary>Asserts a lower bound, for counts a scene cannot pin exactly.</summary>
        public bool CheckAtLeast(string step, string label, int minimum, int actual)
            => Record(step, $"{label}:>={minimum}", actual.ToString(CultureInfo.InvariantCulture), actual >= minimum);

        /// <summary>Asserts a condition that is already a yes/no.</summary>
        public bool CheckTrue(string step, string label, bool actual)
            => Check(step, label, true, actual);

        /// <summary>Records an outright failure, for a step that threw before it could assert.</summary>
        public void Fail(string step, string label, string detail)
            => Record(step, label, detail, false);

        private bool Record(string step, string expected, string actual, bool ok)
        {
            if (ok) Passed++; else Failed++;

            var line = $"{Prefix} {_scene} step={step} expected={expected} actual={actual} {(ok ? "PASS" : "FAIL")}";
            if (ok) Debug.Log(line); else Debug.LogError(line);
            return ok;
        }

        private static string Render(object value)
        {
            if (value is float f) return f.ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }

        /// <summary>Logs the summary line and quits with 0 when everything passed, 1 otherwise.</summary>
        public void Finish()
        {
            Debug.Log($"{Prefix} {_scene}: {Passed} passed, {Failed} failed");

            var exitCode = Failed == 0 ? 0 : 1;
#if UNITY_EDITOR
            // Application.Quit is a no-op in the Editor, so leaving play mode is the equivalent.
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(exitCode);
#endif
        }
    }
}
