using System.Collections.Generic;
using Cuvara.DOTS.Views;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Cuvara.DOTS.Samples.HybridViews
{
    /// <summary>
    /// Reference consumer for the two HUD data feeds: world-space overlays
    /// (<see cref="ViewOverlayBuffer"/>) and the minimap (<see cref="MinimapBuffer"/>). Add it to
    /// the same GameObject as <see cref="HybridViewsSample"/>; it draws with IMGUI so the sample
    /// needs no UI package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Overlays.</b> Every entity that has a view gets a <see cref="ViewOverlayAnchor"/> a little
    /// above it (the sample does this; a real game authors the anchor where it spawns the entity).
    /// A <see cref="ViewOverlayReconciler{TElement}"/> keeps one label per anchored <i>entity</i> in
    /// step with the buffer: acquire on first sight, place while visible, hide while behind the
    /// camera or too far, release the frame the entity leaves the buffer. The labels are plain
    /// records in a list — the presenter is where a UI Toolkit or UGUI project substitutes its own
    /// element type.
    /// </para>
    /// <para>
    /// <b>Minimap.</b> <see cref="MinimapBootstrap.Install"/> publishes the buffer and the producer;
    /// the sample marks each entity with a <see cref="MinimapMarker"/> whose category is the view
    /// key's index, then draws a box in the corner with one dot per entry. Marking is the host's
    /// decision: in a networked game the netcode adapter marks its mirror entities through an
    /// <c>IMinimapCategoryResolver</c>, so nothing outside the area of interest can appear.
    /// </para>
    /// <para>
    /// Watch the step-3 despawn and the step-5/6 releases: labels and dots vanish on the same frame
    /// as the entities, and both buffers read zero at the end — no stale markers, which is the
    /// property the two systems exist to guarantee.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(HybridViewsSample))]
    public sealed class HudOverlaysSample : MonoBehaviour
    {
        [Tooltip("Anchor height above each entity, in world units.")]
        [SerializeField] private float _labelHeight = 1.2f;

        [Tooltip("Labels farther than this from the camera are hidden (0 = no limit).")]
        [SerializeField] private float _labelMaxDistance = 60f;

        [Tooltip("World units shown across the minimap box.")]
        [SerializeField] private float _minimapWorldExtent = 24f;

        [SerializeField] private float _minimapPixels = 160f;

        private World _world;
        private EntityQuery _unanchored;
        private EntityQuery _unmarked;
        private readonly LabelPresenter _presenter = new LabelPresenter();
        private ViewOverlayReconciler<Label> _reconciler;

        /// <summary>The IMGUI "element": a rect and a string. Stands in for a VisualElement.</summary>
        private sealed class Label
        {
            public string Text;
            public Vector2 Screen;
            public bool Visible;
        }

        private sealed class LabelPresenter : IViewOverlayPresenter<Label>
        {
            public readonly List<Label> Live = new List<Label>();
            private readonly Stack<Label> _pool = new Stack<Label>();
            public int Acquired, Released;

            public Label Acquire(in ViewOverlayData data)
            {
                Acquired++;
                var label = _pool.Count > 0 ? _pool.Pop() : new Label();
                label.Text = $"view {data.ViewId}";
                Live.Add(label);
                return label;
            }

            public void Place(Label label, in ViewOverlayData data, in ViewOverlayPlacement placement)
            {
                label.Visible = true;
                // IMGUI's origin is top-left; WorldToScreenPoint's is bottom-left.
                label.Screen = new Vector2(placement.Screen.x, Screen.height - placement.Screen.y);
                label.Text = $"view {data.ViewId}  {placement.Distance:F0}m";
            }

            public void Hide(Label label, in ViewOverlayData data, in ViewOverlayPlacement placement)
            {
                label.Visible = false;
            }

            public void Release(Label label)
            {
                Released++;
                Live.Remove(label);
                _pool.Push(label);
            }
        }

        private void Start()
        {
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null)
            {
                enabled = false;
                return;
            }

            _reconciler = new ViewOverlayReconciler<Label>(_presenter);

            // Session-scoped module, like the camera: the map belongs to this run of the sample.
            MinimapBootstrap.Install(_world, MinimapPlane.XZ);

            var entityManager = _world.EntityManager;
            _unanchored = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityViewLink>(),
                ComponentType.Exclude<ViewOverlayAnchor>());
            _unmarked = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityViewRequest>(),
                ComponentType.Exclude<MinimapMarker>(),
                ComponentType.Exclude<EntityViewLink>());
        }

        private void OnDestroy()
        {
            _reconciler?.Clear();
            if (_world != null && _world.IsCreated)
            {
                MinimapBootstrap.Uninstall(_world);
                _unanchored.Dispose();
                _unmarked.Dispose();
            }
        }

        private void Update()
        {
            // Structural changes between frames, from a MonoBehaviour — the same place the sample
            // creates its entities. Anchors go on entities once they have a view; markers go on
            // freshly requested entities so they are on the map before their key has warmed.
            var entityManager = _world.EntityManager;

            if (!_unanchored.IsEmpty)
            {
                entityManager.AddComponent<ViewOverlayAnchor>(_unanchored);
                using var anchored = entityManager.CreateEntityQuery(ComponentType.ReadWrite<ViewOverlayAnchor>());
                using var entities = anchored.ToEntityArray(Allocator.Temp);
                for (var i = 0; i < entities.Length; i++)
                {
                    var anchor = entityManager.GetComponentData<ViewOverlayAnchor>(entities[i]);
                    if (anchor.WorldOffset.Equals(float3.zero))
                    {
                        entityManager.SetComponentData(entities[i], new ViewOverlayAnchor { WorldOffset = new float3(0f, _labelHeight, 0f) });
                    }
                }
            }

            if (!_unmarked.IsEmpty)
            {
                using var entities = _unmarked.ToEntityArray(Allocator.Temp);
                for (var i = 0; i < entities.Length; i++)
                {
                    var key = entityManager.GetComponentData<EntityViewRequest>(entities[i]).ViewKey;
                    entityManager.AddComponentData(entities[i], new MinimapMarker { Category = CategoryOf(key) });
                }
            }
        }

        private static int CategoryOf(in FixedString64Bytes key)
        {
            // The sample's three keys → three categories. A real host maps its own vocabulary.
            if (key == new FixedString64Bytes("cube")) return 0;
            if (key == new FixedString64Bytes("sphere")) return 1;
            return 2;
        }

        private void LateUpdate()
        {
            // After PresentationSystemGroup: this frame's buffer. Sync is version-gated, so OnGUI
            // calling it again would be a no-op — it is done here once per frame on purpose.
            _reconciler.Sync(FindOverlayBuffer(), Camera.main, _labelMaxDistance);
        }

        private ViewOverlayBuffer FindOverlayBuffer()
        {
            if (_world == null || !_world.IsCreated) return null;
            using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ViewOverlayBuffer>());
            return query.IsEmpty ? null : _world.EntityManager.GetComponentObject<ViewOverlayBuffer>(query.GetSingletonEntity());
        }

        private static readonly Color[] Palette = { new Color(0.85f, 0.35f, 0.25f), new Color(0.25f, 0.6f, 0.9f), new Color(0.4f, 0.8f, 0.4f) };

        private void OnGUI()
        {
            // Labels.
            var live = _presenter.Live;
            for (var i = 0; i < live.Count; i++)
            {
                var label = live[i];
                if (!label.Visible) continue;
                GUI.Label(new Rect(label.Screen.x - 40f, label.Screen.y - 10f, 120f, 20f), label.Text);
            }

            // Minimap.
            var minimap = MinimapBootstrap.InstalledBuffer(_world);
            var box = new Rect(Screen.width - _minimapPixels - 10f, 10f, _minimapPixels, _minimapPixels);
            GUI.Box(box, $"minimap {(minimap?.Count ?? 0)}  labels {_presenter.Live.Count} " +
                         $"(+{_presenter.Acquired}/-{_presenter.Released})");
            if (minimap == null || !minimap.Entries.IsCreated) return;

            var scale = _minimapPixels / _minimapWorldExtent;
            var centre = box.center;
            var entries = minimap.Entries;
            var previous = GUI.color;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                // Plane XZ: Position is (x, z). Up on the map is +z, so the second axis is flipped for IMGUI.
                var at = new Vector2(centre.x + entry.Position.x * scale, centre.y - entry.Position.y * scale);
                GUI.color = Palette[math.clamp(entry.Category, 0, Palette.Length - 1)];
                GUI.Box(new Rect(at.x - 3f, at.y - 3f, 6f, 6f), GUIContent.none);
            }

            GUI.color = previous;
        }
    }
}
