using System;
using System.Collections.Generic;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// <see cref="IMinimapCategoryResolver"/> keyed on the server's entity kind, with an optional
    /// override for the local player — the same shape as <see cref="TypeArchetypeResolver"/>.
    /// </summary>
    /// <remarks>
    /// Exact, ordinal matches on the wire's type string. A kind with no rule is off the map, silently:
    /// unlike an unmapped archetype, an entity absent from the minimap is not a broken screen, and a
    /// log per unmapped kind would be noise for a host that only wants players on the map.
    /// </remarks>
    public sealed class TypeMinimapCategoryResolver : IMinimapCategoryResolver
    {
        /// <summary>One kind → category rule.</summary>
        public readonly struct Rule
        {
            public readonly string Type;
            public readonly int Category;

            public Rule(string type, int category)
            {
                Type = type ?? throw new ArgumentNullException(nameof(type));
                Category = category;
            }
        }

        private readonly Dictionary<string, int> _byType = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly int? _localCategory;

        /// <param name="localCategory">
        /// Category for the local player's entity regardless of kind, or null to resolve it by kind
        /// like everything else.
        /// </param>
        /// <param name="rules">Kind → category. A later rule for the same kind replaces an earlier one.</param>
        public TypeMinimapCategoryResolver(int? localCategory, params Rule[] rules)
        {
            _localCategory = localCategory;
            if (rules == null) return;

            for (var i = 0; i < rules.Length; i++) _byType[rules[i].Type] = rules[i].Category;
        }

        public bool TryResolve(in NetworkEntityDescriptor entity, out int category)
        {
            if (entity.IsLocal && _localCategory.HasValue)
            {
                category = _localCategory.Value;
                return true;
            }

            return _byType.TryGetValue(entity.Type, out category);
        }
    }
}
