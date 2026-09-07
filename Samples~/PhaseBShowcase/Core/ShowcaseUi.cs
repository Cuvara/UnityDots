using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// The handful of UI Toolkit helpers every showcase scene shares: reach the document root,
    /// find a named element, wire a button, keep a rolling log.
    /// </summary>
    /// <remarks>
    /// UI Toolkit only — the package rule for sample and test scenes. No IMGUI, no uGUI.
    /// Every lookup logs loudly when it misses, because a UXML rename otherwise produces a panel
    /// that renders perfectly and simply does nothing when clicked.
    /// </remarks>
    public static class ShowcaseUi
    {
        /// <summary>Root of the scene's <see cref="UIDocument"/>, or null with an error logged.</summary>
        public static VisualElement Root(Component owner)
        {
            if (owner == null) return null;

            var document = owner.GetComponent<UIDocument>();
            if (document == null)
            {
                Debug.LogError($"[PhaseBShowcase] '{owner.name}' has no UIDocument component.");
                return null;
            }

            var root = document.rootVisualElement;
            if (root == null)
            {
                Debug.LogError($"[PhaseBShowcase] '{owner.name}' has a UIDocument with no source asset assigned.");
                return null;
            }

            return root;
        }

        /// <summary>Named <see cref="Label"/>, or null with an error logged.</summary>
        public static Label Label(VisualElement root, string name)
        {
            var label = root?.Q<Label>(name);
            if (label == null) Debug.LogError($"[PhaseBShowcase] UXML has no Label named '{name}'.");
            return label;
        }

        /// <summary>Named <see cref="VisualElement"/>, or null with an error logged.</summary>
        public static VisualElement Element(VisualElement root, string name)
        {
            var element = root?.Q<VisualElement>(name);
            if (element == null) Debug.LogError($"[PhaseBShowcase] UXML has no element named '{name}'.");
            return element;
        }

        /// <summary>
        /// Wires a named button. A missing button is an error rather than a silent no-op: the whole
        /// point of these scenes is that clicking things demonstrates something.
        /// </summary>
        public static void OnClick(VisualElement root, string name, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var button = root?.Q<Button>(name);
            if (button == null)
            {
                Debug.LogError($"[PhaseBShowcase] UXML has no Button named '{name}'.");
                return;
            }

            button.clicked += action;
        }

        /// <summary>Sets a label's text when the label exists; harmless when it does not.</summary>
        public static void SetText(Label label, string text)
        {
            if (label != null) label.text = text;
        }

        /// <summary>
        /// A capped, newest-last text log that owns the <see cref="Label"/> it renders into.
        /// </summary>
        /// <remarks>
        /// Capped because these scenes are meant to be left running: an uncapped log grows without
        /// bound and the interesting line scrolls out of a panel nobody can scroll. The label is
        /// held rather than passed at each call so no site can forget to re-render.
        /// </remarks>
        public sealed class RollingLog
        {
            private readonly Queue<string> _lines = new Queue<string>();
            private readonly StringBuilder _builder = new StringBuilder();
            private readonly Label _target;
            private readonly int _capacity;

            public RollingLog(Label target, int capacity = 18)
            {
                _target = target;
                _capacity = Math.Max(1, capacity);
            }

            public int Count => _lines.Count;

            /// <summary>Appends a line and re-renders.</summary>
            public void Add(string line)
            {
                _lines.Enqueue(line ?? string.Empty);
                while (_lines.Count > _capacity) _lines.Dequeue();
                Render();
            }

            public void Clear()
            {
                _lines.Clear();
                Render();
            }

            public override string ToString()
            {
                _builder.Clear();
                foreach (var line in _lines)
                {
                    if (_builder.Length > 0) _builder.Append('\n');
                    _builder.Append(line);
                }

                return _builder.ToString();
            }

            private void Render()
            {
                // A null label means the UXML lacked the element; the error is already logged.
                if (_target != null) _target.text = ToString();
            }
        }
    }
}
