using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Basis.Social.UI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialProgressTests
    {
        private sealed class QueuedContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<SendOrPostCallback> callbacks = new ConcurrentQueue<SendOrPostCallback>();
            public int Count => callbacks.Count;
            public override void Post(SendOrPostCallback callback, object state) => callbacks.Enqueue(callback);
            public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(null); }
        }

        [Test]
        public async Task WorkerProgressRunsOnUnityContextAndCanUseUnityObjects()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int callbackThread = 0;
            var delivered = new TaskCompletionSource<bool>();
            using var dispatcher = new BasisSocialProgressDispatcher(SynchronizationContext.Current, value =>
            {
                GameObject probe = null;
                try
                {
                    callbackThread = Thread.CurrentThread.ManagedThreadId;
                    probe = new GameObject("Social progress thread probe");
                    delivered.SetResult(probe.transform != null);
                }
                catch (System.Exception error) { delivered.SetException(error); }
                finally { if (probe != null) Object.DestroyImmediate(probe); }
            }, CancellationToken.None);
            await Task.Run(() => dispatcher.Report(42));
            Assert.That(await Task.WhenAny(delivered.Task, Task.Delay(5000)), Is.SameAs(delivered.Task));
            Assert.That(await delivered.Task, Is.True);
            Assert.That(callbackThread, Is.EqualTo(mainThread));
        }

        [Test]
        public async Task WorkerBurstCoalescesAndRejectsNonFiniteProgress()
        {
            var context = new QueuedContext();
            int calls = 0;
            float received = -1;
            using var dispatcher = new BasisSocialProgressDispatcher(context, value => { calls++; received = value; }, CancellationToken.None);
            await Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) dispatcher.Report(i);
                dispatcher.Report(float.NaN);
                dispatcher.Report(float.PositiveInfinity);
                dispatcher.Report(float.NegativeInfinity);
            });
            Assert.That(context.Count, Is.EqualTo(1));
            Assert.That(calls, Is.Zero);
            context.Drain();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(received, Is.EqualTo(100));
            dispatcher.Report(-10);
            context.Drain();
            Assert.That(received, Is.Zero);
        }

        [Test]
        public async Task CancellationDropsQueuedAndFutureWorkerProgress()
        {
            var context = new QueuedContext();
            using var cancellation = new CancellationTokenSource();
            int calls = 0;
            using var dispatcher = new BasisSocialProgressDispatcher(context, ignored => calls++, cancellation.Token);
            await Task.Run(() => dispatcher.Report(20));
            cancellation.Cancel();
            context.Drain();
            await Task.Run(() => dispatcher.Report(70));
            Assert.That(calls, Is.Zero);
            Assert.That(context.Count, Is.Zero);
        }

        [Test]
        public async Task CompletedLoadCannotOverwriteSuccessOrNextLoad()
        {
            var context = new QueuedContext();
            float status = 100;
            var oldLoad = new BasisSocialProgressDispatcher(context, value => status = value, CancellationToken.None);
            await Task.Run(() => oldLoad.Report(20));
            oldLoad.Dispose();
            using var newLoad = new BasisSocialProgressDispatcher(context, value => status = value, CancellationToken.None);
            await Task.Run(() => { oldLoad.Report(50); newLoad.Report(80); });
            context.Drain();
            Assert.That(status, Is.EqualTo(80));
            newLoad.Report(90);
            newLoad.Dispose();
            status = 100;
            context.Drain();
            Assert.That(status, Is.EqualTo(100));
        }
    }
}
