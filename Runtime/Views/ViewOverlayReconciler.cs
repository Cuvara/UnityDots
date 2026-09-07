using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Cuvara.DOTS.Views
{
    /// <summary>
    /// What a consumer supplies to <see cref="ViewOverlayReconciler{TElement}"/>: how to make, place,
    /// hide and recycle one UI element. The element type is the consumer's — a UI Toolkit
    /// <c>VisualElement</c>, a UGUI <c>RectTransform</c>, a struct for IMGUI — and the package never
    /// names it.
    /// </summary>
    public interface IViewOverlayPresenter<TElement>
    {
        /// <summary>An element for an entity seen for the first time (or first time since it was released).</summary>
        TElement Acquire(in ViewOverlayData data);

        /// <summary>Position and refresh a visible element. Called once per frame per visible entry.</summary>
        void Place(TElement element, in ViewOverlayData data, in ViewOverlayPlacement placement);

        /// <summary>
        /// The entry is still in the buffer but not drawable this frame (behind the camera, too far).
        /// The element is kept, so a name plate that swings behind the player and back does not churn.
        /// </summary>
        void Hide(TElement element, in ViewOverlayData data, in ViewOverlayPlacement placement);

        /// <summary>The entity left the buffer (despawned, or its anchor was removed). Return the element to a pool or destroy it.</summary>
        void Release(TElement element);
    }

    /// <summary>
    /// Keeps one UI element per anchored entity in step with <see cref="ViewOverlayBuffer"/>:
    /// acquires for new entries, places or hides existing ones, releases what left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed by <see cref="ViewOverlayData.Entity"/>, not by <see cref="ViewOverlayData.ViewId"/>.</b>
    /// A view handle changes when the same entity's GameObject is recycled and re-acquired (a chunk
    /// release, an externally destroyed instance); the entity does not, and a health bar should not
    /// flicker because its owner's mesh was re-pooled. The entity's version makes a reused index a
    /// different key, so a despawn and a respawn into the same slot are two elements.
    /// </para>
    /// <para>
    /// <b>Cadence.</b> Call <see cref="Sync"/> once per rendered frame, after
    /// <c>PresentationSystemGroup</c> (<c>LateUpdate</c>), so it reads the buffer this frame's
    /// <c>ViewOverlaySystem</c> wrote. It checks <see cref="ViewOverlayBuffer.Version"/> and does
    /// nothing if the buffer was not rebuilt since the last call — so calling it more often is safe
    /// and calling it from <c>OnGUI</c> (several times a frame) costs one comparison.
    /// </para>
    /// <para>
    /// <b>Recycling.</b> Elements are released exactly when their entity stops appearing in the
    /// buffer — the frame the entity despawned, lost its anchor, or the module was uninstalled and
    /// the buffer read empty. <see cref="Clear"/> releases everything, for the consumer's own teardown.
    /// The reconciler never destroys anything itself; <see cref="IViewOverlayPresenter{TElement}.Release"/>
    /// decides between pooling and destruction.
    /// </para>
    /// <para>
    /// Allocation: one dictionary and one scratch list, reused. Per-frame work is O(entries).
    /// </para>
    /// </remarks>
    public sealed class ViewOverlayReconciler<TElement>
    {
        private readonly IViewOverlayPresenter<TElement> _presenter;
        private readonly Dictionary<Entity, TElement> _elements = new Dictionary<Entity, TElement>();
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();
        private readonly List<Entity> _gone = new List<Entity>();
        private uint _lastVersion;
        private bool _synced;

        public ViewOverlayReconciler(IViewOverlayPresenter<TElement> presenter)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        /// <summary>Elements currently held, visible or hidden.</summary>
        public int ElementCount => _elements.Count;

        /// <summary>Entries placed (drawn) on the last sync.</summary>
        public int VisibleCount { get; private set; }

        /// <summary>Entries hidden (behind camera / too far) on the last sync.</summary>
        public int HiddenCount { get; private set; }

        /// <summary>
        /// Reconciles against <paramref name="buffer"/>. A null or released buffer releases every
        /// element — the module was uninstalled, and stale plates must go with it.
        /// </summary>
        /// <param name="camera">Camera to project with; null hides everything as <see cref="ViewOverlayVisibility.NoCamera"/>.</param>
        /// <param name="maxDistance">World-unit distance filter; ≤ 0 disables it.</param>
        /// <returns>True if the buffer had a new version and work was done.</returns>
        public bool Sync(ViewOverlayBuffer buffer, Camera camera, float maxDistance = 0f)
        {
            if (buffer == null || !buffer.Entries.IsCreated)
            {
                if (_elements.Count > 0) Clear();
                _synced = false;
                return false;
            }

            if (_synced && buffer.Version == _lastVersion) return false;
            _lastVersion = buffer.Version;
            _synced = true;

            _seen.Clear();
            VisibleCount = 0;
            HiddenCount = 0;

            var entries = buffer.Entries;
            for (var i = 0; i < entries.Length; i++)
            {
                var data = entries[i];
                _seen.Add(data.Entity);

                if (!_elements.TryGetValue(data.Entity, out var element))
                {
                    element = _presenter.Acquire(in data);
                    _elements[data.Entity] = element;
                }

                var placement = ViewOverlayProjection.Project(camera, data.WorldPosition, maxDistance);
                if (placement.IsVisible)
                {
                    _presenter.Place(element, in data, in placement);
                    VisibleCount++;
                }
                else
                {
                    _presenter.Hide(element, in data, in placement);
                    HiddenCount++;
                }
            }

            _gone.Clear();
            foreach (var pair in _elements)
            {
                if (!_seen.Contains(pair.Key)) _gone.Add(pair.Key);
            }

            for (var i = 0; i < _gone.Count; i++)
            {
                var entity = _gone[i];
                _presenter.Release(_elements[entity]);
                _elements.Remove(entity);
            }

            return true;
        }

        /// <summary>Releases every element. For the consumer's teardown.</summary>
        public void Clear()
        {
            foreach (var pair in _elements) _presenter.Release(pair.Value);
            _elements.Clear();
            _seen.Clear();
            VisibleCount = 0;
            HiddenCount = 0;
        }
    }
}
