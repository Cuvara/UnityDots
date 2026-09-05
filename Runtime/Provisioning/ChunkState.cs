namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// Lifecycle state of a chunk in <see cref="ChunkViewProvisioner"/>.
    /// </summary>
    public enum ChunkState
    {
        /// <summary>Prewarm requested but not yet started.</summary>
        Pending,

        /// <summary>Assets are loading asynchronously.</summary>
        Warming,

        /// <summary>All assets loaded and pooled — spawns are hitch-free.</summary>
        Warm,

        /// <summary>Chunk released — views cascade-despawned, assets returned to pool.</summary>
        Released,
    }
}
