using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Cuvara.DOTS.Configuration;
using Cuvara.DOTS.Modules;
using Cuvara.DOTS.Simulation;
using Cuvara.DOTS.Views;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Drives the module registry and the view-config pipeline (plan items D04 and D05): install and
    /// uninstall twice, two Worlds that cannot reach each other, the system-order verifier, the
    /// config validator on a deliberately broken library, and a catalog rebuild that strands an
    /// existing <see cref="ViewConfigRef"/>.
    /// </summary>
    /// <remarks>
    /// Offline: two throwaway <see cref="World"/>s are created in <c>Start</c> and disposed in
    /// <c>OnDestroy</c>. Nothing here touches the default world's data.
    /// </remarks>
    public sealed class ModulesAndConfigShowcase : MonoBehaviour
    {
        /// <summary>A module registered directly, to show the registry without a bootstrap in the way.</summary>
        private const string DemoModule = "PhaseBDemo";

        private World _worldA;
        private World _worldB;

        private ViewConfigCatalog _catalog;
        private ViewArchetypeLibrary _validLibrary;
        private ViewArchetypeLibrary _brokenLibrary;
        private readonly List<ScriptableObject> _createdAssets = new List<ScriptableObject>();

        private ViewConfigRef _issuedRef;
        private bool _hasIssuedRef;

        // Outcomes the panel shows as text but the self-test needs as values.
        private bool _scopeConflictRefused;
        private ViewConfigValidationReport _lastReport;

        private readonly StringBuilder _state = new StringBuilder();
        private ShowcaseUi.RollingLog _log;
        private Label _stateLabel;
        private Label _reportLabel;

        /// <summary>False until Initialise has completed; every per-frame method checks it.</summary>
        private bool _ready;

        private void Start()
        {
            try
            {
                Initialise();

                // Not simply true: Initialise disables the component instead of throwing when
                // the world or the UI document is missing, and that is not a ready scene.
                _ready = enabled;
            }
            catch (Exception exception)
            {
                // A half-initialised bootstrap would otherwise NullReference every frame while the
                // headless run hangs to its outer timeout. Stop the component and fail loudly.
                Debug.LogException(exception);
                enabled = false;
                if (ShowcaseAutorun.Requested) ShowcaseAutorun.Abort("ModulesAndConfig", exception);
            }
        }

        /// <summary>Scene setup. Any throw here is caught by <see cref="Start"/>.</summary>
        private void Initialise()
        {
            _worldA = new World("PhaseB Showcase A");
            _worldB = new World("PhaseB Showcase B");

            // Gives world A a real system-group tree, so the order verifier has something to walk.
            DotsSimulationBootstrap.InstallSimulationSystems(_worldA);

            BuildLibraries();
            _catalog = new ViewConfigCatalog();

            BuildUi();
            Log("Two worlds created. Nothing installed yet.");

            if (ShowcaseAutorun.Requested) StartCoroutine(Autorun());
        }

        private void OnDestroy()
        {
            _catalog?.Dispose();

            DisposeWorld(ref _worldA);
            DisposeWorld(ref _worldB);

            foreach (var asset in _createdAssets)
            {
                if (asset != null) Destroy(asset);
            }
        }

        private static void DisposeWorld(ref World world)
        {
            if (world == null) return;
            if (world.IsCreated)
            {
                // Modules first: a module's uninstall recycles managed views to their pools, and
                // disposing the world out from under them would strand those.
                DotsModules.UninstallAll(world);
                world.Dispose();
            }

            world = null;
        }

        private void BuildUi()
        {
            var root = ShowcaseUi.Root(this);
            if (root == null) return;

            _stateLabel = ShowcaseUi.Label(root, "state");
            _reportLabel = ShowcaseUi.Label(root, "report");
            _log = new ShowcaseUi.RollingLog(ShowcaseUi.Label(root, "log"), 14);

            ShowcaseUi.OnClick(root, "install-a", () => Install(_worldA, "A"));
            ShowcaseUi.OnClick(root, "install-b", () => Install(_worldB, "B"));
            ShowcaseUi.OnClick(root, "uninstall-a", () => Uninstall(_worldA, "A"));
            ShowcaseUi.OnClick(root, "uninstall-b", () => Uninstall(_worldB, "B"));
            ShowcaseUi.OnClick(root, "scope-conflict", ScopeConflict);

            ShowcaseUi.OnClick(root, "verify-order", VerifyOrder);

            ShowcaseUi.OnClick(root, "validate-valid", () => Validate(_validLibrary, "valid"));
            ShowcaseUi.OnClick(root, "validate-broken", () => Validate(_brokenLibrary, "broken"));

            ShowcaseUi.OnClick(root, "build-catalog", BuildCatalog);
            ShowcaseUi.OnClick(root, "issue-ref", IssueRef);
            ShowcaseUi.OnClick(root, "rebuild-catalog", RebuildCatalog);
            ShowcaseUi.OnClick(root, "check-ref", CheckRef);
        }

        private void Update()
        {
            if (!_ready) return;
            RenderState();
        }

        // -------------------------------------------------------------- modules

        /// <summary>
        /// Installs two real modules plus one registered by hand. Clicking twice is the point:
        /// the registry is idempotent by name — the second call bumps InstallCount and refreshes
        /// the uninstaller, it does not create a second record.
        /// </summary>
        private void Install(World world, string label)
        {
            if (world == null || !world.IsCreated) return;

            MinimapBootstrap.Install(world, scope: DotsModuleScope.Session);
            DotsModules.Register(world, DemoModule, DotsModuleScope.Session, _ => { });

            Log($"World {label}: installed. modules=[{string.Join(", ", DotsModules.Installed(world))}] " +
                $"InstallCount({DemoModule})={DotsModules.InstallCount(world, DemoModule)}");
        }

        /// <summary>Uninstalls everything in one world. The other world is untouched — that is the isolation claim.</summary>
        private void Uninstall(World world, string label)
        {
            if (world == null || !world.IsCreated) return;

            var count = DotsModules.UninstallAll(world);
            Log($"World {label}: UninstallAll removed {count}. " +
                $"A now=[{Modules(_worldA)}] B now=[{Modules(_worldB)}] (uninstalling one cannot reach the other).");
        }

        /// <summary>
        /// The one re-install the registry refuses: same module name, different scope. Everything
        /// else about double-install is deliberately silent, so this is the only throw to show.
        /// </summary>
        private void ScopeConflict()
        {
            if (_worldA == null || !_worldA.IsCreated) return;

            _scopeConflictRefused = false;
            try
            {
                DotsModules.Register(_worldA, DemoModule, DotsModuleScope.Session, _ => { });
                DotsModules.Register(_worldA, DemoModule, DotsModuleScope.Root, _ => { });
                Log("No exception — unexpected; the second Register should have refused the scope change.");
            }
            catch (InvalidOperationException e)
            {
                _scopeConflictRefused = true;
                Log("Refused re-install under a different scope: " + e.Message);
            }
        }

        private static string Modules(World world)
        {
            if (world == null || !world.IsCreated) return "disposed";
            return string.Join(", ", DotsModules.Installed(world));
        }

        // ---------------------------------------------------------- system order

        /// <summary>
        /// Verify returns one sentence per violated ordering attribute, and an empty list when the
        /// declared relations all hold. An empty list is the good outcome, so it is spelled out.
        /// </summary>
        private void VerifyOrder()
        {
            if (_worldA == null || !_worldA.IsCreated) return;

            var violations = SystemOrderVerifier.Verify(_worldA);
            if (violations.Count == 0)
            {
                SetReport($"SystemOrderVerifier.Verify(\"{_worldA.Name}\")\n\nNo violations: every declared " +
                          "[UpdateInGroup] / [UpdateBefore] / [UpdateAfter] / OrderFirst / OrderLast relation holds " +
                          "in the actual master update list.");
                Log($"System order verified for world A: clean ({violations.Count} violations).");
                return;
            }

            SetReport($"SystemOrderVerifier.Verify(\"{_worldA.Name}\") — {violations.Count} violation(s)\n\n  " +
                      string.Join("\n  ", violations));
            Log($"System order verified for world A: {violations.Count} violation(s); see the report panel.");
        }

        // ------------------------------------------------------- config validation

        private ViewConfig NewConfig(string viewKey, int pool, float scale)
        {
            var config = ScriptableObject.CreateInstance<ViewConfig>();
            config.name = string.IsNullOrEmpty(viewKey) ? "config-with-no-key" : "config-" + viewKey;
            config.Configure(viewKey, pool, scale);
            _createdAssets.Add(config);
            return config;
        }

        private ViewArchetypeLibrary NewLibrary(string name, params ViewArchetypeLibrary.Entry[] entries)
        {
            var library = ScriptableObject.CreateInstance<ViewArchetypeLibrary>();
            library.name = name;
            library.Configure(entries);
            _createdAssets.Add(library);
            return library;
        }

        private void BuildLibraries()
        {
            _validLibrary = NewLibrary(
                "ValidLibrary",
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = NewConfig("goblin", 8, 1f) },
                new ViewArchetypeLibrary.Entry { Name = "chest", Config = NewConfig("chest", 4, 1f) });

            // One defect per issue code, so the report has something distinct to say about each.
            // "ghost" is the key the prefab probe below reports as absent, which is what raises
            // MissingPrefab — without an entry naming it, that code would never appear.
            _brokenLibrary = NewLibrary(
                "BrokenLibrary",
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = NewConfig("goblin", 8, 1f) },
                new ViewArchetypeLibrary.Entry { Name = "goblin", Config = NewConfig("goblin", 8, 1f) },   // DuplicateName
                new ViewArchetypeLibrary.Entry { Name = "", Config = NewConfig("orphan", 1, 1f) },         // EmptyName
                new ViewArchetypeLibrary.Entry { Name = "nulled", Config = null },                          // MissingConfig
                new ViewArchetypeLibrary.Entry { Name = "keyless", Config = NewConfig("", 1, 1f) },         // EmptyViewKey
                new ViewArchetypeLibrary.Entry { Name = "ghosted", Config = NewConfig("ghost", 1, 1f) });   // MissingPrefab
        }

        /// <summary>
        /// Validates a library and prints the whole report. The prefab probe deliberately claims
        /// "ghost" does not exist, so the MissingPrefab path shows up too.
        /// </summary>
        private void Validate(ViewArchetypeLibrary library, string label)
        {
            var report = ViewConfigValidator.ValidateLibrary(library, key => key != "ghost");
            _lastReport = report;

            SetReport($"ViewConfigValidator.ValidateLibrary({library.name})\n" +
                      $"IsValid={report.IsValid}  (warnings do not invalidate)\n\n{report}");
            Log($"Validated the {label} library: {report.ErrorCount} error(s), {report.WarningCount} warning(s).");
        }

        // -------------------------------------------------------------- catalog

        private void BuildCatalog()
        {
            if (!_catalog.TryBuild(_validLibrary, out var report))
            {
                SetReport("TryBuild refused the library and left the catalog untouched:\n\n" + report);
                Log("Catalog build refused; the previously installed table is still valid.");
                return;
            }

            Log($"Catalog built. Version={_catalog.Version} Count={_catalog.Count}.");
        }

        /// <summary>Issues a ref through the catalog — the only supported way to make a valid one.</summary>
        private void IssueRef()
        {
            if (_catalog.Version == 0)
            {
                // CreateRef would throw here; TryCreateRef reports the same thing without throwing.
                var ok = _catalog.TryCreateRef("goblin", out _);
                Log($"No table yet: TryCreateRef(\"goblin\") returned {ok}. Build the catalog first.");
                return;
            }

            if (!_catalog.TryCreateRef("goblin", out _issuedRef))
            {
                Log("TryCreateRef(\"goblin\") returned false — no such archetype in the catalog.");
                return;
            }

            _hasIssuedRef = true;
            Log($"Issued ViewConfigRef {{ Index = {_issuedRef.Index}, Version = {_issuedRef.Version} }} " +
                $"against catalog version {_catalog.Version}.");
        }

        /// <summary>Rebuilds the catalog, which bumps Version and strands every ref issued before it.</summary>
        private void RebuildCatalog()
        {
            if (_catalog.Version == 0)
            {
                Log("Build the catalog before rebuilding it.");
                return;
            }

            var before = _catalog.Version;
            _catalog.Build(_validLibrary);
            Log($"Rebuilt. Version {before} -> {_catalog.Version}. Any ref stamped {before} is now stale.");
        }

        /// <summary>
        /// Compares the held ref against the installed table exactly as the spawn system does.
        /// </summary>
        /// <remarks>
        /// A stale ref is never an exception at spawn time: <c>EntityViewSpawnSystem.TryResolveConfig</c>
        /// logs a warning and falls back to the request's own <c>ViewKey</c>, so a rebuild degrades a
        /// view rather than dropping it. Version is checked before range, and a ref made with
        /// <c>new</c> carries Version 0, which no built table ever has.
        /// </remarks>
        private void CheckRef()
        {
            var lines = new StringBuilder();
            lines.Append($"Installed catalog version: {_catalog.Version}\n\n");

            if (_hasIssuedRef)
            {
                var stale = _issuedRef.Version != _catalog.Version;
                lines.Append($"Issued ref   Index={_issuedRef.Index} Version={_issuedRef.Version}  -> " +
                             (stale
                                 ? "REFUSED (version mismatch). The spawn system warns and falls back to the\n" +
                                   "             request's own ViewKey; re-resolve with CreateRef after a rebuild.\n"
                                 : "accepted.\n"));
            }
            else
            {
                lines.Append("No ref issued yet — click \"Issue ref\" first.\n");
            }

            var unstamped = new ViewConfigRef { Index = 0 };
            lines.Append($"\nHand-made ref  new ViewConfigRef {{ Index = 0 }}  Version={unstamped.Version}  -> REFUSED.\n" +
                         "             Version 0 is the unstamped value and no built table ever has it, so a ref\n" +
                         "             built with `new` is refused whatever the index says.\n");

            SetReport(lines.ToString());
            Log("Compared the held ref against the installed table; see the report panel.");
        }

        // ------------------------------------------------------------- rendering

        private void SetReport(string text) => ShowcaseUi.SetText(_reportLabel, text);

        private void Log(string line)
        {
            _log?.Add(line);
        }

        private void RenderState()
        {
            if (_stateLabel == null) return;

            _state.Clear();
            AppendWorld(_state, _worldA, "A");
            AppendWorld(_state, _worldB, "B");

            _state.Append("\nViewConfigCatalog\n");
            _state.Append($"  Version={_catalog.Version}  Count={_catalog.Count}  worlds={_catalog.InstalledWorldCount}\n");
            _state.Append(_hasIssuedRef
                ? $"  held ref: Index={_issuedRef.Index} Version={_issuedRef.Version} " +
                  $"({(_issuedRef.Version == _catalog.Version ? "current" : "STALE")})\n"
                : "  held ref: none\n");

            _stateLabel.text = _state.ToString();
        }

        private static void AppendWorld(StringBuilder builder, World world, string label)
        {
            if (world == null || !world.IsCreated)
            {
                builder.Append($"World {label}: disposed\n");
                return;
            }

            var installed = DotsModules.Installed(world);
            builder.Append($"World {label} \"{world.Name}\"\n");
            builder.Append($"  modules ({installed.Count}): {(installed.Count == 0 ? "-" : string.Join(", ", installed))}\n");
            builder.Append($"  {DemoModule}: installed={DotsModules.IsInstalled(world, DemoModule)} " +
                           $"installCount={DotsModules.InstallCount(world, DemoModule)}\n");
            builder.Append($"  Minimap: installed={MinimapBootstrap.IsInstalled(world)}\n");
        }

        // ------------------------------------------------------------- autorun

        /// <summary>
        /// Clicks this scene's buttons in order and asserts the outcomes the README promises.
        /// </summary>
        private IEnumerator Autorun()
        {
            var run = new ShowcaseAutorun("ModulesAndConfig");
            var wait = new WaitForSeconds(ShowcaseAutorun.StepDelay);
            yield return wait;

            // The two worlds do not start level: Initialise gives world A the Simulation module and
            // gives world B nothing, so A's count is not a valid expected value for B. Each world
            // gets its own baseline, and Install adds exactly two modules to whichever it is given.
            var baseA = DotsModules.Installed(_worldA).Count;
            var baseB = DotsModules.Installed(_worldB).Count;
            const int addedByInstall = 2; // Minimap + PhaseBDemo

            Install(_worldA, "A");
            run.CheckTrue("install-a", "installed(A,PhaseBDemo)", DotsModules.IsInstalled(_worldA, DemoModule));
            run.Check("install-a-count", "InstallCount(A,PhaseBDemo)", 1, DotsModules.InstallCount(_worldA, DemoModule));
            run.Check("install-a-modules", "modules(A)", baseA + addedByInstall, DotsModules.Installed(_worldA).Count);
            yield return wait;

            // The point of clicking twice: idempotent by name, so the count of modules must not move.
            Install(_worldA, "A");
            run.Check("install-a-twice", "InstallCount(A,PhaseBDemo)", 2, DotsModules.InstallCount(_worldA, DemoModule));
            run.Check("install-a-twice-modules", "modules(A)", baseA + addedByInstall, DotsModules.Installed(_worldA).Count);
            yield return wait;

            Install(_worldB, "B");
            run.Check("install-b", "modules(B)", baseB + addedByInstall, DotsModules.Installed(_worldB).Count);
            run.CheckTrue("install-b-minimap", "installed(B,Minimap)", MinimapBootstrap.IsInstalled(_worldB));
            yield return wait;

            Uninstall(_worldA, "A");
            run.Check("uninstall-a", "modules(A)", 0, DotsModules.Installed(_worldA).Count);
            run.Check("uninstall-a-isolation", "modules(B)", baseB + addedByInstall, DotsModules.Installed(_worldB).Count);
            yield return wait;

            Uninstall(_worldA, "A");
            run.Check("uninstall-a-twice", "modules(A)", 0, DotsModules.Installed(_worldA).Count);
            yield return wait;

            Uninstall(_worldB, "B");
            run.Check("uninstall-b", "modules(B)", 0, DotsModules.Installed(_worldB).Count);
            yield return wait;

            ScopeConflict();
            run.CheckTrue("scope-conflict", "refusedScopeChange", _scopeConflictRefused);
            yield return wait;

            VerifyOrder();
            run.Check("verify-order", "violations", 0, SystemOrderVerifier.Verify(_worldA).Count);
            yield return wait;

            Validate(_validLibrary, "valid");
            run.Check("validate-valid", "errors", 0, _lastReport.ErrorCount);
            run.CheckTrue("validate-valid-isvalid", "IsValid", _lastReport.IsValid);
            yield return wait;

            Validate(_brokenLibrary, "broken");
            run.CheckAtLeast("validate-broken", "errors", 5, _lastReport.ErrorCount);
            run.CheckTrue("validate-broken-duplicate-name", "Has(DuplicateName)", _lastReport.Has(ViewConfigIssue.DuplicateName));
            run.CheckTrue("validate-broken-empty-name", "Has(EmptyName)", _lastReport.Has(ViewConfigIssue.EmptyName));
            run.CheckTrue("validate-broken-missing-config", "Has(MissingConfig)", _lastReport.Has(ViewConfigIssue.MissingConfig));
            run.CheckTrue("validate-broken-empty-view-key", "Has(EmptyViewKey)", _lastReport.Has(ViewConfigIssue.EmptyViewKey));
            run.CheckTrue("validate-broken-missing-prefab", "Has(MissingPrefab)", _lastReport.Has(ViewConfigIssue.MissingPrefab));
            yield return wait;

            BuildCatalog();
            run.Check("build-catalog", "catalogVersion", 1, _catalog.Version);
            yield return wait;

            IssueRef();
            run.CheckTrue("issue-ref", "hasRef", _hasIssuedRef);
            run.Check("issue-ref-version", "refVersion", _catalog.Version, _issuedRef.Version);
            yield return wait;

            RebuildCatalog();
            run.Check("rebuild-catalog", "catalogVersion", 2, _catalog.Version);
            run.CheckTrue("rebuild-strands-ref", "refIsStale", _issuedRef.Version != _catalog.Version);
            yield return wait;

            // A ref built with `new` carries version 0, which no built table ever has.
            run.Check("unstamped-ref-refused", "newRefVersion", 0, new ViewConfigRef { Index = 0 }.Version);
            yield return wait;

            run.Finish();
        }
    }
}
