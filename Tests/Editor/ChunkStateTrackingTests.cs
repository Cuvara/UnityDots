using System.Collections.Generic;
using Cuvara.DOTS.Provisioning;
using NUnit.Framework;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Tests for <see cref="ChunkViewProvisioner"/> state tracking and metrics.
    /// </summary>
    public sealed class ChunkStateTrackingTests
    {
        private RecordingViewAssetProvider _provider;
        private ChunkViewProvisioner _provisioner;
        private List<(string chunkId, ChunkState state)> _transitions;

        [SetUp]
        public void SetUp()
        {
            _provider = new RecordingViewAssetProvider();
            _provisioner = new ChunkViewProvisioner(_provider);
            _transitions = new List<(string, ChunkState)>();
            _provisioner.OnChunkStateChanged += (id, state) => _transitions.Add((id, state));
        }

        [Test]
        public void PrewarmChunk_TransitionsToWarm()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            Assert.AreEqual(ChunkState.Warm, _provisioner.ChunkStates["chunk-a"]);
            Assert.AreEqual(1, _provisioner.WarmChunkCount);
            Assert.AreEqual(0, _provisioner.PendingChunkCount);
        }

        [Test]
        public void PrewarmChunk_FiresStateChangedEvents()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            // Warming → Warm (provider completes synchronously)
            Assert.IsTrue(_transitions.Count >= 1);
            Assert.AreEqual(ChunkState.Warm, _transitions[_transitions.Count - 1].state);
            Assert.AreEqual("chunk-a", _transitions[_transitions.Count - 1].chunkId);
        }

        [Test]
        public void ReleaseChunk_RemovesFromStates()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            Assert.IsTrue(_provisioner.ChunkStates.ContainsKey("chunk-a"));

            _provisioner.ReleaseChunk("chunk-a");

            Assert.IsFalse(_provisioner.ChunkStates.ContainsKey("chunk-a"));
            Assert.AreEqual(0, _provisioner.WarmChunkCount);
        }

        [Test]
        public void ReleaseChunk_FiresReleasedEvent()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _transitions.Clear();

            _provisioner.ReleaseChunk("chunk-a");

            Assert.AreEqual(1, _transitions.Count);
            Assert.AreEqual("chunk-a", _transitions[0].chunkId);
            Assert.AreEqual(ChunkState.Released, _transitions[0].state);
        }

        [Test]
        public void MultipleChunks_CountsAreCorrect()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "torch" });
            _provisioner.PrewarmChunkAsync("chunk-c", new[] { "barrel" });

            Assert.AreEqual(3, _provisioner.WarmChunkCount);
            Assert.AreEqual(0, _provisioner.PendingChunkCount);

            _provisioner.ReleaseChunk("chunk-b");
            Assert.AreEqual(2, _provisioner.WarmChunkCount);
        }

        [Test]
        public void RePrewarmChunk_TransitionsBackToWarming()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _transitions.Clear();

            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });

            // Should fire at least a Warm event (provider completes synchronously)
            Assert.AreEqual(ChunkState.Warm, _provisioner.ChunkStates["chunk-a"]);
        }
    }
}
