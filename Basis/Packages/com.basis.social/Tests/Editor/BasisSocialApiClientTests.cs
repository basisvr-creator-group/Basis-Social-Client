using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialApiClientTests
    {
        [Test]
        public async Task LoginStoresTokensAndReturnsCurrentUser()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200,
                "{\"accessToken\":\"access-1\",\"refreshToken\":\"refresh-1\",\"user\":{\"id\":\"user-1\",\"username\":\"alice\",\"profile\":{\"displayName\":\"Alice\"}}}");
            var tokens = new BasisSocialMemoryTokenStore();
            var client = new BasisSocialApiClient(transport, tokens);

            BasisSocialUser user = await client.LoginAsync("alice", "correct horse battery staple");

            Assert.That(user.username, Is.EqualTo("alice"));
            Assert.That(user.EffectiveProfile.displayName, Is.EqualTo("Alice"));
            Assert.That(tokens.AccessToken, Is.EqualTo("access-1"));
            Assert.That(tokens.RefreshToken, Is.EqualTo("refresh-1"));
            Assert.That(transport.Requests[0].Path, Is.EqualTo("/api/v1/auth/login"));
            Assert.That(transport.Requests[0].AccessToken, Is.Null);
        }

        [Test]
        public async Task RestoreRefreshesExpiredAccessTokenAndRetriesMe()
        {
            var transport = new FakeTransport();
            transport.Enqueue(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"expired\"}}");
            transport.Enqueue(200,
                "{\"accessToken\":\"access-2\",\"refreshToken\":\"refresh-2\",\"user\":{\"username\":\"alice\"}}");
            transport.Enqueue(200, "{\"id\":\"user-1\",\"username\":\"alice\"}");
            var tokens = new BasisSocialMemoryTokenStore();
            tokens.Save("expired-access", "refresh-1");
            var client = new BasisSocialApiClient(transport, tokens);

            BasisSocialUser user = await client.RestoreSessionAsync();

            Assert.That(user.username, Is.EqualTo("alice"));
            Assert.That(tokens.AccessToken, Is.EqualTo("access-2"));
            Assert.That(tokens.RefreshToken, Is.EqualTo("refresh-2"));
            Assert.That(transport.Requests.ConvertAll(request => request.Path), Is.EqualTo(new[]
            {
                "/api/v1/me",
                "/api/v1/auth/refresh",
                "/api/v1/me"
            }));
            Assert.That(transport.Requests[2].AccessToken, Is.EqualTo("access-2"));
        }

        [Test]
        public void ApiErrorsExposeCodeWithoutLeakingTheRawBody()
        {
            var transport = new FakeTransport();
            transport.Enqueue(401,
                "{\"error\":{\"code\":\"invalid_credentials\",\"message\":\"invalid login or password\"},\"secret\":\"must-not-escape\"}");
            var client = new BasisSocialApiClient(transport);

            BasisSocialApiException exception = Assert.ThrowsAsync<BasisSocialApiException>(async () =>
                await client.LoginAsync("alice", "wrong password"));

            Assert.That(exception.StatusCode, Is.EqualTo(401));
            Assert.That(exception.ErrorCode, Is.EqualTo("invalid_credentials"));
            Assert.That(exception.Message, Is.EqualTo("invalid login or password"));
            Assert.That(exception.ToString(), Does.Not.Contain("must-not-escape"));
        }

        [Test]
        public void LogoutAlwaysClearsLocalCredentials()
        {
            var transport = new FakeTransport();
            transport.Enqueue(503, "{\"error\":{\"code\":\"unavailable\",\"message\":\"try later\"}}");
            var tokens = new BasisSocialMemoryTokenStore();
            tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);

            Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.LogoutAsync());

            Assert.That(tokens.AccessToken, Is.Null);
            Assert.That(tokens.RefreshToken, Is.Null);
            Assert.That(client.HasSession, Is.False);
        }

        [Test]
        public void RuntimeNormalizesServiceUrl()
        {
            Assert.That(
                BasisSocialRuntime.NormalizeBaseUrl("  https://social.example/  "),
                Is.EqualTo("https://social.example"));
        }

        [Test]
        public void RuntimeRejectsServiceUrlWithEmbeddedCredentials()
        {
            Assert.Throws<ArgumentException>(() =>
                BasisSocialRuntime.NormalizeBaseUrl("https://token@social.example"));
        }

        [Test]
        public async Task BeeBaStartIsAnonymousUnlessLinkExplicitlyRequested()
        {
            var transport = new FakeTransport();
            const string response = "{\"deviceCode\":\"device-secret\",\"userCode\":\"ABCD-EFGH\",\"verificationUri\":\"https://beeba.example/connect\",\"expiresIn\":600,\"interval\":5}";
            transport.Enqueue(201, response); transport.Enqueue(201, response);
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            var proof = BasisSocialDeviceProof.Create();
            var result = await client.StartBeeBaAsync(proof.CodeChallenge);
            await client.StartBeeBaAsync(proof.CodeChallenge, true);
            Assert.That(result.userCode, Is.EqualTo("ABCD-EFGH"));
            Assert.That(result.interval, Is.EqualTo(5));
            Assert.That(transport.Requests[0].AccessToken, Is.Null);
            Assert.That(transport.Requests[1].AccessToken, Is.EqualTo("access"));
            Assert.That(transport.Requests[0].BodyJson, Does.Contain("codeChallenge"));
            Assert.That(transport.Requests[0].BodyJson, Does.Not.Contain(proof.CodeVerifier));
        }

        [Test]
        public async Task BeeBaCompleteStoresOnlySocialSessionAndPendingDoesNotClearExistingSession()
        {
            var transport = new FakeTransport();
            transport.Enqueue(400, "{\"error\":{\"code\":\"authorization_pending\",\"message\":\"pending\"}}");
            transport.Enqueue(200, SessionJson("linked"));
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("old", "old-refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            var error = Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.CompleteBeeBaAsync("device", "verifier"));
            Assert.That(error.ErrorCode, Is.EqualTo("authorization_pending"));
            Assert.That(tokens.AccessToken, Is.EqualTo("old"));
            await client.CompleteBeeBaAsync("device", "verifier");
            Assert.That(tokens.AccessToken, Is.EqualTo("linked-access"));
            Assert.That(transport.Requests[1].AccessToken, Is.Null);
            Assert.That(transport.Requests[1].BodyJson, Does.Contain("codeVerifier"));
        }

        [Test]
        public void DeviceProofUsesFreshS256Base64UrlValues()
        {
            var one = BasisSocialDeviceProof.Create(); var two = BasisSocialDeviceProof.Create();
            Assert.That(one.CodeVerifier.Length, Is.EqualTo(43));
            Assert.That(one.CodeChallenge.Length, Is.EqualTo(43));
            Assert.That(one.CodeVerifier, Is.Not.EqualTo(two.CodeVerifier));
            using var sha = System.Security.Cryptography.SHA256.Create();
            string expected = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(one.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.That(one.CodeChallenge, Is.EqualTo(expected));
        }

        [Test]
        public async Task DeviceListAndRevocationUseCurrentFlagWithoutDecodingTokens()
        {
            var transport = new FakeTransport();
            const string id = "3114bf3d-b3e6-484f-a195-3d446ab814cd";
            transport.Enqueue(200, "{\"items\":[{\"id\":\"" + id + "\",\"current\":true,\"createdAt\":\"2026-01-01T00:00:00Z\",\"expiresAt\":\"2026-02-01T00:00:00Z\"}]}");
            transport.Enqueue(204, "");
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            var sessions = await client.ListSessionsAsync();
            Assert.That(sessions.Length, Is.EqualTo(1)); Assert.That(sessions[0].current, Is.True);
            await client.RevokeSessionAsync(sessions[0]);
            Assert.That(transport.Requests[1].Method, Is.EqualTo(BasisSocialHttpMethod.Delete));
            Assert.That(transport.Requests[1].Path, Is.EqualTo("/api/v1/auth/sessions/" + id));
            Assert.That(client.HasSession, Is.False);
        }

        [Test]
        public async Task ExplicitLogoutFailureKeepsSessionAndSuccessfulAllClears()
        {
            var transport = new FakeTransport(); transport.Enqueue(503, "{}"); transport.Enqueue(204, "");
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.LogoutCurrentAsync());
            Assert.That(client.HasSession, Is.True);
            await client.LogoutAllAsync();
            Assert.That(client.HasSession, Is.False);
            Assert.That(transport.Requests[0].Path, Is.EqualTo("/api/v1/auth/logout-current"));
            Assert.That(transport.Requests[1].Path, Is.EqualTo("/api/v1/auth/logout-all"));
        }

        [Test]
        public async Task OldRefreshCannotOverwriteNewLoginOrClearItOnUnauthorized()
        {
            foreach (long status in new long[] { 200, 401 })
            {
                var oldResponse = new TaskCompletionSource<BasisSocialHttpResponse>();
                var transport = new DelegateTransport(request => request.Path.EndsWith("refresh")
                    ? oldResponse.Task : Task.FromResult(Response(200, SessionJson("new"))));
                var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("old-access", "old-refresh");
                var client = new BasisSocialApiClient(transport, tokens);
                Task refresh = client.RefreshSessionAsync();
                await client.LoginAsync("new", "password");
                oldResponse.SetResult(Response(status, status == 200 ? SessionJson("old") : "{}"));
                try { await refresh; Assert.Fail("Stale refresh must be cancelled."); } catch (OperationCanceledException) { }
                Assert.That(tokens.AccessToken, Is.EqualTo("new-access"));
                Assert.That(client.CurrentUser.username, Is.EqualTo("new"));
            }
        }

        [Test]
        public async Task DelayedMeAndLogoutCannotMutateNewAccount()
        {
            foreach (bool logout in new[] { false, true })
            {
                var oldResponse = new TaskCompletionSource<BasisSocialHttpResponse>();
                var transport = new DelegateTransport(request => request.Path.EndsWith("login")
                    ? Task.FromResult(Response(200, SessionJson("new"))) : oldResponse.Task);
                var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("old-access", "old-refresh");
                var client = new BasisSocialApiClient(transport, tokens);
                Task pending = logout ? client.LogoutAsync() : client.GetMeAsync();
                await client.LoginAsync("new", "password");
                oldResponse.SetResult(Response(logout ? 204 : 200, logout ? "" : "{\"username\":\"old\"}"));
                try { await pending; Assert.Fail("Stale operation must be cancelled."); } catch (OperationCanceledException) { }
                Assert.That(tokens.AccessToken, Is.EqualTo("new-access"));
                Assert.That(client.CurrentUser.username, Is.EqualTo("new"));
            }
        }

        [Test]
        public async Task ConcurrentRefreshCallsRotateOnlyOnce()
        {
            var response = new TaskCompletionSource<BasisSocialHttpResponse>(); int calls = 0;
            var transport = new DelegateTransport(_ => { calls++; return response.Task; });
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            Task first = client.RefreshSessionAsync(); Task second = client.RefreshSessionAsync();
            Assert.That(calls, Is.EqualTo(1));
            response.SetResult(Response(200, SessionJson("rotated")));
            await Task.WhenAll(first, second);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(tokens.RefreshToken, Is.EqualTo("rotated-refresh"));
        }

        [Test]
        public void LostRefreshResponseDiscardsConsumedTokenButServerOutageDoesNot()
        {
            foreach (long status in new long[] { 0, 503 })
            {
                var transport = new FakeTransport(); transport.Enqueue(status, "{}");
                var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
                var client = new BasisSocialApiClient(transport, tokens);
                Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.RefreshSessionAsync());
                Assert.That(client.HasSession, Is.EqualTo(status == 503));
            }
        }

        [Test]
        public async Task OlderLoginAndCompletionAfterClearCannotRestoreSession()
        {
            var delayed = new TaskCompletionSource<BasisSocialHttpResponse>(); int calls = 0;
            var transport = new DelegateTransport(_ => ++calls == 1 ? delayed.Task : Task.FromResult(Response(200, SessionJson("new"))));
            var tokens = new BasisSocialMemoryTokenStore(); var client = new BasisSocialApiClient(transport, tokens);
            Task old = client.LoginAsync("old", "password");
            await client.LoginAsync("new", "password");
            delayed.SetResult(Response(200, SessionJson("old")));
            try { await old; Assert.Fail("Old login must be cancelled."); } catch (OperationCanceledException) { }
            Assert.That(tokens.AccessToken, Is.EqualTo("new-access"));

            delayed = new TaskCompletionSource<BasisSocialHttpResponse>();
            transport = new DelegateTransport(_ => delayed.Task); client = new BasisSocialApiClient(transport, tokens);
            Task completing = client.CompleteBeeBaAsync("device", "verifier"); client.ClearSession();
            delayed.SetResult(Response(200, SessionJson("late")));
            try { await completing; Assert.Fail("Cleared authorization must be cancelled."); } catch (OperationCanceledException) { }
            Assert.That(client.HasSession, Is.False);
        }

        [Test]
        public void IncompleteRotationResponseCannotReuseOldRefreshToken()
        {
            var transport = new FakeTransport(); transport.Enqueue(200, "{\"accessToken\":\"new\",\"user\":{\"username\":\"alice\"}}");
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("old", "old-refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.RefreshSessionAsync());
            Assert.That(client.HasSession, Is.False);
        }

        [Test]
        public void IdentityRecoveryCodesAreTypedAndPresentationNeverUsesRemoteText()
        {
            var cases = new[] {
                ("authorization_pending", BasisSocialRecoveryAction.WaitForApproval),
                ("slow_down", BasisSocialRecoveryAction.SlowPolling),
                ("access_denied", BasisSocialRecoveryAction.StartNewAuthorization),
                ("expired_token", BasisSocialRecoveryAction.StartNewAuthorization),
                ("invalid_grant", BasisSocialRecoveryAction.StartNewAuthorization),
                ("token_revoked", BasisSocialRecoveryAction.SignIn),
                ("identity_already_linked", BasisSocialRecoveryAction.ReviewLinkedAccounts),
                ("identity_unavailable", BasisSocialRecoveryAction.TryAgainLater)
            };
            foreach (var entry in cases)
            {
                var error = new BasisSocialApiException(400, entry.Item1, "remote-secret-message");
                Assert.That(error.RecoveryAction, Is.EqualTo(entry.Item2));
                Assert.That(error.GetUserMessage("en"), Does.Not.Contain("remote-secret-message"));
                Assert.That(error.GetUserMessage("ru"), Does.Not.Contain("remote-secret-message"));
                Assert.That(error.GetUserMessage("ru"), Is.Not.EqualTo(error.GetUserMessage("en")));
            }
        }

        [TestCase("player_not_ready", "Wait for your character", "Дождитесь появления персонажа")]
        [TestCase("avatar_load_failed", "Choose another avatar", "Выберите другой аватар")]
        public void AvatarErrorsGiveSafeActionableLocalizedMessages(string code, string englishAction, string russianAction)
        {
            var error = new BasisSocialApiException(409, code, "remote-secret-url");
            Assert.That(error.GetUserMessage("en"), Does.Contain(englishAction).And.Not.Contain("remote-secret-url"));
            Assert.That(error.GetUserMessage("ru"), Does.Contain(russianAction).And.Not.Contain("remote-secret-url"));
        }

        private static string SessionJson(string name) => "{\"accessToken\":\"" + name + "-access\",\"refreshToken\":\"" + name + "-refresh\",\"user\":{\"id\":\"" + name + "\",\"username\":\"" + name + "\"}}";
        private static BasisSocialHttpResponse Response(long status, string body) => new() { StatusCode = status, Body = body };
        private sealed class DelegateTransport : IBasisSocialHttpTransport
        {
            private readonly Func<BasisSocialHttpRequest, Task<BasisSocialHttpResponse>> send;
            public DelegateTransport(Func<BasisSocialHttpRequest, Task<BasisSocialHttpResponse>> send) { this.send = send; }
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default) => send(request);
        }

        private sealed class FakeTransport : IBasisSocialHttpTransport
        {
            private readonly Queue<BasisSocialHttpResponse> responses = new();
            public readonly List<BasisSocialHttpRequest> Requests = new();

            public void Enqueue(long statusCode, string body)
            {
                responses.Enqueue(new BasisSocialHttpResponse { StatusCode = statusCode, Body = body });
            }

            public Task<BasisSocialHttpResponse> SendAsync(
                BasisSocialHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (responses.Count == 0) throw new InvalidOperationException("No fake response was queued.");
                return Task.FromResult(responses.Dequeue());
            }
        }
    }
}
