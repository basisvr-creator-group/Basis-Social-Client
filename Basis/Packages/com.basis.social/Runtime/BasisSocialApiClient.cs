using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social
{
    public sealed partial class BasisSocialApiClient
    {
        private const string LoginPath = "/api/v1/auth/login";
        private const string RefreshPath = "/api/v1/auth/refresh";
        private const string MePath = "/api/v1/me";
        private readonly IBasisSocialHttpTransport transport;
        private readonly IBasisSocialTokenStore tokenStore;
        private readonly SemaphoreSlim refreshLock = new(1, 1);
        private readonly object sessionGate = new();
        private long generation;

        public BasisSocialApiClient(IBasisSocialHttpTransport transport, IBasisSocialTokenStore tokenStore = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.tokenStore = tokenStore ?? new BasisSocialMemoryTokenStore();
        }

        public BasisSocialUser CurrentUser { get; private set; }
        public bool HasSession { get { lock (sessionGate) return !string.IsNullOrEmpty(tokenStore.AccessToken) || !string.IsNullOrEmpty(tokenStore.RefreshToken); } }
        public event Action<BasisSocialUser> SessionChanged;

        public async Task<BasisSocialUser> LoginAsync(string login, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(login)) throw new ArgumentException("Login is required.", nameof(login));
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required.", nameof(password));
            return await AuthenticateAsync(LoginPath, JsonUtility.ToJson(new BasisSocialLoginRequest { login = login.Trim(), password = password }), cancellationToken);
        }

        /// <summary>Start an explicit browser approval. Set linkCurrentAccount only when the user requested linking their signed-in Social account.</summary>
        public async Task<BasisSocialBeeBaAuthorization> StartBeeBaAsync(string codeChallenge, bool linkCurrentAccount = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(codeChallenge)) throw new ArgumentException("S256 challenge is required.", nameof(codeChallenge));
            long epoch = SnapshotGeneration();
            string body = JsonUtility.ToJson(new BasisSocialBeeBaStartRequest { codeChallenge = codeChallenge });
            BasisSocialHttpResponse response = linkCurrentAccount
                ? await SendAuthorizedAsync(BasisSocialHttpMethod.Post, "/api/v1/auth/beeba/start", body, true, cancellationToken)
                : await transport.SendAsync(new BasisSocialHttpRequest { Method = BasisSocialHttpMethod.Post, Path = "/api/v1/auth/beeba/start", BodyJson = body }, cancellationToken);
            AssertCurrent(epoch);
            EnsureSuccess(response);
            var result = Deserialize<BasisSocialBeeBaAuthorization>(response.Body, response.StatusCode);
            if (string.IsNullOrEmpty(result.deviceCode) || string.IsNullOrEmpty(result.userCode) ||
                !Uri.TryCreate(result.verificationUri, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || result.expiresIn <= 0 || result.interval <= 0)
                throw new BasisSocialApiException(0, "invalid_response", "Invalid browser authorization response.");
            return result;
        }

        /// <summary>Complete only after the user's browser approval; pending/denied/expired errors remain typed. No automatic polling or browser approval.</summary>
        public Task<BasisSocialUser> CompleteBeeBaAsync(string deviceCode, string codeVerifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceCode)) throw new ArgumentException("Device code is required.", nameof(deviceCode));
            if (string.IsNullOrWhiteSpace(codeVerifier)) throw new ArgumentException("Code verifier is required.", nameof(codeVerifier));
            return AuthenticateAsync("/api/v1/auth/beeba/complete", JsonUtility.ToJson(new BasisSocialBeeBaCompleteRequest { deviceCode = deviceCode, codeVerifier = codeVerifier }), cancellationToken);
        }

        private async Task<BasisSocialUser> AuthenticateAsync(string path, string body, CancellationToken cancellationToken)
        {
            long epoch;
            lock (sessionGate) epoch = ++generation;
            var response = await transport.SendAsync(new BasisSocialHttpRequest { Method = BasisSocialHttpMethod.Post, Path = path, BodyJson = body }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AssertCurrent(epoch);
            var session = ReadSession(response);
            ApplySession(session, epoch, true);
            return session.user;
        }

        public async Task<BasisSocialUser> RestoreSessionAsync(CancellationToken cancellationToken = default)
        {
            if (!HasSession) return null;
            return await GetMeAsync(cancellationToken);
        }

        public async Task<BasisSocialUser> GetMeAsync(CancellationToken cancellationToken = default)
        {
            long epoch = SnapshotGeneration();
            var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Get, MePath, null, true, cancellationToken);
            EnsureSuccess(response);
            var user = Deserialize<BasisSocialUser>(response.Body, response.StatusCode);
            lock (sessionGate) { AssertCurrent(epoch); CurrentUser = user; }
            SessionChanged?.Invoke(user);
            return user;
        }

        /// <summary>Edits Social-owned text only. Display name and image remain managed by BeeBa.</summary>
        public async Task<BasisSocialProfile> UpdateProfileAsync(string bio, string statusText, CancellationToken cancellationToken = default)
        {
            if (bio == null) throw new ArgumentNullException(nameof(bio));
            if (statusText == null) throw new ArgumentNullException(nameof(statusText));
            long epoch = SnapshotGeneration();
            var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Patch, "/api/v1/me/profile",
                JsonUtility.ToJson(new BasisSocialProfileTextUpdate { bio = bio, statusText = statusText }), true, cancellationToken);
            EnsureSuccess(response);
            var profile = Deserialize<BasisSocialProfile>(response.Body, response.StatusCode);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sessionGate)
            {
                AssertCurrent(epoch);
                if (CurrentUser != null) CurrentUser.profile = profile;
            }
            SessionChanged?.Invoke(CurrentUser);
            return profile;
        }

        public async Task<BasisSocialDeviceSession[]> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Get, "/api/v1/auth/sessions", null, true, cancellationToken);
            EnsureSuccess(response);
            return Deserialize<BasisSocialSessionList>(response.Body, response.StatusCode).items ?? Array.Empty<BasisSocialDeviceSession>();
        }

        public async Task RevokeSessionAsync(BasisSocialDeviceSession session, CancellationToken cancellationToken = default)
        {
            if (session == null || !Guid.TryParse(session.id, out _)) throw new ArgumentException("A valid device session is required.", nameof(session));
            long epoch = SnapshotGeneration();
            var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Delete, "/api/v1/auth/sessions/" + Uri.EscapeDataString(session.id), null, true, cancellationToken);
            EnsureSuccess(response, 204);
            if (session.current) ClearSessionIfCurrent(epoch);
        }

        public Task LogoutCurrentAsync(CancellationToken cancellationToken = default) => LogoutAtAsync("/api/v1/auth/logout-current", false, cancellationToken);
        public Task LogoutAllAsync(CancellationToken cancellationToken = default) => LogoutAtAsync("/api/v1/auth/logout-all", false, cancellationToken);
        // Preserve the legacy all-device endpoint and local-clear-on-error behavior for existing callers.
        public Task LogoutAsync(CancellationToken cancellationToken = default) => LogoutAtAsync("/api/v1/auth/logout", true, cancellationToken);

        private async Task LogoutAtAsync(string path, bool clearOnFailure, CancellationToken cancellationToken)
        {
            long epoch = SnapshotGeneration();
            try
            {
                if (HasSession)
                {
                    var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Post, path, null, true, cancellationToken);
                    EnsureSuccess(response, 204);
                }
                ClearSessionIfCurrent(epoch);
            }
            finally { if (clearOnFailure) ClearSessionIfCurrent(epoch); }
        }

        public Task RefreshSessionAsync(CancellationToken cancellationToken = default)
        {
            long epoch; string refreshToken;
            lock (sessionGate) { epoch = generation; refreshToken = tokenStore.RefreshToken; }
            return RefreshSessionAsync(epoch, refreshToken, cancellationToken);
        }

        private async Task RefreshSessionAsync(long epoch, string observedRefreshToken, CancellationToken cancellationToken)
        {
            await refreshLock.WaitAsync(cancellationToken);
            try
            {
                lock (sessionGate)
                {
                    AssertCurrent(epoch);
                    // Another request already rotated this token; share its result.
                    if (tokenStore.RefreshToken != observedRefreshToken) return;
                }
                if (string.IsNullOrEmpty(observedRefreshToken))
                {
                    ClearSessionIfCurrent(epoch);
                    throw new BasisSocialApiException(401, "missing_refresh_token", "No refresh token is available.");
                }
                BasisSocialHttpResponse response;
                try
                {
                    response = await transport.SendAsync(new BasisSocialHttpRequest
                    {
                        Method = BasisSocialHttpMethod.Post, Path = RefreshPath,
                        BodyJson = JsonUtility.ToJson(new BasisSocialRefreshRequest { refreshToken = observedRefreshToken })
                    }, cancellationToken);
                }
                catch
                {
                    // A lost rotation response cannot safely reuse the consumed refresh token.
                    ClearSessionIfCurrent(epoch);
                    throw;
                }
                AssertCurrent(epoch);
                if (response == null || response.StatusCode == 401 || response.StatusCode == 0 || !string.IsNullOrEmpty(response.TransportError))
                    ClearSessionIfCurrent(epoch);
                BasisSocialAuthSession session;
                try { session = ReadSession(response); }
                catch { if (response?.IsSuccess == true) ClearSessionIfCurrent(epoch); throw; }
                try { ApplySession(session, epoch, false); }
                catch { ClearSessionIfCurrent(epoch); throw; }
            }
            finally { refreshLock.Release(); }
        }

        public void ClearSession()
        {
            lock (sessionGate) { generation++; tokenStore.Clear(); CurrentUser = null; }
            SessionChanged?.Invoke(null);
        }

        private void ClearSessionIfCurrent(long epoch)
        {
            lock (sessionGate)
            {
                if (generation != epoch) return;
                generation++; tokenStore.Clear(); CurrentUser = null;
            }
            SessionChanged?.Invoke(null);
        }

        private long SnapshotGeneration() { lock (sessionGate) return generation; }
        private void AssertCurrent(long epoch)
        {
            lock (sessionGate) if (generation != epoch) throw new OperationCanceledException("The authentication session changed.");
        }

        private async Task<BasisSocialHttpResponse> SendAuthorizedAsync(BasisSocialHttpMethod method, string path, string bodyJson, bool retryAfterRefresh, CancellationToken cancellationToken)
        {
            long epoch;
            string accessToken, refreshToken;
            lock (sessionGate) { epoch = generation; accessToken = tokenStore.AccessToken; refreshToken = tokenStore.RefreshToken; }
            if (string.IsNullOrEmpty(accessToken))
            {
                if (string.IsNullOrEmpty(refreshToken)) throw new BasisSocialApiException(401, "unauthorized", "Authentication is required.");
                await RefreshSessionAsync(epoch, refreshToken, cancellationToken);
                lock (sessionGate) { AssertCurrent(epoch); accessToken = tokenStore.AccessToken; }
            }
            var response = await transport.SendAsync(new BasisSocialHttpRequest { Method = method, Path = path, BodyJson = bodyJson, AccessToken = accessToken }, cancellationToken);
            AssertCurrent(epoch);
            if (response.StatusCode == 401 && retryAfterRefresh)
            {
                bool mustRefresh;
                lock (sessionGate) { mustRefresh = tokenStore.AccessToken == accessToken; refreshToken = tokenStore.RefreshToken; }
                if (!string.IsNullOrEmpty(refreshToken))
                {
                    if (mustRefresh) await RefreshSessionAsync(epoch, refreshToken, cancellationToken);
                    AssertCurrent(epoch);
                    return await SendAuthorizedAsync(method, path, bodyJson, false, cancellationToken);
                }
            }
            return response;
        }

        private void ApplySession(BasisSocialAuthSession session, long epoch, bool newLogin)
        {
            if (session == null || string.IsNullOrEmpty(session.accessToken) || string.IsNullOrEmpty(session.refreshToken) || session.user == null)
                throw new BasisSocialApiException(0, "invalid_response", "Basis Social returned an incomplete auth session.");
            lock (sessionGate)
            {
                AssertCurrent(epoch);
                tokenStore.Save(session.accessToken, session.refreshToken);
                CurrentUser = session.user;
                if (newLogin) generation++;
            }
            SessionChanged?.Invoke(session.user);
        }

        private static BasisSocialAuthSession ReadSession(BasisSocialHttpResponse response)
        {
            EnsureSuccess(response);
            return Deserialize<BasisSocialAuthSession>(response.Body, response.StatusCode);
        }

        private static T Deserialize<T>(string json, long statusCode)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new BasisSocialApiException(statusCode, "invalid_response", "Basis Social returned an empty response.");

            try
            {
                T value = JsonUtility.FromJson<T>(json);
                if (value == null) throw new InvalidOperationException("JSON result was null.");
                return value;
            }
            catch (Exception exception) when (exception is not BasisSocialApiException)
            {
                throw new BasisSocialApiException(statusCode, "invalid_response", "Basis Social returned malformed JSON.");
            }
        }

        private static void EnsureSuccess(BasisSocialHttpResponse response, long expectedStatus = 0)
        {
            if (response == null)
                throw new BasisSocialApiException(0, "transport_error", "Basis Social did not return a response.");

            if (!string.IsNullOrEmpty(response.TransportError))
                throw new BasisSocialApiException(0, "transport_error", response.TransportError);

            if (response.IsSuccess && (expectedStatus == 0 || response.StatusCode == expectedStatus)) return;

            string code = "http_" + response.StatusCode;
            string message = "Basis Social request failed with HTTP " + response.StatusCode + ".";
            if (!string.IsNullOrWhiteSpace(response.Body))
            {
                try
                {
                    BasisSocialErrorEnvelope envelope = JsonUtility.FromJson<BasisSocialErrorEnvelope>(response.Body);
                    if (envelope?.error != null)
                    {
                        if (!string.IsNullOrWhiteSpace(envelope.error.code)) code = envelope.error.code;
                        if (!string.IsNullOrWhiteSpace(envelope.error.message)) message = envelope.error.message;
                    }
                }
                catch
                {
                    // Keep the status-based error without exposing an untrusted response body.
                }
            }

            throw new BasisSocialApiException(response.StatusCode, code, message);
        }
    }
}
