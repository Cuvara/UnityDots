using UnityEngine;

namespace Cuvara.DOTS.Samples.PhaseBShowcase
{
    /// <summary>
    /// Inactive primitive GameObjects used as prefabs for <c>PooledViewAssetProvider.RegisterPrefab</c>,
    /// so these scenes need no authored prefab assets and no Addressables.
    /// </summary>
    /// <remarks>
    /// A scene object works as a prefab here: the provider only ever calls <c>Instantiate</c> on it
    /// and never activates the template itself.
    /// </remarks>
    public static class PrimitiveTemplates
    {
        /// <summary>
        /// Creates an inactive, collider-free coloured primitive parented under <paramref name="parent"/>.
        /// </summary>
        public static GameObject Create(string name, PrimitiveType primitive, Color color, Transform parent)
        {
            var template = GameObject.CreatePrimitive(primitive);
            template.name = "[template] " + name;
            template.transform.SetParent(parent, false);
            template.SetActive(false);

            // These scenes demonstrate view provisioning, not physics; a stray collider on every
            // pooled instance would be dead weight. The physics scene builds its own bodies.
            var collider = template.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            Tint(template, color);
            return template;
        }

        /// <summary>
        /// Colours a renderer through a property block, so no per-instance material is created
        /// and nothing leaks when the pool destroys the instance.
        /// </summary>
        public static void Tint(GameObject instance, Color color)
        {
            if (instance == null) return;

            var renderer = instance.GetComponent<Renderer>();
            if (renderer == null) return;

            var block = new MaterialPropertyBlock();
            // URP reads _BaseColor, the built-in pipeline reads _Color. Setting both keeps the
            // sample legible whichever pipeline the importing project uses.
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }
    }
}
