using System.Collections.Generic;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Provisioning;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// A chunk release must take its views down before releasing the assets they stand on, and must
    /// leave nothing pointing at what it released.
    /// </summary>
    /// <remarks>
    /// The regression these cover: reference counting tracks <i>chunks</i>, and a live view is held
    /// by an entity the provisioner has never heard of. Before the cascade, unloading a chunk while
    /// its entities were alive destroyed their pooled instances while the registry kept the handles
    /// and the entities kept an <c>EntityViewLink</c> that could never resolve — views silently gone,
    /// no error, no recovery. That is the ordinary streaming path, not an exotic case.
    /// <para>
    /// These exercise the provisioner half against a recording sink. The other half — that the sink
    /// really does route through the ordinary despawn path and clear the links — is covered by
    /// <c>EntityViewCascadeTests</c>, which needs a real World.
    /// </para>
    /// </remarks>
    public sealed class ChunkReleaseSafetyTests
    {
        /// <summary>Records what the provisioner asked to be cascaded, and in what order.</summary>
        private sealed class RecordingCascadeSink : IViewCascadeSink
        {
            public readonly List<string> CascadedKeys = new List<string>();
            public readonly Dictionary<string, int> ViewsPerKey = new Dictionary<string, int>();
            public int Calls;

            public int CascadeDespawn(IReadOnlyCollection<string> keys)
            {
                Calls++;
                var despawned = 0;
                foreach (var key in keys)
                {
                    CascadedKeys.Add(key);
                    if (ViewsPerKey.TryGetValue(key, out var count)) despawned += count;
                }

                return despawned;
            }
        }

        /// <summary>Captures published messages so the "not silent" requirement can be asserted.</summary>
        private sealed class CapturingPublisher<T> : IDotsPublisher<T>
        {
            public readonly List<T> Published = new List<T>();

            public void Publish(T message) => Published.Add(message);
        }

        private RecordingViewAssetProvider _provider;
        private RecordingCascadeSink _sink;
        private CapturingPublisher<ChunkCascadeReleased> _cascadePublisher;
        private ChunkViewProvisioner _provisioner;

        [SetUp]
        public void SetUp()
        {
            _provider = new RecordingViewAssetProvider();
            _sink = new RecordingCascadeSink();
            _cascadePublisher = new CapturingPublisher<ChunkCascadeReleased>();
            _provisioner = new ChunkViewProvisioner(_provider, _sink, cascadePublisher: _cascadePublisher);

            // A cascade logs on purpose; an expected log must not read as an unexpected one.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown() => LogAssert.ignoreFailingMessages = false;

        [Test]
        public void ReleasingAChunkWithLiveViews_CascadesThenReleases_AndPublishesTheCount()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            _sink.ViewsPerKey["goblin"] = 3;

            var result = _provisioner.ReleaseChunk("chunk-a");

            Assert.IsTrue(result.Released);
            Assert.IsTrue(result.WasTracked);
            Assert.AreEqual(3, result.ViewsDespawned);
            Assert.AreEqual(2, result.KeysReleased);

            // Both halves happened, and the assets did go.
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _sink.CascadedKeys);
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _provider.Released);
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);
            Assert.IsFalse(_provisioner.IsChunkTracked("chunk-a"));

            // Surviving without a view is invisible unless it is announced.
            Assert.AreEqual(1, _cascadePublisher.Published.Count);
            Assert.AreEqual("chunk-a", _cascadePublisher.Published[0].ChunkId);
            Assert.AreEqual(2, _cascadePublisher.Published[0].KeyCount);
            Assert.AreEqual(3, _cascadePublisher.Published[0].ViewsDespawned);
        }

        [Test]
        public void SharedKey_IsNeitherCascadedNorReleased_WhenAnotherChunkStillHoldsIt()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" });
            _sink.ViewsPerKey["goblin"] = 5;
            _sink.ViewsPerKey["torch"] = 2;

            var result = _provisioner.ReleaseChunk("chunk-a");

            // "goblin" is not being released, so its views are in no danger and must be left alone.
            CollectionAssert.AreEquivalent(new[] { "torch" }, _sink.CascadedKeys);
            CollectionAssert.AreEquivalent(new[] { "torch" }, _provider.Released);
            Assert.AreEqual(2, result.ViewsDespawned, "only torch's views");
            Assert.AreEqual(1, result.KeysReleased);
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));
        }

        [Test]
        public void ReleaseWithNoLiveViews_BehavesExactlyAsBefore()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            var result = _provisioner.ReleaseChunk("chunk-a");

            Assert.IsTrue(result.Released);
            Assert.AreEqual(0, result.ViewsDespawned);
            CollectionAssert.Contains(_provider.Released, "goblin");
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);

            // Nothing to announce when nothing was torn down.
            CollectionAssert.IsEmpty(_cascadePublisher.Published);
        }

        [Test]
        public void UnknownChunk_DoesNotCascade()
        {
            var result = _provisioner.ReleaseChunk("never-warmed");

            Assert.IsFalse(result.Released);
            Assert.IsFalse(result.WasTracked);
            Assert.AreEqual(0, _sink.Calls, "nothing to tear down for a chunk that never existed");
        }

        [Test]
        public void ReleaseAll_ReportsTotalViewsCascaded()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "torch" });
            _sink.ViewsPerKey["goblin"] = 2;
            _sink.ViewsPerKey["torch"] = 1;

            Assert.AreEqual(3, _provisioner.ReleaseAll());
            Assert.AreEqual(0, _provisioner.ChunkCount);
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);
        }

        [Test]
        public void WithoutACascadeSink_ConstructionIsRejected()
        {
            // With no sink the provisioner cannot reach the view layer at all, so a release would
            // strand every live view on the released keys. That is refused up front rather than
            // discovered at the first streaming unload.
            Assert.Throws<System.ArgumentNullException>(() => new ChunkViewProvisioner(_provider, null));
        }

        [Test]
        public void NullSink_IsTheExplicitOptOut_AndReleasesWithoutCascading()
        {
            // Where no view layer exists the opt-out is a named type a reviewer can grep for.
            var provisioner = new ChunkViewProvisioner(_provider, NullViewCascadeSink.Instance);
            provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            var result = provisioner.ReleaseChunk("chunk-a");

            Assert.IsTrue(result.Released);
            Assert.AreEqual(0, result.ViewsDespawned);
            CollectionAssert.Contains(_provider.Released, "goblin");
        }

        [Test]
        public void IsChunkTracked_IsNotAClaimAboutLoading()
        {
            // The rename exists because the old name (IsChunkWarm) reads as "loads finished", which
            // it never meant. The fake provider completes synchronously, so both are true after the
            // awaited prewarm; the distinction is in what each name promises.
            Assert.IsFalse(_provisioner.IsChunkTracked("chunk-a"));
            Assert.IsFalse(_provisioner.IsChunkLoaded("chunk-a"));

            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            Assert.IsTrue(_provisioner.IsChunkTracked("chunk-a"));
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));
        }
    }
}
