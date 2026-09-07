using Unity.Entities;

namespace Cuvara.DOTS.Configuration
{
    /// <summary>
    /// 2D sorting layer and order carried from a <see cref="ViewConfig"/>.
    /// </summary>
    /// <remarks>
    /// <b>Carried, deliberately not applied — and, as of this release, formally unsupported until a
    /// 2D consumer exists.</b> Nothing in this package touches a <c>SpriteRenderer</c>. The decision
    /// was re-examined for D08 and kept: no project consuming the package renders sprites (the client
    /// is a 3D XZ-plane world, every sample uses primitives), applying the order correctly means a
    /// per-view <c>SpriteRenderer</c> lookup in the managed sync pass or a cached component per view,
    /// and a value that is static per config is a one-line write at spawn once a real consumer tells
    /// us whether it belongs on the root renderer, on children, or on a sorting group. Building and
    /// testing that against no consumer is exactly the speculative work the improvement plan forbids.
    /// Authoring it now costs two ints and means the 2D branch does not have to re-open the config
    /// asset format; pretending it were live would be the worse half of that trade. See
    /// <c>Documentation~/SUPPORT-MATRIX.md</c>.
    /// </remarks>
    public struct ViewSortingKey : IComponentData
    {
        public int LayerId;
        public int Order;
    }
}
