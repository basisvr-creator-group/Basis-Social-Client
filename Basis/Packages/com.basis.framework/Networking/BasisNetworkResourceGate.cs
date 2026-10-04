using System;

namespace Basis.Scripts.Networking
{
    /// <summary>Scopes raw resource delivery to a connection generation. Verified-world sessions load through their catalog owner.</summary>
    public static class BasisNetworkResourceGate
    {
        private static readonly object Sync = new object();
        private static long generation;
        private static Lease active;
        public static long Capture() { lock (Sync) return generation; }

        public static IDisposable BeginVerifiedSession(Action rejected)
        {
            lock (Sync)
            {
                generation++;
                active = new Lease(rejected);
                return active;
            }
        }

        public static void InvalidatePending() { lock (Sync) generation++; }

        public static bool Allows(long capturedGeneration, bool notifyRejected = true)
        {
            Action notify = null;
            lock (Sync)
            {
                if (capturedGeneration != generation) return false;
                if (active == null) return true;
                if (notifyRejected && !active.Notified) { active.Notified = true; notify = active.Rejected; }
            }
            notify?.Invoke();
            return false;
        }

        private sealed class Lease : IDisposable
        {
            internal readonly Action Rejected;
            internal bool Notified;
            internal Lease(Action rejected) { Rejected = rejected; }
            public void Dispose()
            {
                lock (Sync)
                {
                    if (!ReferenceEquals(active, this)) return;
                    active = null;
                    generation++;
                }
            }
        }
    }
}
