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
