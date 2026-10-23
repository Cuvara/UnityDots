using Unity.Entities;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Marks the mirror of the local player's own entity — the one whose
    /// <see cref="NetworkEntity.IsLocal"/> is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tag so the local entity can be queried, not searched for.</b> Without it, a system that
    /// needs the one local entity has to match every mirror and read <see cref="NetworkEntity"/> on
    /// each to find it: main-thread work per frame that grows with the number of replicated
    /// entities, to locate one. <c>LocalPredictionSystem</c> did exactly that until this tag existed.
    /// </para>
    /// <para>
    /// <b>Added at spawn, together with every other component of a mirror, and never toggled.</b>
    /// Locality is fixed for the life of a mirror — a relocalize is a despawn and a spawn — so the
    /// archetype stays stable from the first frame, as the rest of the spawn set's does.
    /// <see cref="NetworkEntity.IsLocal"/> stays as well: it is what a job reading the component
    /// already has in hand.
    /// </para>
    /// <para>
    /// An entity built by hand rather than by the snapshot adapter must add this tag itself to be
    /// picked up by prediction.
    /// </para>
    /// </remarks>
    public struct LocalNetworkEntity : IComponentData
    {
    }
}
