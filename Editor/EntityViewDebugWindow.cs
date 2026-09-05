using System.Linq;
using Cuvara.DOTS.Provisioning;
using Cuvara.DOTS.Views;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Cuvara.DOTS.Editor
{
    /// <summary>
    /// Editor window showing live hybrid view state: pool diagnostics, warm keys, live view
    /// counts, orphaned handles, and chunk provisioner state.
    /// </summary>
    /// <remarks>
    /// Open via <b>Window > Cuvara > DOTS View Debug</b>. Only useful in play mode — the
    /// registry and provisioner are session-scoped and do not exist in edit mode.
    /// </remarks>
    public sealed class EntityViewDebugWindow : EditorWindow
    {
        private Vector2 _scrollViews;
        private Vector2 _scrollChunks;
        private bool _autoRefresh = true;
        private bool _showViews = true;
        private bool _showChunks = true;

        [MenuItem("Window/Cuvara/DOTS View Debug")]
        private static void Open() => GetWindow<EntityViewDebugWindow>("DOTS View Debug");

        private void OnInspectorUpdate()
        {
            if (_autoRefresh && Application.isPlaying) Repaint();
        }

        private void OnGUI()
        {
            _autoRefresh = EditorGUILayout.Toggle("Auto-refresh", _autoRefresh);
            if (!_autoRefresh && GUILayout.Button("Refresh")) Repaint();

            EditorGUILayout.Space();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter play mode to see live view state.", MessageType.Info);
                return;
            }

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                EditorGUILayout.HelpBox("No default world.", MessageType.Warning);
                return;
            }

            // Find the registry singleton
            EntityViewRegistry registry = null;
            using (var query = world.EntityManager.CreateEntityQuery(typeof(EntityViewRegistryReference)))
            {
                if (!query.IsEmpty)
                {
                    var reference = query.GetSingleton<EntityViewRegistryReference>();
                    registry = reference.Registry;
                }
            }

            if (registry == null)
            {
                EditorGUILayout.HelpBox("No EntityViewRegistry found in the default world.", MessageType.Warning);
                return;
            }

            // Summary
            EditorGUILayout.LabelField("Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Total Views", registry.TotalViews.ToString());
            EditorGUILayout.LabelField("Active Keys", registry.TotalKeys.ToString());

            EditorGUILayout.Space();

            // Live views by key
            _showViews = EditorGUILayout.Foldout(_showViews, $"Live Views by Key ({registry.LiveCountsByKey.Count})", true);
            if (_showViews)
            {
                _scrollViews = EditorGUILayout.BeginScrollView(_scrollViews, GUILayout.MaxHeight(200));
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Key", EditorStyles.boldLabel, GUILayout.MinWidth(150));
                EditorGUILayout.LabelField("Warm", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Live", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Deferred", EditorStyles.boldLabel, GUILayout.Width(60));
                EditorGUILayout.EndHorizontal();

                foreach (var kvp in registry.LiveCountsByKey.OrderBy(k => k.Key))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(kvp.Key, GUILayout.MinWidth(150));
                    EditorGUILayout.LabelField(registry.IsWarm(kvp.Key) ? "Yes" : "No", GUILayout.Width(50));
                    EditorGUILayout.LabelField(kvp.Value.ToString(), GUILayout.Width(50));

                    registry.DeferralsByKey.TryGetValue(kvp.Key, out var deferrals);
                    var style = deferrals > 0 ? new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } } : EditorStyles.label;
                    EditorGUILayout.LabelField(deferrals.ToString(), style, GUILayout.Width(60));
                    EditorGUILayout.EndHorizontal();
                }

                // Show deferred-only keys (not yet warm, no live views)
                foreach (var kvp in registry.DeferralsByKey.OrderBy(k => k.Key))
                {
                    if (registry.LiveCountsByKey.ContainsKey(kvp.Key)) continue;
                    EditorGUILayout.BeginHorizontal();
                    var warnStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.red } };
                    EditorGUILayout.LabelField(kvp.Key, warnStyle, GUILayout.MinWidth(150));
                    EditorGUILayout.LabelField("No", GUILayout.Width(50));
                    EditorGUILayout.LabelField("0", GUILayout.Width(50));
                    EditorGUILayout.LabelField(kvp.Value.ToString(), warnStyle, GUILayout.Width(60));
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();

            // Chunk provisioner state (if available — it's not always installed)
            DrawChunkProvisioner(world);
        }

        private void DrawChunkProvisioner(World world)
        {
            // The provisioner is not an ECS singleton — it's typically held by the DI container.
            // We can't easily reach it without reflection or a static accessor. Show a hint instead.
            _showChunks = EditorGUILayout.Foldout(_showChunks, "Chunk Provisioner", true);
            if (!_showChunks) return;

            EditorGUILayout.HelpBox(
                "Chunk provisioner state is available via ChunkViewProvisioner.ChunkStates " +
                "in code. Expose it through a MonoBehaviour or static accessor to see it here.",
                MessageType.Info);
        }
    }
}
