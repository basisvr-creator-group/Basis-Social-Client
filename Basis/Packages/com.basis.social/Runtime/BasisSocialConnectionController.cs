using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Social
{
    public enum BasisSocialConnectionState
    {
        Idle, Starting, AwaitingApproval, Slowed, Connected, Denied, Expired, Cancelled, Failed
    }

    /// <summary>
    /// Owns one in-memory browser-approval attempt. UI observes state and explicitly opens
    /// VerificationUri; device credentials never leave this controller except to the SDK.
    /// Calls and Changed callbacks use the caller's synchronization context (Unity main thread).
    /// </summary>
    public sealed class BasisSocialConnectionController : IDisposable
    {
        private readonly BasisSocialApiClient client;
        private readonly bool allowLoopbackHttp;
        private CancellationTokenSource attempt;
        private int generation;
        private bool disposed;

        public BasisSocialConnectionController(BasisSocialApiClient client, bool allowLoopbackHttp = false)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.allowLoopbackHttp = allowLoopbackHttp;
        }

        public BasisSocialConnectionState State { get; private set; }
        public string UserCode { get; private set; }
        public string VerificationUri { get; private set; }
        public DateTimeOffset? ExpiresAt { get; private set; }
        public BasisSocialApiException Error { get; private set; }
        public event Action Changed;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(BasisSocialConnectionController));
            CancelAttempt();
            int current = ++generation;
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt = source;
            var token = source.Token;
            ClearPresentation();
            SetState(BasisSocialConnectionState.Starting);
            try
            {
                token.ThrowIfCancellationRequested();
                var proof = BasisSocialDeviceProof.Create();
                // Lifetime begins conservatively before the start request, including its latency.
                var elapsed = Stopwatch.StartNew();
                var started = DateTimeOffset.UtcNow;
                var authorization = await client.StartBeeBaAsync(proof.CodeChallenge, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (current != generation) return;
                var uri = new Uri(authorization.verificationUri, UriKind.Absolute);
                if ((uri.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query) ||
                    authorization.expiresIn > 86400 || authorization.interval > 86400)
                    throw new BasisSocialApiException(0, "invalid_response", "Invalid browser authorization response.");
                UserCode = authorization.userCode;
                VerificationUri = authorization.verificationUri;
                ExpiresAt = started.AddSeconds(authorization.expiresIn);
                double interval = authorization.interval;
                SetState(BasisSocialConnectionState.AwaitingApproval);
                while (current == generation)
                {
                    double remaining = authorization.expiresIn - elapsed.Elapsed.TotalSeconds;
                    if (remaining <= 0) { SetState(BasisSocialConnectionState.Expired); return; }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, remaining)), token);
                    if (elapsed.Elapsed.TotalSeconds >= authorization.expiresIn)
                    { SetState(BasisSocialConnectionState.Expired); return; }
                    try
                    {
                        await client.CompleteBeeBaAsync(authorization.deviceCode, proof.CodeVerifier, token);
                        token.ThrowIfCancellationRequested();
                        if (current == generation) SetState(BasisSocialConnectionState.Connected);
                        return;
                    }
                    catch (BasisSocialApiException error) when (error.ErrorCode == "authorization_pending")
                    {
                        SetState(BasisSocialConnectionState.AwaitingApproval);
                    }
                    catch (BasisSocialApiException error) when (error.ErrorCode == "slow_down" || error.StatusCode == 429)
                    {
                        // RFC 8628: retain at least five extra seconds for all subsequent polls.
                        interval = Math.Min(86400, interval + 5);
                        SetState(BasisSocialConnectionState.Slowed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (current == generation) SetState(BasisSocialConnectionState.Cancelled);
            }
            catch (BasisSocialApiException error)
            {
                if (current != generation) return;
                Error = error;
                SetState(error.ErrorCode switch
                {
                    "access_denied" => BasisSocialConnectionState.Denied,
                    "expired_token" or "invalid_grant" => BasisSocialConnectionState.Expired,
                    _ => BasisSocialConnectionState.Failed
                });
            }
            catch (Exception)
            {
                if (current != generation) return;
                // Transport implementations can throw before producing a typed HTTP result.
                // Preserve a recoverable state without exposing URLs, bodies or credentials.
                Error = new BasisSocialApiException(0, "transport_error", "Unable to complete the connection request.");
                SetState(BasisSocialConnectionState.Failed);
            }
            finally
            {
                if (current == generation)
                {
                    attempt = null;
                    // The public approval code and URL are useful only during a live attempt.
                    UserCode = null;
                    VerificationUri = null;
                    ExpiresAt = null;
                    Changed?.Invoke();
                }
            }
        }

        public void Cancel()
        {
            bool active = attempt != null;
            CancelAttempt();
            if (active) { ClearPresentation(); SetState(BasisSocialConnectionState.Cancelled); }
        }

        private void CancelAttempt()
        {
            generation++;
            attempt?.Cancel();
            attempt = null;
        }

        private void ClearPresentation()
        {
            UserCode = null;
            VerificationUri = null;
            ExpiresAt = null;
            Error = null;
        }

        private void SetState(BasisSocialConnectionState state) { State = state; Changed?.Invoke(); }
        public void Dispose() { if (disposed) return; Cancel(); disposed = true; Changed = null; }
    }
}
