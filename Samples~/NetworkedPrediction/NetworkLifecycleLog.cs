using System;
using System.Collections.Generic;
using Cuvara.DOTS.Netcode;
using UnityEngine;

namespace Cuvara.DOTS.Samples.NetworkedPrediction
{
    /// <summary>
    /// The sample's consumer of <see cref="NetworkEntitySpawned"/> / <see cref="NetworkEntityDespawned"/>:
    /// keeps the last few events for the overlay and a running present-count, and logs each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a real consumer looks like with no DI and no MessagePipe: hand two handlers to
    /// <see cref="DotsEntityView.Lifecycle"/>, keep the <see cref="IDisposable"/>s, dispose them at
    /// teardown. The handlers run on the main thread inside the drain, synchronously, so anything
    /// they touch — a HUD, a minimap, a VFX pool — sees the world exactly as the drain left it.
    /// </para>
    /// <para>
    /// <b>Present, not visible.</b> The count here is the number of replicated ids the drain holds a
    /// mirror for. The overlay prints it beside <c>mirror entities</c> and <c>pooled views</c>: the
    /// first two must agree while nothing destroys mirrors behind the adapter's back, and the third may
    /// lag both while a key is still warming. Seeing the three side by side is the point — network
    /// presence and visual presence are different lifecycles, and this sample exists to make such
    /// things observable rather than inferred.
    /// </para>
    /// <para>
    /// <b>Why a despawn is not a death here.</b> The wire does not say why an id left; an
    /// area-of-interest exit and a server-side removal arrive identically. The handler therefore
    /// prints the <see cref="NetworkDespawnReason"/> the adapter <i>can</i> know — wire despawn,
    /// external destruction, teardown — and nothing about hit points.
    /// </para>
    /// </remarks>
    public sealed class NetworkLifecycleLog : IDisposable
    {
        private const int Capacity = 6;

        private readonly Queue<string> _lines = new Queue<string>(Capacity);
        private readonly IDisposable _spawned;
        private readonly IDisposable _despawned;

        public NetworkLifecycleLog(NetworkEntityLifecycle lifecycle)
        {
            if (lifecycle == null) throw new ArgumentNullException(nameof(lifecycle));

            _spawned = lifecycle.Subscribe((NetworkEntitySpawned e) =>
            {
                Present++;
                Record($"+ {e.EntityId} ({e.EntityType}{(e.IsLocal ? ", local" : "")}) → {e.Entity}");
            });

            _despawned = lifecycle.Subscribe((NetworkEntityDespawned e) =>
            {
                Present--;
                Record($"- {e.EntityId} ({e.EntityType}) {e.Reason} ← {e.Entity}");
            });
        }

        /// <summary>Spawns seen minus despawns seen since this log subscribed.</summary>
        public int Present { get; private set; }

        /// <summary>The most recent events, oldest first.</summary>
        public IEnumerable<string> Lines => _lines;

        public void Dispose()
        {
            _spawned.Dispose();
            _despawned.Dispose();
        }

        private void Record(string line)
        {
            if (_lines.Count == Capacity) _lines.Dequeue();
            _lines.Enqueue(line);
            Debug.Log("[NetworkedPrediction] " + line);
        }
    }
}
