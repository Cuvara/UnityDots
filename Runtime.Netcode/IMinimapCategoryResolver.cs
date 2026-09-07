namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Decides whether a replicated entity appears on the minimap and under which presentation
    /// category. Optional constructor argument of <see cref="DotsEntityView"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is how the minimap stays inside the area of interest.</b> The resolver is consulted
    /// once per spawn, and the drain puts a <c>MinimapMarker</c> on the mirror entity it creates —
    /// which exists exactly as long as the server lists the id. An entity the server omitted has no
    /// mirror, so it has no marker, so it is not on the map, and nothing a host does with this
    /// interface can change that. The host still decides <i>which</i> replicated kinds show and as
    /// what.
    /// </para>
    /// <para>
    /// Resolved on the caller's thread at enqueue time, like <see cref="INetworkArchetypeResolver"/>,
    /// and for the same reason: the drain must not call consumer code from inside a system update.
    /// </para>
    /// </remarks>
    public interface IMinimapCategoryResolver
    {
        /// <summary>
        /// The category for <paramref name="entity"/>, or false to keep it off the map. The value is
        /// the host's to interpret; the package assigns it no meaning.
        /// </summary>
        bool TryResolve(in NetworkEntityDescriptor entity, out int category);
    }
}
