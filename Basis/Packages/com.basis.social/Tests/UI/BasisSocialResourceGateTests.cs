using Basis.Network.Core;
using Basis.Scripts.Networking;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using UnityEngine.SceneManagement;
using static SerializableBasis;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialResourceGateTests
    {
        [Test]
        public void QueuedPayloadCannotCrossManagedSessionOrReconnect()
        {
            long before = BasisNetworkResourceGate.Capture();
            Assert.That(BasisNetworkResourceGate.Allows(before), Is.True);
            int notices = 0;
            long managed;
            using (BasisNetworkResourceGate.BeginVerifiedSession(() => notices++))
            {
                managed = BasisNetworkResourceGate.Capture();
                Assert.That(BasisNetworkResourceGate.Allows(before), Is.False);
                Assert.That(BasisNetworkResourceGate.Allows(managed), Is.False);
                Assert.That(BasisNetworkResourceGate.Allows(managed), Is.False);
                Assert.That(notices, Is.EqualTo(1));
            }
            Assert.That(BasisNetworkResourceGate.Allows(managed), Is.False);
            Assert.That(BasisNetworkResourceGate.Allows(before), Is.False);
            Assert.That(BasisNetworkResourceGate.Allows(BasisNetworkResourceGate.Capture()), Is.True);
        }

        [Test]
        public void SilentLibraryRejectionDoesNotConsumeTheResourceWarning()
        {
            long legacy = BasisNetworkResourceGate.Capture();
            Assert.That(BasisNetworkResourceGate.Allows(legacy, notifyRejected: false), Is.True);
            int notices = 0;
            using (BasisNetworkResourceGate.BeginVerifiedSession(() => notices++))
            {
                long current = BasisNetworkResourceGate.Capture();
                Assert.That(BasisNetworkResourceGate.Allows(legacy, notifyRejected: false), Is.False);
                Assert.That(BasisNetworkResourceGate.Allows(current, notifyRejected: false), Is.False);
                Assert.That(BasisNetworkResourceGate.Allows(current, notifyRejected: false), Is.False);
                Assert.That(notices, Is.Zero);
                Assert.That(BasisNetworkResourceGate.Allows(current), Is.False);
                Assert.That(BasisNetworkResourceGate.Allows(current), Is.False);
                Assert.That(notices, Is.EqualTo(1));
            }
        }

        [Test]
        public void OldScopeCannotReleaseNewSessionAndResetInvalidatesQueuedWork()
        {
            var first = BasisNetworkResourceGate.BeginVerifiedSession(null);
            using var second = BasisNetworkResourceGate.BeginVerifiedSession(null);
            long pending = BasisNetworkResourceGate.Capture();
            first.Dispose();
            Assert.That(BasisNetworkResourceGate.Allows(pending), Is.False);
            BasisNetworkResourceGate.InvalidatePending();
            Assert.That(BasisNetworkResourceGate.Allows(pending), Is.False);
            Assert.That(BasisNetworkResourceGate.Allows(BasisNetworkResourceGate.Capture()), Is.False);
        }

        [Test]
        public async Task SceneCompletionKeepsTheOriginalCancellationTokenAfterReset()
        {
            using var original = new CancellationTokenSource();
            using var replacement = new CancellationTokenSource();
            var completion = new TaskCompletionSource<Scene>();
            var pending = LoadSceneForSession(token =>
            {
                Assert.That(token, Is.EqualTo(original.Token));
                return completion.Task;
            }, original.Token, BasisNetworkResourceGate.Capture());
            original.Cancel();
            completion.SetResult(default);
            try { await pending; Assert.Fail("A cancelled old request must not commit with the replacement token."); }
            catch (OperationCanceledException) { }
            Assert.That(replacement.IsCancellationRequested, Is.False);
        }

        [Test]
        public async Task LateLegacySceneCannotCommitOrUnloadAnExistingSceneInVerifiedSession()
        {
            var existing = SceneManager.GetActiveScene();
            Assert.That(existing.IsValid() && existing.isLoaded, Is.True);
            var completion = new TaskCompletionSource<Scene>();
            var pending = LoadSceneForSession(_ => completion.Task, CancellationToken.None, BasisNetworkResourceGate.Capture());
            using var verified = BasisNetworkResourceGate.BeginVerifiedSession(null);
            completion.SetResult(existing);
            try { await pending; Assert.Fail("A scene from an old session must not commit."); }
            catch (OperationCanceledException) { }
            Assert.That(existing.isLoaded, Is.True, "A previously loaded world is not owned by this request.");
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(existing));
        }

        private static Task<Scene> LoadSceneForSession(Func<CancellationToken, Task<Scene>> loader, CancellationToken token, long generation)
        {
            var method = typeof(BasisNetworkSpawnItem).GetMethod("LoadSceneForSessionAsync", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (Task<Scene>)method.Invoke(null, new object[] { loader, token, generation });
        }

        [Test]
        public async Task ImmediateAndDelayedResourcesAreRejectedBeforeParsingDownloadingOrUnloading()
        {
            using var scope = BasisNetworkResourceGate.BeginVerifiedSession(null);
            // A null packet would throw if dispatch reached deserialization. No network or scene loader is touched.
            await BasisNetworkGenericMessages.LoadResourceMessage(null, DeliveryMethod.ReliableOrdered);
            await BasisNetworkGenericMessages.UnloadResourceMessage(null, DeliveryMethod.ReliableOrdered);
            await BasisNetworkGenericMessages.SpawnPreloadedMessage(null, DeliveryMethod.ReliableOrdered);
            await BasisNetworkPreloadManager.HandlePredownload(default);
            await BasisNetworkPreloadManager.HandleSynchronizedPreload(default);
            await BasisNetworkPreloadManager.HandleSpawnPreloaded(default);
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => BasisNetworkSpawnItem.SpawnScene(default));
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => BasisNetworkSpawnItem.SpawnGameObject(default, BundledContentHolder.Selector.Prop));
            Assert.That(BasisNetworkSpawnItem.RequestSceneLoad("password", "https://untrusted.example/world.bee", false, false, out _), Is.False);
        }
    }
}
