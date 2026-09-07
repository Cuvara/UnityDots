using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cuvara.DOTS.Messaging;
using Cuvara.DOTS.Provisioning;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Cuvara.DOTS.Tests.Editor
{
    /// <summary>
    /// Cancellation, failure, epoch and ownership behaviour of <see cref="ChunkViewProvisioner"/>
    /// while loads are in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ManualViewAssetProvider"/> keeps every prewarm pending until the test settles it,
    /// so these can assert what happens to a chunk that is released, re-warmed, failed or cancelled
    /// <i>between</i> the call and the completion — the window the synchronous recording provider
    /// cannot open.
    /// </para>
    /// <para>
    /// The editor's <see cref="SynchronizationContext"/> would defer every await continuation to the
    /// next editor tick, which a synchronous test never reaches. <c>SetUp</c> removes it so
    /// continuations run inline on <c>Complete</c>/<c>Fault</c>/<c>Cancel</c>; <c>TearDown</c> puts
    /// it back. Everything still runs on the one test thread, so the provisioner's main-thread
    /// assertion holds.
    /// </para>
    /// </remarks>
    public sealed class ChunkProvisioningEpochTests
    {
        private sealed class RecordingCascadeSink : IViewCascadeSink
        {
            public readonly List<string> CascadedKeys = new List<string>();
            public readonly Dictionary<string, int> ViewsPerKey = new Dictionary<string, int>();

            public int CascadeDespawn(IReadOnlyCollection<string> keys)
            {
                var despawned = 0;
                foreach (var key in keys)
                {
                    CascadedKeys.Add(key);
                    if (ViewsPerKey.TryGetValue(key, out var count)) despawned += count;
                }

                return despawned;
            }
        }

        private sealed class CapturingPublisher<T> : IDotsPublisher<T>
        {
            public readonly List<T> Published = new List<T>();

            public void Publish(T message) => Published.Add(message);
        }

        private SynchronizationContext _savedContext;
        private ManualViewAssetProvider _provider;
        private RecordingCascadeSink _sink;
        private CapturingPublisher<ChunkWarmed> _warmed;
        private CapturingPublisher<ChunkReleased> _released;
        private ChunkViewProvisioner _provisioner;
        private List<(string chunkId, ChunkState state)> _transitions;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            _provider = new ManualViewAssetProvider();
            _sink = new RecordingCascadeSink();
            _warmed = new CapturingPublisher<ChunkWarmed>();
            _released = new CapturingPublisher<ChunkReleased>();
            _provisioner = new ChunkViewProvisioner(_provider, _sink, _warmed, _released);
            _transitions = new List<(string, ChunkState)>();
            _provisioner.OnChunkStateChanged += (id, state) => _transitions.Add((id, state));

            // A cascade logs on purpose.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void AssertBaseline()
        {
            Assert.AreEqual(0, _provisioner.ChunkCount);
            Assert.AreEqual(0, _provisioner.TrackedKeyCount);
            Assert.AreEqual(0, _provisioner.WarmChunkCount);
            Assert.AreEqual(0, _provisioner.PendingChunkCount);
            Assert.AreEqual(0, _provisioner.ChunkStates.Count);
            Assert.IsFalse(_provisioner.IsSessionPinned);
        }

        private static void Observe(Task task)
        {
            // Touch the exception so a faulted task is never "unobserved"; asserts elsewhere say what it was.
            if (task.IsFaulted) _ = task.Exception;
        }

        // ---- configuration ----

        [Test]
        public void Constructor_RejectsMissingCascadeSink()
        {
            Assert.Throws<ArgumentNullException>(() => new ChunkViewProvisioner(_provider, null));
        }

        [Test]
        public void PrewarmChunkAsync_RejectsTheReservedSessionId()
        {
            Assert.Throws<ArgumentException>(() => _provisioner.PrewarmChunkAsync(ChunkViewProvisioner.SessionId, new[] { "goblin" }));
            AssertBaseline();
        }

        [Test]
        public void PublicMembers_RejectCallsOffTheConstructingThread()
        {
            var ex = Assert.Throws<AggregateException>(() =>
                Task.Run(() => _provisioner.ReleaseChunk("chunk-a")).Wait());
            Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);

            var ex2 = Assert.Throws<AggregateException>(() =>
                Task.Run(() => _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" })).Wait());
            Assert.IsInstanceOf<InvalidOperationException>(ex2.InnerException);
            AssertBaseline();
        }

        // ---- cancellation ----

        [Test]
        public void PreCancelledWarm_TouchesNothing()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" }, 4, cts.Token);

            Assert.IsTrue(task.IsCanceled);
            CollectionAssert.IsEmpty(_provider.Prewarmed, "no work was started");
            CollectionAssert.IsEmpty(_transitions);
            AssertBaseline();
        }

        [Test]
        public void CancelAfterPartialCompletion_RollsBack_AndCountsReconcile()
        {
            using var cts = new CancellationTokenSource();
            var task = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" }, 2, cts.Token);
            Assert.AreEqual(1, _provider.Complete("goblin"), "half the chunk landed");
            Assert.IsFalse(task.IsCompleted);
            Assert.AreEqual(ChunkState.Warming, _provisioner.ChunkStates["chunk-a"]);

            cts.Cancel(); // the provider honours the token: torch cancels

            Assert.IsTrue(task.IsCanceled);
            CollectionAssert.Contains(_transitions, ("chunk-a", ChunkState.Failed));
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _provider.Released, "both references dropped");
            Assert.AreEqual(1, _released.Published.Count, "the rollback is observable as a release");
            CollectionAssert.IsEmpty(_warmed.Published, "never warm");
            AssertBaseline();
        }

        // ---- release / re-warm while warming ----

        [Test]
        public void ReleaseWhileWarming_StaleCompletionDoesNotResurrectTheChunk()
        {
            var task = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            Assert.AreEqual(ChunkState.Warming, _provisioner.ChunkStates["chunk-a"]);

            var release = _provisioner.ReleaseChunk("chunk-a");
            Assert.IsTrue(release.Released, "releasing a warming chunk is legal");
            CollectionAssert.Contains(_provider.Released, "goblin");
            _transitions.Clear();

            _provider.Complete("goblin"); // the obsolete load lands now

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status, "the awaiting caller is not failed for a race it lost");
            Assert.IsFalse(_provisioner.IsChunkTracked("chunk-a"));
            Assert.IsFalse(_provisioner.IsChunkLoaded("chunk-a"));
            CollectionAssert.IsEmpty(_transitions, "no state change from a stale completion");
            CollectionAssert.IsEmpty(_warmed.Published, "no ChunkWarmed for a chunk that is gone");
            AssertBaseline();
        }

        [Test]
        public void RewarmWhileWarming_OnlyTheNewEpochCanMarkWarm()
        {
            var first = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            var second = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "torch" });
            CollectionAssert.Contains(_provider.Released, "goblin", "the diff dropped goblin");
            Assert.AreEqual(1, _provisioner.GetReferenceCount("torch"));

            _provider.Complete("goblin"); // stale

            Assert.AreEqual(TaskStatus.RanToCompletion, first.Status);
            Assert.IsFalse(_provisioner.IsChunkLoaded("chunk-a"), "the old epoch must not mark the new set warm");
            Assert.AreEqual(ChunkState.Warming, _provisioner.ChunkStates["chunk-a"]);
            CollectionAssert.IsEmpty(_warmed.Published);

            _provider.Complete("torch");

            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status);
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));
            Assert.AreEqual(1, _warmed.Published.Count);
            Assert.AreEqual(1, _warmed.Published[0].KeyCount);
            Assert.AreEqual(1, _provisioner.ChunkCount);
            Assert.AreEqual(1, _provisioner.TrackedKeyCount);
        }

        [Test]
        public void ImmediateRewarmSameId_AfterRelease_IsAFreshEpoch()
        {
            var first = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provisioner.ReleaseChunk("chunk-a");
            var second = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });

            Assert.AreEqual(2, _provider.Prewarmed.Count, "released then re-requested: the load is re-issued");
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"), "exactly one reference, not two");

            _provider.Complete("goblin"); // settles both pending loads

            Assert.AreEqual(TaskStatus.RanToCompletion, first.Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status);
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));
            Assert.AreEqual(1, _warmed.Published.Count, "exactly one ChunkWarmed — the stale one is silent");
            Assert.AreEqual(1, _provisioner.WarmChunkCount);
        }

        // ---- failure and retry ----

        [Test]
        public void WarmFailure_RollsBackReferences_AndRetrySucceeds()
        {
            var task = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            _provider.Complete("goblin");

            _provider.Fault("torch");
            Observe(task);

            Assert.IsTrue(task.IsFaulted, "the caller learns about the failure");
            CollectionAssert.Contains(_transitions, ("chunk-a", ChunkState.Failed));
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _provider.Released);
            AssertBaseline();

            // Retry: plain call, same id. Both keys are re-issued because neither is warm any more.
            _provider.Prewarmed.Clear();
            var retry = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            CollectionAssert.AreEquivalent(new[] { "goblin", "torch" }, _provider.Prewarmed);

            _provider.CompleteAll();

            Assert.AreEqual(TaskStatus.RanToCompletion, retry.Status);
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));
            Assert.AreEqual(2, _provisioner.TrackedKeyCount);
        }

        [Test]
        public void WarmFailureOnAKeyAnotherChunkHolds_LeavesThatChunkAlone_ButReissuesTheLoadNextTime()
        {
            var b = _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" });
            _provider.Complete("goblin");
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-b"));

            var a = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            Assert.AreEqual(2, _provisioner.GetReferenceCount("goblin"));
            _provider.Fault("torch");
            Observe(a);

            Assert.IsTrue(a.IsFaulted);
            Assert.IsFalse(_provisioner.IsChunkTracked("chunk-a"));
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-b"), "the surviving chunk is untouched");
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"), "back to b's single reference");
            CollectionAssert.DoesNotContain(_provider.Released, "goblin", "b still holds it");
            CollectionAssert.Contains(_provider.Released, "torch", "the provider is told to drop whatever partial state the failed load left");
            Assert.AreEqual(1, _provisioner.TrackedKeyCount);

            // torch is not warm, so a new requester re-issues its load rather than trusting the count table.
            _provider.Prewarmed.Clear();
            var c = _provisioner.PrewarmChunkAsync("chunk-c", new[] { "torch" });
            CollectionAssert.AreEquivalent(new[] { "torch" }, _provider.Prewarmed);
            _provider.Complete("torch");
            Assert.AreEqual(TaskStatus.RanToCompletion, c.Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, b.Status);
        }

        [Test]
        public void FailureOfAnObsoleteEpoch_DoesNotRollBackTheNewerOne()
        {
            var first = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            var second = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"), "diff: goblin kept, not double-counted");

            _provider.Fault("goblin"); // the first epoch's load fails after it was superseded
            Observe(first);

            Assert.IsTrue(first.IsFaulted, "its own caller still hears about it");
            Assert.IsTrue(_provisioner.IsChunkTracked("chunk-a"), "but the newer epoch owns the chunk now");
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));
            Assert.AreEqual(1, _provisioner.GetReferenceCount("torch"));
            CollectionAssert.DoesNotContain(_transitions, ("chunk-a", ChunkState.Failed));

            _provider.Complete("torch");
            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status);
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));
        }

        // ---- shared keys and ownership ----

        [Test]
        public void TwoChunksSharingAKey_SurvivorRetainsIt_WhileTheOtherIsStillWarming()
        {
            var a = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            var b = _provisioner.PrewarmChunkAsync("chunk-b", new[] { "goblin" });
            _sink.ViewsPerKey["goblin"] = 3;
            _sink.ViewsPerKey["torch"] = 1;

            var result = _provisioner.ReleaseChunk("chunk-a");

            Assert.AreEqual(1, result.KeysReleased);
            Assert.AreEqual(1, result.ViewsDespawned, "only torch's view; goblin is not being released");
            CollectionAssert.AreEquivalent(new[] { "torch" }, _sink.CascadedKeys);
            CollectionAssert.AreEquivalent(new[] { "torch" }, _provider.Released);
            Assert.AreEqual(1, _provisioner.GetReferenceCount("goblin"));

            _provider.Complete("goblin");
            _provider.Complete("torch"); // stale for chunk-a

            Assert.AreEqual(TaskStatus.RanToCompletion, a.Status);
            Assert.AreEqual(TaskStatus.RanToCompletion, b.Status);
            Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-b"));
            Assert.IsFalse(_provisioner.IsChunkTracked("chunk-a"));
            Assert.AreEqual(1, _warmed.Published.Count);
            Assert.AreEqual("chunk-b", _warmed.Published[0].ChunkId);
        }

        [Test]
        public void SessionPin_OutlivesChunkReleases_AndReleaseAll()
        {
            var pin = _provisioner.PinSessionKeysAsync(new[] { "player", "goblin" });
            _provider.CompleteAll();
            Assert.AreEqual(TaskStatus.RanToCompletion, pin.Status);
            Assert.IsTrue(_provisioner.IsSessionPinned);
            Assert.AreEqual(0, _provisioner.ChunkCount, "the pin is not a chunk");

            var a = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" });
            Assert.AreEqual(2, _provisioner.GetReferenceCount("goblin"));
            _provider.CompleteAll();
            _sink.ViewsPerKey["goblin"] = 5;

            _provisioner.ReleaseChunk("chunk-a");
            CollectionAssert.AreEquivalent(new[] { "torch" }, _provider.Released, "goblin is session-owned");
            CollectionAssert.DoesNotContain(_sink.CascadedKeys, "goblin");

            Assert.AreEqual(0, _provisioner.ReleaseAll(), "scene teardown leaves the pin alone");
            Assert.IsTrue(_provisioner.IsSessionPinned);
            Assert.AreEqual(2, _provisioner.TrackedKeyCount);

            var result = _provisioner.ReleaseSessionKeys();
            Assert.IsTrue(result.Released);
            Assert.AreEqual(5, result.ViewsDespawned, "views come down before the last reference goes");
            CollectionAssert.AreEquivalent(new[] { "torch", "player", "goblin" }, _provider.Released);
            Assert.AreEqual(TaskStatus.RanToCompletion, a.Status);
            AssertBaseline();
        }

        [Test]
        public void ReleaseAll_IncludeSession_DropsEverything()
        {
            _provisioner.PinSessionKeysAsync(new[] { "player" });
            _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" });
            _provider.CompleteAll();

            _provisioner.ReleaseAll(includeSession: true);

            CollectionAssert.AreEquivalent(new[] { "goblin", "player" }, _provider.Released);
            AssertBaseline();
        }

        [Test]
        public void ReleaseChunk_WithTheSessionId_IsANoOp()
        {
            _provisioner.PinSessionKeysAsync(new[] { "player" });
            _provider.CompleteAll();

            var result = _provisioner.ReleaseChunk(ChunkViewProvisioner.SessionId);

            Assert.IsFalse(result.Released);
            Assert.IsFalse(result.WasTracked);
            Assert.IsTrue(_provisioner.IsSessionPinned);
        }

        // ---- repeatability ----

        [Test]
        public void RepeatedWarmAndRelease_ReturnsToBaselineCounts()
        {
            for (var i = 0; i < 10; i++)
            {
                var task = _provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin", "torch" }, 2);
                _provider.CompleteAll();
                Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
                Assert.IsTrue(_provisioner.IsChunkLoaded("chunk-a"));

                var result = _provisioner.ReleaseChunk("chunk-a");
                Assert.IsTrue(result.Released);
                Assert.AreEqual(2, result.KeysReleased);
            }

            Assert.AreEqual(20, _provider.Prewarmed.Count);
            Assert.AreEqual(20, _provider.Released.Count);
            Assert.AreEqual(0, _provider.PendingCount);
            Assert.AreEqual(10, _warmed.Published.Count);
            Assert.AreEqual(10, _released.Published.Count);
            AssertBaseline();
        }

        [Test]
        public void RepeatedWarmReleaseWhileWarming_ReturnsToBaseline_WithNoStaleEffects()
        {
            var tasks = new List<Task>();
            for (var i = 0; i < 10; i++)
            {
                tasks.Add(_provisioner.PrewarmChunkAsync("chunk-a", new[] { "goblin" }));
                _provisioner.ReleaseChunk("chunk-a"); // before the load lands
            }

            Assert.AreEqual(10, _provider.PendingCount);
            AssertBaseline();

            _provider.CompleteAll();

            foreach (var t in tasks) Assert.AreEqual(TaskStatus.RanToCompletion, t.Status);
            CollectionAssert.IsEmpty(_warmed.Published, "every completion was stale");
            Assert.AreEqual(10, _released.Published.Count);
            AssertBaseline();
        }
    }
}
