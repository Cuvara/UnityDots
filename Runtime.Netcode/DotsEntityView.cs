using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Cuvara.DOTS.Configuration;
using Cuvara.Netcode.View;
using Unity.Collections;
using UnityEngine;

namespace Cuvara.DOTS.Netcode
{
    /// <summary>
    /// <c>IEntityView</c> over ECS: replicated ids become entities carrying
    /// <see cref="NetworkEntity"/>, a <c>LocalTransform</c> and a view request, and the package's
    /// existing view pipeline turns those into GameObjects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enqueues; it does not write components.</b> Every call appends to a queue that
    /// <c>NetworkViewCommandSystem</c> drains inside <see cref="Groups.NetcodeSystemGroup"/>. Three
    /// reasons, in order of how much they matter:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Thread affinity.</b> <c>WorldViewBinder.Tick</c> is called by the consumer, and the
    /// netcode's own guidance is to drive world state from the thread that consumes the socket —
    /// which is not necessarily Unity's main thread. <c>EntityManager</c> writes from another thread
    /// are undefined behaviour, not an exception. A queue is the only shape that makes the seam safe
    /// no matter where the consumer ticks it; the reference implementation is main-thread-only and
    /// does not say so.
    /// </description></item>
    /// <item><description>
    /// <b>Structural changes belong at a declared point in the frame.</b> Creating and destroying
    /// entities is a sync point. Done from the caller it lands wherever the caller happens to run —
    /// possibly mid-<c>SimulationSystemGroup</c>, completing every job in flight. Drained in
    /// <see cref="Groups.NetcodeSystemGroup"/> it lands where this package already declares that
    /// "wire traffic reaches the world", which is what that group was created for and why it shipped
    /// empty in 0.6.
    /// </description></item>
    /// <item><description>
    /// <b>Ordering.</b> One FIFO preserves spawn → state → despawn per id and between ids. Direct
    /// writes interleaved with anything queued would not.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The queue does not cost a frame of view latency.</b> <c>NetcodeSystemGroup</c> sits in
    /// <c>InitializationSystemGroup</c>, so a drain runs before <c>SimulationSystemGroup</c> and
    /// therefore before <c>TransformSystemGroup</c> computes <c>LocalToWorld</c>, and long before
    /// <c>PresentationSystemGroup</c> runs <c>ViewSystemGroup</c>. A snapshot enqueued before frame
    /// N's initialization becomes an entity, a transform, a view and a positioned view all within
    /// frame N. Writing directly from the caller cannot beat that and can lose to it: a direct write
    /// landing after <c>TransformSystemGroup</c> has already run gets its <c>LocalToWorld</c> a frame
    /// late, so its view spawns at a stale position. The queue's real exposure is the other end —
    /// a snapshot arriving after frame N's initialization waits for frame N+1, at most one frame,
    /// which is exactly the same wait a direct write would face at the transform stage.
    /// </para>
    /// <para>
    /// <b>One caller thread, whichever one that is.</b> The three <c>IEntityView</c> methods share
    /// unsynchronised bookkeeping and must be called from a single thread — which costs nothing,
    /// because <c>WorldViewBinder</c> is not thread-safe either and is the only thing that calls
    /// them. The queue is the one structure that crosses threads, and it is the only one that has
    /// to. Locking the bookkeeping as well would buy nothing and hide the requirement.
    /// </para>
    /// <para>
    /// <b>Not thread-safe against a catalog rebuild.</b> Archetype names are resolved to config
    /// indices here, at enqueue time, by reading <see cref="ViewConfigCatalog"/>. That is a
    /// dictionary read with no Unity API in it, so it is legal off the main thread — but
    /// <c>ViewConfigCatalog.Build</c> mutates the same dictionary and already documents that it is
    /// only safe between frames. Rebuilding the catalog while a socket thread is enqueueing breaks
    /// that; the consumer owns the sequencing.
    /// </para>
    /// </remarks>
    public sealed class DotsEntityView : IEntityView
    {
        private readonly ConcurrentQueue<NetworkViewCommand> _commands = new ConcurrentQueue<NetworkViewCommand>();
        private readonly HashSet<string> _live = new HashSet<string>();
        private readonly Dictionary<string, int> _configIndexById = new Dictionary<string, int>();
        private readonly HashSet<string> _unresolvedArchetypes = new HashSet<string>();

        private readonly INetworkArchetypeResolver _resolver;
        private readonly ViewConfigCatalog _catalog;
        private readonly SnapshotSpaceMapping _mapping;
        private readonly bool _writeHealth;

