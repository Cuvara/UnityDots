namespace Cuvara.DOTS.Provisioning
{
    /// <summary>
    /// Lifecycle state of a chunk in <see cref="ChunkViewProvisioner"/>.
    /// </summary>
    /// <remarks>
    /// Legal transitions, all on the main thread:
    /// <list type="bullet">
    /// <item>untracked → <see cref="Warming"/> or <see cref="Warm"/> — <c>PrewarmChunkAsync</c>;
    /// Warm directly when every key was already warm from another chunk.</item>
    /// <item><see cref="Warming"/> → <see cref="Warm"/> — the prewarm that opened this epoch completed.</item>
    /// <item><see cref="Warming"/> → <see cref="Failed"/> → untracked — that prewarm faulted or was
    /// cancelled; the chunk's references are rolled back in the same step.</item>
    /// <item><see cref="Warming"/> or <see cref="Warm"/> → <see cref="Warming"/> or <see cref="Warm"/>
    /// — re-warm of the same id opens a new epoch; the old prewarm's completion is then ignored.</item>
    /// <item>any tracked → <see cref="Released"/> → untracked — <c>ReleaseChunk</c>.</item>
    /// </list>
    /// <see cref="Pending"/> is reserved and never emitted: intake is synchronous, so a chunk is
    /// <see cref="Warming"/> the instant the call returns.
    /// </remarks>
    public enum ChunkState
    {
        /// <summary>Reserved. Intake is synchronous, so this state is never observed.</summary>
        Pending,

        /// <summary>Assets are loading asynchronously.</summary>
        Warming,

        /// <summary>All assets loaded and pooled — spawns are hitch-free.</summary>
        Warm,

        /// <summary>Chunk released — views cascade-despawned, assets returned to pool.</summary>
        Released,

        /// <summary>
        /// The prewarm faulted or was cancelled. The chunk's references were rolled back and it is no
        /// longer tracked; the caller may retry with the same id.
        /// </summary>
        Failed,
    }
}
