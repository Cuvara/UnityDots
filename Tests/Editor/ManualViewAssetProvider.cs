using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Provisioning;
using UnityEngine;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// An <see cref="IViewAssetProvider"/> whose prewarms stay pending until the test completes,
    /// faults or cancels them — so the provisioner's behaviour <i>during</i> a load can be asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Complements <see cref="RecordingViewAssetProvider"/>, which completes synchronously and
    /// therefore cannot express "released while warming" or "failed after partial completion".
    /// </para>
    /// <para>
    /// Continuations run inline on <c>Complete</c>/<c>Fault</c>/<c>Cancel</c> only when no
    /// <see cref="SynchronizationContext"/> is installed; the Unity editor installs one that defers
    /// them to the next editor tick, which a synchronous NUnit test never reaches. Tests using this
    /// fake clear the context in <c>SetUp</c> and restore it in <c>TearDown</c> — see
    /// <see cref="ChunkProvisioningEpochTests"/>.
    /// </para>
    /// </remarks>
    internal sealed class ManualViewAssetProvider : IViewAssetProvider
    {
        private sealed class Pending
        {
            public string Key;
            public TaskCompletionSource<bool> Source;
            public CancellationTokenRegistration Registration;
        }

        private readonly List<Pending> _pending = new List<Pending>();

        /// <summary>Every key a prewarm was requested for, in order, including repeats.</summary>
        public readonly List<string> Prewarmed = new List<string>();

        /// <summary>Every key released, in order.</summary>
        public readonly List<string> Released = new List<string>();

        /// <summary>Keys whose prewarm has completed successfully and not been released since.</summary>
        public readonly HashSet<string> Warm = new HashSet<string>();

        /// <summary>Last count requested per key.</summary>
        public readonly Dictionary<string, int> WarmCounts = new Dictionary<string, int>();

        /// <summary>Prewarms still pending.</summary>
        public int PendingCount => _pending.Count;

        public Task PrewarmAsync(string key, int count, CancellationToken cancellationToken = default)
        {
            Prewarmed.Add(key);
            WarmCounts[key] = count;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            var pending = new Pending { Key = key, Source = new TaskCompletionSource<bool>() };
            if (cancellationToken.CanBeCanceled)
            {
                // Honour the provisioner's token like a real loader would.
                pending.Registration = cancellationToken.Register(() =>
                {
                    if (_pending.Remove(pending)) pending.Source.TrySetCanceled(cancellationToken);
                });
            }

            _pending.Add(pending);
            return pending.Source.Task;
        }

        public bool IsWarm(string key) => Warm.Contains(key);

        public GameObject Acquire(string key, Vector3 position, Quaternion rotation, Transform parent = null) => null;

        public Task<GameObject> AcquireAsync(string key, Vector3 position, Quaternion rotation, Transform parent = null, CancellationToken cancellationToken = default)
            => Task.FromResult<GameObject>(null);

        public void ReleaseInstance(GameObject instance)
        {
        }

        public void Release(string key)
        {
            Released.Add(key);
            Warm.Remove(key);
        }

        /// <summary>Completes every pending prewarm for <paramref name="key"/>. Returns how many.</summary>
        public int Complete(string key) => Settle(key, p => { Warm.Add(p.Key); p.Source.TrySetResult(true); });

        /// <summary>Faults every pending prewarm for <paramref name="key"/>. Returns how many.</summary>
        public int Fault(string key, Exception error = null) =>
            Settle(key, p => p.Source.TrySetException(error ?? new InvalidOperationException($"load of '{p.Key}' failed")));

        /// <summary>Cancels every pending prewarm for <paramref name="key"/>. Returns how many.</summary>
        public int Cancel(string key) => Settle(key, p => p.Source.TrySetCanceled());

        /// <summary>Completes every pending prewarm regardless of key.</summary>
        public int CompleteAll() => Settle(null, p => { Warm.Add(p.Key); p.Source.TrySetResult(true); });

        private int Settle(string key, Action<Pending> action)
        {
            var matches = new List<Pending>();
            foreach (var p in _pending)
            {
                if (key == null || p.Key == key) matches.Add(p);
            }

            // Remove before settling: a continuation may issue a new prewarm for the same key.
            foreach (var p in matches) _pending.Remove(p);
            foreach (var p in matches)
            {
                p.Registration.Dispose();
                action(p);
            }

            return matches.Count;
        }
    }
}
