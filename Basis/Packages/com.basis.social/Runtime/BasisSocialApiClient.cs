using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social
{
    public sealed class BasisSocialApiClient
    {
        private const string LoginPath = "/api/v1/auth/login";
        private const string RefreshPath = "/api/v1/auth/refresh";
        private const string LogoutPath = "/api/v1/auth/logout";
        private const string MePath = "/api/v1/me";

        private readonly IBasisSocialHttpTransport transport;
        private readonly IBasisSocialTokenStore tokenStore;
        private readonly SemaphoreSlim refreshLock = new(1, 1);

        public BasisSocialApiClient(
            IBasisSocialHttpTransport transport,
            IBasisSocialTokenStore tokenStore = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.tokenStore = tokenStore ?? new BasisSocialMemoryTokenStore();
        }

        public BasisSocialUser CurrentUser { get; private set; }
        public bool HasSession => !string.IsNullOrEmpty(tokenStore.AccessToken) ||
                                  !string.IsNullOrEmpty(tokenStore.RefreshToken);

        public event Action<BasisSocialUser> SessionChanged;

        public async Task<BasisSocialUser> LoginAsync(
            string login,
            string password,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(login)) throw new ArgumentException("Login is required.", nameof(login));
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required.", nameof(password));

            var payload = new BasisSocialLoginRequest { login = login.Trim(), password = password };
            BasisSocialHttpResponse response = await transport.SendAsync(new BasisSocialHttpRequest
            {
                Method = BasisSocialHttpMethod.Post,
                Path = LoginPath,
                BodyJson = JsonUtility.ToJson(payload)
            }, cancellationToken);

            BasisSocialAuthSession session = ReadSession(response);
            ApplySession(session);
            return CurrentUser;
        }

        public async Task<BasisSocialUser> RestoreSessionAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tokenStore.AccessToken))
            {
                if (string.IsNullOrEmpty(tokenStore.RefreshToken)) return null;
                await RefreshSessionAsync(cancellationToken);
            }

            return await GetMeAsync(cancellationToken);
        }

        public async Task<BasisSocialUser> GetMeAsync(CancellationToken cancellationToken = default)
        {
            BasisSocialHttpResponse response = await SendAuthorizedAsync(
                BasisSocialHttpMethod.Get,
                MePath,
                null,
                true,
                cancellationToken);

            EnsureSuccess(response);
            CurrentUser = Deserialize<BasisSocialUser>(response.Body, response.StatusCode);
            SessionChanged?.Invoke(CurrentUser);
            return CurrentUser;
        }

        public async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
        {
            await refreshLock.WaitAsync(cancellationToken);
            try
            {
                string refreshToken = tokenStore.RefreshToken;
                if (string.IsNullOrEmpty(refreshToken))
                {
                    ClearSession();
                    throw new BasisSocialApiException(401, "missing_refresh_token", "No refresh token is available.");
                }

                var payload = new BasisSocialRefreshRequest { refreshToken = refreshToken };
                BasisSocialHttpResponse response = await transport.SendAsync(new BasisSocialHttpRequest
                {
                    Method = BasisSocialHttpMethod.Post,
                    Path = RefreshPath,
                    BodyJson = JsonUtility.ToJson(payload)
                }, cancellationToken);

                if (response.StatusCode == 401)
                {
                    ClearSession();
                }

                BasisSocialAuthSession session = ReadSession(response);
                ApplySession(session);
            }
            finally
            {
                refreshLock.Release();
            }
        }

        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(tokenStore.AccessToken))
                {
                    BasisSocialHttpResponse response = await transport.SendAsync(new BasisSocialHttpRequest
                    {
                        Method = BasisSocialHttpMethod.Post,
                        Path = LogoutPath,
                        AccessToken = tokenStore.AccessToken
                    }, cancellationToken);
                    EnsureSuccess(response, 204);
                }
            }
            finally
            {
                ClearSession();
            }
        }

        public void ClearSession()
        {
            tokenStore.Clear();
            CurrentUser = null;
            SessionChanged?.Invoke(null);
        }

        private async Task<BasisSocialHttpResponse> SendAuthorizedAsync(
            BasisSocialHttpMethod method,
            string path,
            string bodyJson,
            bool retryAfterRefresh,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(tokenStore.AccessToken))
            {
                if (string.IsNullOrEmpty(tokenStore.RefreshToken))
                    throw new BasisSocialApiException(401, "unauthorized", "Authentication is required.");
                await RefreshSessionAsync(cancellationToken);
            }

            BasisSocialHttpResponse response = await transport.SendAsync(new BasisSocialHttpRequest
            {
                Method = method,
                Path = path,
                BodyJson = bodyJson,
                AccessToken = tokenStore.AccessToken
            }, cancellationToken);

            if (response.StatusCode == 401 && retryAfterRefresh && !string.IsNullOrEmpty(tokenStore.RefreshToken))
            {
                await RefreshSessionAsync(cancellationToken);
                return await SendAuthorizedAsync(method, path, bodyJson, false, cancellationToken);
            }

            return response;
        }

        private void ApplySession(BasisSocialAuthSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.accessToken) || string.IsNullOrEmpty(session.refreshToken))
            {
                throw new BasisSocialApiException(0, "invalid_response", "Basis Social returned an incomplete auth session.");
            }

            tokenStore.Save(session.accessToken, session.refreshToken);
            CurrentUser = session.user;
            SessionChanged?.Invoke(CurrentUser);
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
