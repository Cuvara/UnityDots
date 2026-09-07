using Cuvara.DOTS.Provisioning;
using NUnit.Framework;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Reference-count semantics of <see cref="ChunkViewProvisioner"/> — the part of the chunk API
    /// that is easy to get subtly wrong and impossible to notice until a prefab vanishes mid-play.
    /// </summary>
    public sealed class ChunkViewProvisionerTests
    {
        private RecordingViewAssetProvider _provider;
        private ChunkViewProvisioner _provisioner;

        [SetUp]
        public void SetUp()
        {
            _provider = new RecordingViewAssetProvider();
            _provisioner = new ChunkViewProvisioner(_provider, NullViewCascadeSink.Instance);
        }

        [Test]
        public void PrewarmChunk_WarmsEachKeyOnce()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" }, 4);

            Assert.AreEqual(2, _provider.Prewarmed.Count);
            Assert.AreEqual(4, _provider.WarmCounts["goblin"]);
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));
        }

        [Test]
        public void DuplicateKeysInOneChunk_CountAsOneReference()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "goblin", "goblin" });

            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));

            _provisioner.ReleaseChunk("chunk-a");

            // Counting occurrences instead of chunks would leave the count at 2 here and leak.
            Assert.AreEqual(0, _provisioner.GetReferenceCount("goblin"));
            CollectionAssert.Contains(_provider.Released, "goblin");
        }

        [Test]
        public void ReleasingOneChunk_KeepsKeyAnotherChunkStillUses()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin", "barrel" });

            _provisioner.ReleaseChunk("chunk-a");

            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"), "chunk-b still needs it");
            CollectionAssert.DoesNotContain(_provider.Released, "goblin");
            CollectionAssert.Contains(_provider.Released, "torch");

            _provisioner.ReleaseChunk("chunk-b");

            CollectionAssert.Contains(_provider.Released, "goblin");
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);
        }

        [Test]
        public void SharedKey_IsWarmedOnlyOnce()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" });

            Assert.AreEqual(1, _provider.Prewarmed.Count);
            Assert.AreEqual(2, _provisioner.GetReferenceCount("goblin"));
        }

        [Test]
        public void HigherCountRequest_GrowsTheWarmSet()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" }, 2);
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" }, 8);

            Assert.AreEqual(2, _provider.Prewarmed.Count, "second request grows the pool");
            Assert.AreEqual(8, _provider.WarmCounts["goblin"]);
        }

        [Test]
        public void ReleasingUnknownOrAlreadyReleasedChunk_IsANoOp()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" });

            var unknown = _provisioner.ReleaseChunk("never-warmed");
            Assert.IsFalse(unknown.Released);
            Assert.IsFalse(unknown.WasTracked, "an unknown chunk is a no-op, not a refusal");

            Assert.IsTrue(_provisioner.ReleaseChunk("chunk-a").Released);
            Assert.IsFalse(_provisioner.ReleaseChunk("chunk-a").Released, "second release must not decrement again");

            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));
            CollectionAssert.DoesNotContain(_provider.Released, "goblin");
        }

        [Test]
        public void RewarmingAChunk_DiffsInsteadOfReloadingSharedKeys()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            _provider.Prewarmed.Clear();

            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "barrel" });

            CollectionAssert.AreEquivalent(new[] { "barrel" }, _provider.Prewarmed, "goblin must not be reloaded");
            CollectionAssert.AreEquivalent(new[] { "torch" }, _provider.Released);
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));
            Assert.AreEqual(1, _provisioner.ChunkCount);
        }

        [Test]
        public void ReleaseAll_DropsEverything()
        {
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin", "torch" });

            _provisioner.ReleaseAll();

            Assert.AreEqual(0, _provisioner.ChunkCount);
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _provider.Released);
        }
    }
}
