using System;
using Cuvara.DOTS.Messaging;
using UnityEngine;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// Where <see cref="NetworkEntitySpawned"/> and <see cref="NetworkEntityDespawned"/> are
    /// delivered. One per <see cref="DotsEntityView"/>, and so one per session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a bus, and the 0.5.0 messaging rule still stands.</b> <c>IDotsPublisher</c> exists so
    /// the core never names MessagePipe, and the package refuses to grow a general signal bus behind
    /// it. This class is narrower than that: two event types, one owner, synchronous delivery on the
    /// drain's thread, no queue, no replay. It exists because the drain system is an
    /// <c>ISystem</c> and cannot hold a delegate, and because a consumer without MessagePipe still
    /// needs somewhere to hand a handler — the null-publisher answer ("messages are dropped") is right
    /// for diagnostics like <c>ChunkWarmed</c> and wrong for a lifecycle a game must react to.
    /// With MessagePipe present, <c>Cuvara.DOTS.DI</c> constructs this with forwarding publishers
    /// and both routes deliver the same events in the same order.
    /// </para>
    /// <para>
    /// <b>Ordering.</b> Events are published in command order, one at a time, each after its
    /// structural change is applied and before the next command is drained. Handlers run in
    /// subscription order. Within one handler invocation the world is stable: the drain does no work
    /// while a handler runs.
    /// </para>
    /// <para>
    /// <b>Error isolation.</b> A handler that throws is logged via <see cref="Debug.LogException"/>
    /// and the remaining handlers, the forwarding publisher and the drain all continue. A throwing
    /// handler is therefore never a reason for a mirror entity to be missing or leaked. Exceptions
    /// are not rethrown; if the throw was <i>your</i> assertion, read the console.
    /// </para>
    /// <para>
    /// <b>Late subscription is not retroactive.</b> Events are ephemeral: a subscriber added after an
    /// entity spawned hears nothing about it and must query <see cref="NetworkEntity"/> for the
    /// current set. A subscription added or disposed <i>during</i> dispatch takes effect from the
    /// next event, not the current one.
    /// </para>
    /// <para>
    /// <b>Thread affinity.</b> Publishing happens on whichever thread updates
    /// <c>NetcodeSystemGroup</c> — the main thread in every supported configuration. Subscribe and
    /// dispose from that same thread; the handler list is not synchronised because nothing here
    /// crosses threads (the command queue does, and it is the only thing that has to).
    /// </para>
    /// <para>
    /// <b>Teardown.</b> <see cref="Clear"/> drops every handler without publishing; the instance is
    /// owned by the view and dies with it. Whether present entities get a terminal
    /// <see cref="NetworkEntityDespawned"/> at session end is <c>DotsNetcodeBootstrap.Uninstall</c>'s
    /// decision, not this class's.
    /// </para>
    /// </remarks>
    public sealed class NetworkEntityLifecycle : IDotsSubscriber<NetworkEntitySpawned>, IDotsSubscriber<NetworkEntityDespawned>
    {
        private readonly IDotsPublisher<NetworkEntitySpawned> _forwardSpawned;
        private readonly IDotsPublisher<NetworkEntityDespawned> _forwardDespawned;

        // Copy-on-write arrays: dispatch iterates a snapshot, so a handler subscribing or disposing
        // mid-dispatch never mutates the list being walked. Subscribe/dispose are rare; publish is
        // per spawn/despawn, and neither allocates on the publish path.
        private Action<NetworkEntitySpawned>[] _spawned = Array.Empty<Action<NetworkEntitySpawned>>();
        private Action<NetworkEntityDespawned>[] _despawned = Array.Empty<Action<NetworkEntityDespawned>>();

        /// <param name="forwardSpawned">
        /// Optional second destination — with MessagePipe present, the adapter that publishes into
        /// <c>IPublisher&lt;NetworkEntitySpawned&gt;</c>. Null forwards nowhere.
        /// </param>
        /// <param name="forwardDespawned">Same, for despawns.</param>
        public NetworkEntityLifecycle(
            IDotsPublisher<NetworkEntitySpawned> forwardSpawned = null,
            IDotsPublisher<NetworkEntityDespawned> forwardDespawned = null)
        {
            _forwardSpawned = forwardSpawned ?? NullDotsPublisher<NetworkEntitySpawned>.Instance;
            _forwardDespawned = forwardDespawned ?? NullDotsPublisher<NetworkEntityDespawned>.Instance;
        }

        /// <summary>
        /// Spawned events delivered so far. Diagnostics. Counts deliveries, not lives: the drain
        /// publishes only while <see cref="HasObservers"/> is true, so a session nobody observed reads
        /// zero.
        /// </summary>
        public int SpawnedCount { get; private set; }

        /// <summary>Despawned events delivered so far. Diagnostics; same caveat as <see cref="SpawnedCount"/>.</summary>
        public int DespawnedCount { get; private set; }

        /// <summary>Handlers currently attached, both kinds. Diagnostics and tests.</summary>
        public int SubscriberCount => _spawned.Length + _despawned.Length;

        /// <summary>
        /// True when at least one handler or a forwarding publisher would observe a publish. The
        /// drain checks this before building the managed event, so an unobserved session pays no
        /// string allocation per spawn.
        /// </summary>
        public bool HasObservers =>
            _spawned.Length > 0
            || _despawned.Length > 0
            || !ReferenceEquals(_forwardSpawned, NullDotsPublisher<NetworkEntitySpawned>.Instance)
            || !ReferenceEquals(_forwardDespawned, NullDotsPublisher<NetworkEntityDespawned>.Instance);

        /// <summary>Hears every spawn from now on. Dispose the handle to stop; disposing twice is a no-op.</summary>
        public IDisposable Subscribe(Action<NetworkEntitySpawned> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _spawned = Append(_spawned, handler);
            return new Subscription(() => _spawned = Remove(_spawned, handler));
        }

        /// <summary>Hears every despawn from now on. Dispose the handle to stop; disposing twice is a no-op.</summary>
        public IDisposable Subscribe(Action<NetworkEntityDespawned> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _despawned = Append(_despawned, handler);
            return new Subscription(() => _despawned = Remove(_despawned, handler));
        }

        /// <summary>Drops every handler without publishing anything. For session teardown.</summary>
        public void Clear()
        {
            _spawned = Array.Empty<Action<NetworkEntitySpawned>>();
            _despawned = Array.Empty<Action<NetworkEntityDespawned>>();
        }

        /// <summary>Called by the drain after a mirror entity has been created.</summary>
        internal void PublishSpawned(in NetworkEntitySpawned message)
        {
            SpawnedCount++;

            var handlers = _spawned;
            for (var i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](message);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            try
            {
                _forwardSpawned.Publish(message);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>Called by the drain before a mirror entity is destroyed, or after one was found destroyed.</summary>
        internal void PublishDespawned(in NetworkEntityDespawned message)
        {
            DespawnedCount++;

            var handlers = _despawned;
            for (var i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](message);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            try
            {
                _forwardDespawned.Publish(message);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static T[] Append<T>(T[] source, T item)
        {
            var result = new T[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[source.Length] = item;
            return result;
        }

        private static T[] Remove<T>(T[] source, T item) where T : class
        {
            var index = Array.IndexOf(source, item);
            if (index < 0) return source;

            if (source.Length == 1) return Array.Empty<T>();

            var result = new T[source.Length - 1];
            Array.Copy(source, 0, result, 0, index);
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
            return result;
        }

        private sealed class Subscription : IDisposable
        {
            private Action _dispose;

            public Subscription(Action dispose) => _dispose = dispose;

            public void Dispose()
            {
                var dispose = _dispose;
                _dispose = null;
                dispose?.Invoke();
            }
        }
    }
}
