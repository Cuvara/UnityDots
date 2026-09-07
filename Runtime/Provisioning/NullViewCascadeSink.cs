using System.Collections.Generic;

namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// The explicit "there is no view layer to cascade into" sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChunkViewProvisioner"/> refuses to be constructed without a sink, because a
    /// release that cannot reach the view layer strands every live view standing on the released
    /// keys — the dangling-link bug the cascade exists to prevent. This type is how a caller says,
    /// in code that a reviewer can grep for, that no views can exist: editor tooling, a warm-only
    /// preloader, a test over a recording provider. It is <b>not</b> a valid choice for a streaming
    /// world; that is <c>EntityViewCascade</c>.
    /// </para>
    /// </remarks>
    public sealed class NullViewCascadeSink : IViewCascadeSink
    {
        public static readonly NullViewCascadeSink Instance = new NullViewCascadeSink();

        private NullViewCascadeSink()
        {
        }

        /// <inheritdoc />
        public int CascadeDespawn(IReadOnlyCollection<string> keys) => 0;
    }
}
