using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Netcode;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Netcode
{
    /// <summary>
    /// The hub on its own, with no world: dispatch order, re-entrancy, error isolation, forwarding.
    /// The drain-driven sequences are in <c>NetworkLifecycleEventTests</c>.
    /// </summary>
    public sealed class NetworkEntityLifecycleTests
    {
        private sealed class RecordingPublisher<T> : IDotsPublisher<T>
        {
            public readonly List<T> Received = new List<T>();
            public void Publish(T message) => Received.Add(message);
        }

        private static NetworkEntitySpawned Spawned(string id) =>
            new NetworkEntitySpawned(id, "player", false, new Entity { Index = 7, Version = 3 });

        private static NetworkEntityDespawned Despawned(string id, NetworkDespawnReason reason = NetworkDespawnReason.Despawned) =>
            new NetworkEntityDespawned(id, "player", false, new Entity { Index = 7, Version = 3 }, reason);

        [Test]
        public void Handlers_RunInSubscriptionOrder_ThenTheForwardingPublisher()
        {
            var forward = new RecordingPublisher<NetworkEntitySpawned>();
            var hub = new NetworkEntityLifecycle(forward, null);
            var order = new List<string>();

            using var first = hub.Subscribe((NetworkEntitySpawned e) => order.Add("first"));
            using var second = hub.Subscribe((NetworkEntitySpawned e) =>
            {
                order.Add("second");
                Assert.AreEqual(0, forward.Received.Count, "forwarding happens after every direct handler");
            });

            hub.PublishSpawned(Spawned("uuid-a"));

            CollectionAssert.AreEqual(new[] { "first", "second" }, order);
            Assert.AreEqual(1, forward.Received.Count);
            Assert.AreEqual("uuid-a", forward.Received[0].EntityId);
            Assert.AreEqual(1, hub.SpawnedCount);
        }

        [Test]
        public void HasObservers_TracksHandlersAndForwarders()
        {
            var bare = new NetworkEntityLifecycle();
            Assert.IsFalse(bare.HasObservers, "nothing attached, nothing forwarding");

            var subscription = bare.Subscribe((NetworkEntityDespawned e) => { });
            Assert.IsTrue(bare.HasObservers);
            subscription.Dispose();
            Assert.IsFalse(bare.HasObservers, "disposing the last handler turns publishing back off");

            var forwarding = new NetworkEntityLifecycle(null, new RecordingPublisher<NetworkEntityDespawned>());
            Assert.IsTrue(forwarding.HasObservers, "a forwarding publisher is an observer even with no handlers");
        }

        [Test]
        public void ThrowingHandler_IsLogged_AndEveryOtherHandlerAndTheForwarderStillRun()
        {
            LogAssert.Expect(LogType.Exception, new Regex("boom"));

            var forward = new RecordingPublisher<NetworkEntityDespawned>();
            var hub = new NetworkEntityLifecycle(null, forward);
            var heard = 0;

            using var throwing = hub.Subscribe((NetworkEntityDespawned e) => throw new InvalidOperationException("boom"));
            using var counting = hub.Subscribe((NetworkEntityDespawned e) => heard++);

            hub.PublishDespawned(Despawned("uuid-a"));

            Assert.AreEqual(1, heard);
            Assert.AreEqual(1, forward.Received.Count);
            Assert.AreEqual(1, hub.DespawnedCount);
        }

        [Test]
        public void ThrowingForwarder_IsLogged_AndDoesNotPropagate()
        {
            LogAssert.Expect(LogType.Exception, new Regex("forward-boom"));

            var hub = new NetworkEntityLifecycle(new ThrowingPublisher<NetworkEntitySpawned>(), null);
            Assert.DoesNotThrow(() => hub.PublishSpawned(Spawned("uuid-a")));
        }

        [Test]
        public void SubscribingDuringDispatch_TakesEffectFromTheNextEvent()
        {
            var hub = new NetworkEntityLifecycle();
            var lateHeard = new List<string>();
            IDisposable late = null;

            using var outer = hub.Subscribe((NetworkEntitySpawned e) =>
            {
                late ??= hub.Subscribe((NetworkEntitySpawned inner) => lateHeard.Add(inner.EntityId));
            });

            hub.PublishSpawned(Spawned("uuid-a"));
            hub.PublishSpawned(Spawned("uuid-b"));

            CollectionAssert.AreEqual(new[] { "uuid-b" }, lateHeard, "not the event that was in flight when it subscribed");
            late?.Dispose();
        }

        [Test]
        public void DisposingDuringDispatch_DoesNotDisturbTheOtherHandlersOfThatEvent()
        {
            var hub = new NetworkEntityLifecycle();
            var order = new List<string>();
            IDisposable self = null;

            self = hub.Subscribe((NetworkEntitySpawned e) =>
            {
                order.Add("self");
                self.Dispose();
            });
            using var other = hub.Subscribe((NetworkEntitySpawned e) => order.Add("other"));

            hub.PublishSpawned(Spawned("uuid-a"));
            hub.PublishSpawned(Spawned("uuid-b"));

            CollectionAssert.AreEqual(new[] { "self", "other", "other" }, order);
        }

        [Test]
        public void Clear_DropsEveryHandler_WithoutPublishing()
        {
            var hub = new NetworkEntityLifecycle();
            var heard = 0;
            hub.Subscribe((NetworkEntitySpawned e) => heard++);
            hub.Subscribe((NetworkEntityDespawned e) => heard++);
            Assert.AreEqual(2, hub.SubscriberCount);

            hub.Clear();

            Assert.AreEqual(0, hub.SubscriberCount);
            hub.PublishSpawned(Spawned("uuid-a"));
            Assert.AreEqual(0, heard);
        }

        [Test]
        public void TheEvents_CarryEntityWithVersion_AndTheReason()
        {
            var spawned = Spawned("uuid-a");
            Assert.AreEqual(7, spawned.Entity.Index);
            Assert.AreEqual(3, spawned.Entity.Version);

            var despawned = Despawned("uuid-a", NetworkDespawnReason.Teardown);
            Assert.AreEqual(spawned.Entity, despawned.Entity);
            Assert.AreEqual(NetworkDespawnReason.Teardown, despawned.Reason);

            // The 0.27.x constructors still compile and carry Entity.Null.
            Assert.AreEqual(Entity.Null, new NetworkEntitySpawned("uuid-a", "player", true).Entity);
            Assert.AreEqual(Entity.Null, new NetworkEntityDespawned("uuid-a", "player").Entity);
        }

        [Test]
        public void SubscribeNull_Throws()
        {
            var hub = new NetworkEntityLifecycle();
            Assert.Throws<ArgumentNullException>(() => hub.Subscribe((Action<NetworkEntitySpawned>)null));
            Assert.Throws<ArgumentNullException>(() => hub.Subscribe((Action<NetworkEntityDespawned>)null));
        }

        private sealed class ThrowingPublisher<T> : IDotsPublisher<T>
        {
            public void Publish(T message) => throw new InvalidOperationException("forward-boom");
        }
    }
}