        /// <param name="catalog">
        /// The session's built catalog. Archetype names resolve against it, and the resulting index
        /// and view key are carried on the command so the drain needs no managed lookup.
        /// </param>
        /// <param name="resolver">
        /// Decides the archetype from the server's entity kind. See
        /// <see cref="TypeArchetypeResolver"/> for the built-in implementation.
        /// </param>
        /// <param name="mapping">
        /// Where the server's 2D plane lives in the client's world. Defaults to
        /// <see cref="SnapshotSpaceMapping.XZPlane"/> when left at <c>default</c>, because a
        /// zero mapping collapses the world onto the origin and reads as a networking fault.
        /// </param>
        /// <param name="writeHealth">
        /// Also mirror the wire's hp into <see cref="Cuvara.DOTS.Simulation.Health"/>. <b>Off by
        /// default.</b> <c>Health</c> means "destroy at zero" in this package, so turning this on
        /// lets a client-side system destroy the mirror of an entity the server still lists — see
        /// <see cref="NetworkEntityState"/>. <see cref="NetworkEntityState"/> is written either way.
        /// </param>
        public DotsEntityView(
            ViewConfigCatalog catalog,
            INetworkArchetypeResolver resolver,
            SnapshotSpaceMapping mapping = default,
            bool writeHealth = false)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _mapping = mapping.IsPopulated ? mapping : SnapshotSpaceMapping.XZPlane;
            _writeHealth = writeHealth;
        }

        /// <summary>Ids this view believes are present. Counts enqueues, not applied entities.</summary>
        public int Count => _live.Count;

        /// <summary>Commands enqueued and not yet drained. Diagnostics; zero every frame in health.</summary>
        public int PendingCommands => _commands.Count;

        /// <summary>Where the server's plane lands in the client's world.</summary>
        public SnapshotSpaceMapping Mapping => _mapping;

        /// <summary>True when the wire's hp is also written to <c>Health</c>.</summary>
        public bool WritesHealth => _writeHealth;

