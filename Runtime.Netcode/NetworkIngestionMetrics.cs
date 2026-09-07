using System.Diagnostics;
using System.Threading;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Counters describing the path from <see cref="DotsEntityView"/>'s queue to the world:
    /// how much is pending, how old the oldest pending command is, how long a drain takes, and
    /// how many commands or samples were refused and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured before anything about the queue is bounded, on purpose.</b> The drain applies
    /// the whole queue every frame; whether that is a cost, and whether coalescing would be worth
    /// its ordering risk, is a question these numbers answer on a real device. Until they say
    /// otherwise, the queue stays unbounded and the numbers are what a host reads into its own
    /// diagnostics overlay.
    /// </para>
    /// <para>
    /// <b>Two writers, one per side of the queue.</b> The producer thread increments
    /// <see cref="Enqueued"/> and raises <see cref="PendingHighWatermark"/>; the drain thread
    /// writes everything else. The producer-side fields use <see cref="Interlocked"/>; the
    /// drain-side fields are plain because they have exactly one writer. Reads from any thread are
    /// diagnostics and may see a value one frame old.
    /// </para>
    /// </remarks>
    public sealed class NetworkIngestionMetrics
    {
        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private long _enqueued;
        private int _pendingHighWatermark;

        /// <summary>Seconds on a monotonic clock. The same clock stamps commands and measures drains.</summary>
        public static double Now => Stopwatch.GetTimestamp() * TicksToSeconds;

        /// <summary>Commands ever enqueued, across generations.</summary>
        public long Enqueued => Interlocked.Read(ref _enqueued);

        /// <summary>Commands ever dequeued by the drain, applied or dropped.</summary>
        public long Drained { get; private set; }

        /// <summary>Highest <c>PendingCommands</c> observed at any enqueue.</summary>
        public int PendingHighWatermark => Volatile.Read(ref _pendingHighWatermark);

        /// <summary>Drains that dequeued at least one command.</summary>
        public int Drains { get; private set; }

        /// <summary>Commands the most recent non-empty drain dequeued.</summary>
        public int LastDrainCount { get; private set; }

        /// <summary>Wall seconds the most recent non-empty drain took.</summary>
        public double LastDrainSeconds { get; private set; }

        /// <summary>Longest drain so far, in seconds.</summary>
        public double MaxDrainSeconds { get; private set; }

        /// <summary>
        /// Age, at the start of the most recent non-empty drain, of the oldest command it found —
        /// how long the head of the queue waited for a frame.
        /// </summary>
        public double LastOldestCommandAgeSeconds { get; private set; }

        /// <summary>Oldest command age ever observed at a drain start.</summary>
        public double MaxOldestCommandAgeSeconds { get; private set; }

        /// <summary>
        /// Commands dropped because they carried a generation older than the view's current one —
        /// data from a session that had already been reset. See <see cref="DotsEntityView.BeginGeneration"/>.
        /// </summary>
        public long StaleCommandsDropped { get; private set; }

        /// <summary>
        /// Timed states the sample ring refused: a duplicate or reordered tick. Counted rather than
        /// applied, because a refused sample must not fall back to a direct transform write.
        /// </summary>
        public long RejectedSamples { get; private set; }

        /// <summary>
        /// Untimed states that reached an entity already owned by interpolation (it holds buffered
        /// samples). The transform was left to the interpolation job; only the anchor and hp were
        /// written. A non-zero value means a consumer is feeding one entity through both
        /// <c>SetState</c> and <c>SetStateAtTick</c>.
        /// </summary>
        public long MixedPathStates { get; private set; }

        /// <summary>Session resets the drain has applied.</summary>
        public int GenerationResets { get; private set; }

        internal void NoteEnqueued(int pendingAfter)
        {
            Interlocked.Increment(ref _enqueued);

            var observed = Volatile.Read(ref _pendingHighWatermark);
            while (pendingAfter > observed)
            {
                var previous = Interlocked.CompareExchange(ref _pendingHighWatermark, pendingAfter, observed);
                if (previous == observed) break;
                observed = previous;
            }
        }

        internal void NoteDrain(int count, double seconds, double oldestAgeSeconds)
        {
            if (count <= 0) return;

            Drains++;
            Drained += count;
            LastDrainCount = count;
            LastDrainSeconds = seconds;
            if (seconds > MaxDrainSeconds) MaxDrainSeconds = seconds;
            LastOldestCommandAgeSeconds = oldestAgeSeconds;
            if (oldestAgeSeconds > MaxOldestCommandAgeSeconds) MaxOldestCommandAgeSeconds = oldestAgeSeconds;
        }

        internal void NoteStaleDropped() => StaleCommandsDropped++;

        internal void NoteRejectedSample() => RejectedSamples++;

        internal void NoteMixedPath() => MixedPathStates++;

        internal void NoteGenerationReset() => GenerationResets++;
    }
}
