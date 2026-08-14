using Unity.Collections;
using UnityEngine;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// Authoring asset describing one kind of view: which prefab, how many to keep warm, and how the
    /// instance is offset from its entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ScriptableObject, and no <c>Baker</c>.</b> Baking runs over a subscene at build or
    /// conversion time, and produces entities that already exist when the scene loads. This package's
    /// consumers spawn from server snapshots at runtime, where there is no subscene and no authoring
    /// GameObject to bake — the entity appears because a packet arrived. So the conversion here is a
    /// plain runtime call (<see cref="ToRecord"/>, driven by <see cref="ViewConfigCatalog"/>) rather
    /// than a baking pipeline, which also keeps the package free of a dependency on
    /// <c>Unity.Entities.Hybrid</c>.
    /// </para>
    /// <para>
    /// It lives in the core assembly because <see cref="ScriptableObject"/> costs nothing to
    /// reference — it is part of the engine, not of an optional package — so the five-assembly rule
    /// is untouched.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(menuName = "Cuvara/DOTS/View Config", fileName = "ViewConfig")]
    public sealed class ViewConfig : ScriptableObject
    {
        [Tooltip("Asset/pool key of the view prefab. This is what IViewAssetProvider is asked for.")]
        [SerializeField] private string viewKey = string.Empty;

        [Tooltip("Instances to keep warm. A chunk prewarming this config asks for at least this many.")]
        [Min(0)] [SerializeField] private int poolSize = 1;

        [Tooltip("Uniform scale multiplier applied on top of the entity's own scale.")]
        [Min(0.0001f)] [SerializeField] private float scale = 1f;

        [Tooltip("Offset from the entity's position, in the entity's local space.")]
        [SerializeField] private Vector3 positionOffset = Vector3.zero;

        [Tooltip("Rotation offset in euler degrees, applied after the entity's rotation.")]
        [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

        [Header("2D")]
        [Tooltip("Sorting layer id. Carried through to the entity but NOT applied yet — see remarks.")]
        [SerializeField] private int sortingLayerId;

        [Tooltip("Sorting order within the layer. Carried through but NOT applied yet.")]
        [SerializeField] private int sortingOrder;

        public string ViewKey => viewKey;

        public int PoolSize => poolSize;

        public float Scale => scale;

        public Vector3 PositionOffset => positionOffset;

        public Vector3 RotationOffsetEuler => rotationOffsetEuler;

        public int SortingLayerId => sortingLayerId;

        public int SortingOrder => sortingOrder;

        /// <summary>
        /// Projects this asset onto the unmanaged record stored in <see cref="ViewConfigTable"/>.
        /// </summary>
        /// <param name="nameHash">
        /// Stable hash of the archetype name this config is registered under, so a runtime lookup by
        /// name needs no managed string.
        /// </param>
        /// <remarks>
        /// <b>A view key longer than 61 UTF-8 bytes is truncated, not rejected.</b> The record's key
        /// is a <see cref="FixedString64Bytes"/>, and the choice is between truncating and throwing.
        /// Truncating keeps one over-long key from taking down catalog construction — and with it the
        /// whole session — for every other archetype in the library. It is loud about it: the warning
        /// names the offending asset, and the truncated key will not match anything in the pool, so
        /// the view simply never spawns rather than spawning something wrong.
        /// </remarks>
        public ViewConfigRecord ToRecord(int nameHash)
        {
            // CopyFromTruncated is an extension method in Unity.Collections (FixedStringAppendMethods),
            // so the namespace has to be imported — fully-qualifying the types is not enough to bring
            // an extension method into scope. The implicit string conversion is avoided because it
            // throws on overflow; see the remarks for why truncation is the chosen behaviour.
            var key = default(FixedString64Bytes);
            if (key.CopyFromTruncated(viewKey ?? string.Empty) != CopyError.None)
            {
                Debug.LogWarning(
                    $"[Cuvara.DOTS] ViewConfig '{name}' has a view key longer than 61 bytes; it was " +
                    "truncated and will not match the pool. Shorten the key.");
            }

            return new ViewConfigRecord
            {
                NameHash = nameHash,
                ViewKey = key,
                PoolSize = poolSize < 0 ? 0 : poolSize,
                Scale = scale <= 0f ? 1f : scale,
                PositionOffset = positionOffset,
                RotationOffset = Quaternion.Euler(rotationOffsetEuler),
                SortingLayerId = sortingLayerId,
                SortingOrder = sortingOrder,
            };
        }

        /// <summary>
        /// Sets every authored field in one call.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Intended for tests and for code that generates configs. The fields are
        /// <c>[SerializeField]</c> private so the inspector owns them, and the alternative in a test
        /// is <c>SerializedObject</c> with string property names — which drags UnityEditor into the
        /// play-mode assembly and silently no-ops if a field is ever renamed.
        /// </para>
        /// <para>
        /// <b>Public since 0.15.0, and the reason is a contradiction this docstring was carrying.</b>
        /// It has always said "intended for tests and for code that generates configs" while being
        /// <c>internal</c>, so no consumer could be the second kind of caller. Building
        /// <c>Samples~/NetworkedPrediction</c> is what surfaced it: a package whose whole premise is
        /// spawning from server snapshots at runtime must let a consumer build a catalog without
        /// authored assets, and the fields are <c>[SerializeField] private</c> so there is no other
        /// way to do it outside the Editor.
        /// </remarks>
        public void Configure(
            string key,
            int pool = 1,
            float uniformScale = 1f,
            Vector3 position = default,
            Vector3 rotationEuler = default,
            int layerId = 0,
            int order = 0)
        {
            viewKey = key;
            poolSize = pool;
            scale = uniformScale;
            positionOffset = position;
            rotationOffsetEuler = rotationEuler;
            sortingLayerId = layerId;
            sortingOrder = order;
            OnValidate();
        }

        private void OnValidate()
        {
            // Clamped here as well as by [Min]: a value set from a script or a merge conflict does
            // not go through the inspector's attribute, and a zero scale is an invisible view that
            // looks like a spawn failure.
            if (poolSize < 0) poolSize = 0;
            if (scale <= 0f) scale = 1f;
        }
    }
}