        public void Spawn(string id, bool isLocal, string type)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_live.Add(id)) return;

            var descriptor = new NetworkEntityDescriptor(id, type, isLocal);

            if (!TryResolveConfig(descriptor, out var index, out var key))
            {
                // Resolution failed loudly already — either the resolver refused the kind, or the
                // archetype it named is not in the catalog. Left out of _live so a later snapshot
                // with a working catalog can still spawn it.
                _live.Remove(id);
                return;
            }

            var wireId = default(FixedString64Bytes);
            if (wireId.CopyFromTruncated(id) != CopyError.None)
            {
                // Truncating would produce an id that mis-targets rather than one that fails, so
                // this entity is refused instead. 61 bytes comfortably holds a Nakama UUID.
                Debug.LogError($"[Cuvara.DOTS] Replicated id '{id}' exceeds 61 bytes and cannot be presented.");
                _live.Remove(id);
                return;
            }

            // Truncated rather than refused, unlike the id: this copy is a convenience field for
            // consumer queries, and resolution already ran on the full managed string above, so a
            // long kind still reached the right archetype.
            var wireType = default(FixedString32Bytes);
            wireType.CopyFromTruncated(descriptor.Type);

            _commands.Enqueue(new NetworkViewCommand
            {
                Kind = NetworkViewCommandKind.Spawn,
                Id = wireId,
                Type = wireType,
                IsLocal = isLocal,
                ConfigIndex = index,
                ViewKey = key,
            });
        }

        public void Despawn(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_live.Remove(id)) return;

            _configIndexById.Remove(id);

            var wireId = default(FixedString64Bytes);
            if (wireId.CopyFromTruncated(id) != CopyError.None) return;

            _commands.Enqueue(new NetworkViewCommand
            {
                Kind = NetworkViewCommandKind.Despawn,
                Id = wireId,
            });
        }

        public void SetState(string id, float x, float y, int hp, int maxHp)
        {
            SetStateAtTick(id, x, y, hp, maxHp, tick: 0L, receiveTimeSeconds: 0.0);
        }

        /// <summary>
        /// Latest authoritative state for an already-spawned id, <b>with the server tick it was
        /// true on</b> — so the mirror can be rendered by ECS interpolation instead of being
        /// teleported to each state as it lands.
        /// </summary>
        /// <param name="id">The replicated id, already spawned.</param>
        /// <param name="x">Server-space x, verbatim off the wire.</param>
        /// <param name="y">Server-space y, verbatim off the wire.</param>
        /// <param name="hp">Current hp. Never interpolated — a half-applied hit is not a state.</param>
        /// <param name="maxHp">Maximum hp.</param>
        /// <param name="tick">
        /// The server tick this state was true on, strictly increasing per id. Non-positive falls
        /// back to <see cref="SetState"/>'s behaviour exactly.
        /// </param>
        /// <param name="receiveTimeSeconds">
        /// Seconds on the caller's own monotonic clock when this state was received. Only the
        /// differences between successive values matter, so any epoch will do — but it must be the
        /// same clock every call, and it must be real time rather than a frame count.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Not part of <c>IEntityView</c>, and it cannot be.</b> That interface is netcode's, it
        /// carries five arguments and no tick, and widening it would make every GameObject view in
        /// every consumer implement a method it has no use for. This is the DOTS adapter's own
        /// entry point, for a caller that has the tick in its hand — a snapshot handler reading
        /// <c>WorldState.Tick</c> — and is willing to say so.
        /// </para>
        /// <para>
        /// <b>Choosing between the two methods chooses which layer smooths the motion, and choosing
        /// both would smooth it twice.</b> <c>WorldViewBinder.Tick</c> evaluates its own
        /// interpolation and hands the result to <see cref="SetState"/>, so a view driven by the
        /// binder is already receiving a rendered position: buffering those and interpolating again
        /// would stack a second render delay on top of the first and make remote entities lag by
        /// twice <c>InterpolationConfig.TargetDelay</c>, smoothly and without any error to notice.
        /// The two paths are therefore mutually exclusive per entity, and the drain enforces it
        /// rather than trusting it — a state with a tick is buffered and the transform is left to
        /// <see cref="RemoteInterpolationSystem"/>; a state without one is written straight to the
        /// transform and the entity's sample buffer stays empty, so the job passes over it.
        /// </para>
        /// <para>
        /// The intended shape for a DOTS consumer that wants ECS-side interpolation is therefore:
        /// keep using <c>WorldViewBinder</c> for spawn/despawn reconciliation, and feed positions
        /// through this from the snapshot handler that already knows the tick. A consumer that does
        /// not care keeps calling nothing and gets 0.23.1's behaviour unchanged.
        /// </para>
        /// </remarks>
        public void SetStateAtTick(string id, float x, float y, int hp, int maxHp, long tick, double receiveTimeSeconds)
        {
            if (string.IsNullOrEmpty(id)) return;

            // A state for an id that was never spawned is dropped rather than spawning one. Spawn
            // carries the entity's kind and its isLocal flag; this carries neither, so an implicit
            // spawn would have to invent both — and inventing the kind is precisely the guessing
            // that netcode 0.4.0 removed from this layer.
            if (!_live.Contains(id)) return;

            var wireId = default(FixedString64Bytes);
            if (wireId.CopyFromTruncated(id) != CopyError.None) return;

            _commands.Enqueue(new NetworkViewCommand
            {
                Kind = NetworkViewCommandKind.State,
                Id = wireId,
                X = x,
                Y = y,
                Hp = hp,
                MaxHp = maxHp,
                Tick = tick > 0L ? tick : 0L,
                ReceiveTime = receiveTimeSeconds,
            });
        }

        /// <summary>
        /// Takes the next queued command. Called by the drain system, and by tests that want to
        /// observe what a call produced without standing up a <c>World</c>.
        /// </summary>
        /// <remarks>
        /// One at a time rather than draining into a list, because the drain system is an
        /// <c>ISystem</c> and cannot hold a managed <c>List</c> field — a per-frame list would be a
        /// per-frame allocation, and a <c>NativeList</c> would be a copy of a copy.
        /// </remarks>
        internal bool TryDequeue(out NetworkViewCommand command) => _commands.TryDequeue(out command);

        /// <summary>
        /// Resolves this entity's archetype to a config index and view key, caching per id.
        /// </summary>
        /// <remarks>
        /// Cached because the resolver is consumer code and an entity's kind does not change over
        /// its lifetime — which is exactly why netcode passes the type at spawn rather than on every
        /// state. The cache is dropped on despawn so a re-entry into the AOI re-resolves.
        /// </remarks>
        private bool TryResolveConfig(in NetworkEntityDescriptor entity, out int index, out FixedString64Bytes key)
        {
            index = -1;
            key = default;

            if (_configIndexById.TryGetValue(entity.Id, out index))
            {
                key = _catalog[index].ViewKey;
                return true;
            }

            if (!_resolver.TryResolve(entity, out var archetypeName) || string.IsNullOrEmpty(archetypeName))
            {
                // Not an error here: a resolver returning false is saying this entity is not
                // presentable, and it is the thing that knows why — TypeArchetypeResolver logs the
                // unmapped kind itself.
                return false;
            }

            index = _catalog.IndexOf(archetypeName);
            if (index < 0)
            {
                // Once per archetype name, not once per spawn — a missing archetype affects every
                // entity of that kind and would otherwise fill the console at snapshot rate.
                if (_unresolvedArchetypes.Add(archetypeName))
                {
                    Debug.LogError(
                        $"[Cuvara.DOTS] Archetype '{archetypeName}' is not in the view catalog; " +
                        "entities resolving to it will not be presented. Add it to the ViewArchetypeLibrary.");
                }

                return false;
            }

            _configIndexById[entity.Id] = index;
            key = _catalog[index].ViewKey;
            return true;
        }
    }
}
