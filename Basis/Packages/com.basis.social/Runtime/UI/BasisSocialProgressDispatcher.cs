using System;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("Basis.Social.UI.Tests")]

namespace Basis.Social.UI
{
    /// <summary>At most one queued UI update per load; queued work expires with that load.</summary>
    internal sealed class BasisSocialProgressDispatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly SynchronizationContext context;
        private readonly Action<float> callback;
        private readonly CancellationToken cancellationToken;
        private readonly SendOrPostCallback deliver;
        private bool queued, disposed;
        private float latest;

        public BasisSocialProgressDispatcher(SynchronizationContext context, Action<float> callback, CancellationToken cancellationToken)
        {
            this.context = context ?? throw new InvalidOperationException("Avatar loading must start on the Unity main thread.");
            this.callback = callback;
            this.cancellationToken = cancellationToken;
            deliver = Deliver;
        }

        public void Report(float value)
        {
            if (callback == null || float.IsNaN(value) || float.IsInfinity(value)) return;
            lock (gate)
            {
                if (disposed || cancellationToken.IsCancellationRequested) return;
                latest = Math.Max(0f, Math.Min(100f, value));
                if (queued) return;
                queued = true;
                context.Post(deliver, null);
            }
        }

        private void Deliver(object ignored)
        {
            lock (gate)
            {
                queued = false;
                if (disposed || cancellationToken.IsCancellationRequested) return;
                callback(latest);
            }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
        }
    }
}
