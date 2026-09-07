namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// What <see cref="PooledViewAssetProvider.Dispose"/> does with instances that are still
    /// acquired — handed out and never returned — at the moment of disposal.
    /// </summary>
    /// <remarks>
    /// Every instance the provider hands out was instantiated by the provider, so the provider is
    /// its owner either way. The question this answers is only whether ownership is <i>exercised</i>
    /// (destroy) or <i>transferred</i> (detach) when the provider goes away. Neither option loses
    /// track silently: the count of outstanding leases is logged as a warning in both cases,
    /// because a lease still open at dispose is a despawn the host forgot.
    /// </remarks>
    public enum OutstandingLeasePolicy
    {
        /// <summary>
        /// Destroy outstanding instances. The default: a disposed provider leaves no GameObject of
        /// its own behind, which is what a scene or session teardown wants.
        /// </summary>
        Destroy,

        /// <summary>
        /// Leave outstanding instances alive and forget them; the holder now owns them and is
        /// responsible for destroying them. For hosts that deliberately hand a view over to another
        /// system before tearing the pool down.
        /// </summary>
        Detach,
    }
}
