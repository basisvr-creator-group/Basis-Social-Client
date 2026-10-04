using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialConnectionTests
    {
        private const string Authorization = "{\"deviceCode\":\"secret-device\",\"userCode\":\"ABCD-EFGH\",\"verificationUri\":\"https://beeba.example/connect\",\"expiresIn\":600,\"interval\":1}";
        private const string Session = "{\"accessToken\":\"access\",\"refreshToken\":\"refresh\",\"user\":{\"id\":\"alice\",\"username\":\"alice\"}}";

        [TestCase("http://social.example")]
        [TestCase("http://127.0.0.1:8080")]
        [TestCase("https://social.example/api")]
        [TestCase("https://social.example?token=secret")]
        [TestCase("https://social.example#fragment")]
        [TestCase("https://secret@social.example")]
        public void ProductionEndpointRejectsUnsafeOrAmbiguousOrigins(string endpoint)
        {
            Assert.Throws<ArgumentException>(() => BasisSocialRuntime.NormalizeBaseUrl(endpoint));
            Assert.Throws<ArgumentException>(() => new BasisSocialUnityWebRequestTransport(endpoint));
        }

        [Test]
        public void DevelopmentModeOnlyAllowsLoopbackHttp()
        {
            Assert.That(BasisSocialRuntime.NormalizeBaseUrl("http://127.0.0.1:18089/", true), Is.EqualTo("http://127.0.0.1:18089"));
            Assert.That(BasisSocialRuntime.NormalizeBaseUrl("http://[::1]:18089/", true), Is.EqualTo("http://[::1]:18089"));
            Assert.Throws<ArgumentException>(() => BasisSocialRuntime.NormalizeBaseUrl("http://192.168.1.1", true));
        }

        [Test]
        public async Task CancelledApprovalNeverInstallsDelayedSession()
        {
            var completion = new TaskCompletionSource<BasisSocialHttpResponse>();
            var called = new TaskCompletionSource<bool>();
            var client = new BasisSocialApiClient(new Transport(request =>
            {
                if (request.Path.EndsWith("/start")) return Task.FromResult(Response(201, Authorization));
                called.TrySetResult(true); return completion.Task;
            }));
            using var controller = new BasisSocialConnectionController(client);
            Task run = controller.StartAsync();
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.AwaitingApproval));
            Assert.That(controller.UserCode, Is.EqualTo("ABCD-EFGH"));
            Assert.That(controller.VerificationUri, Does.Not.Contain("secret-device"));
            await called.Task;
            controller.Cancel();
            completion.SetResult(Response(200, Session));
            await run;
            Assert.That(client.HasSession, Is.False);
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.Cancelled));
            Assert.That(controller.VerificationUri, Is.Null);
        }

        [Test]
        public async Task PendingWaitsForApprovalAndThenConnects()
        {
            int completeCalls = 0;
            var states = new List<BasisSocialConnectionState>();
            var client = new BasisSocialApiClient(new Transport(request => Task.FromResult(
                request.Path.EndsWith("/start") ? Response(201, Authorization) :
                ++completeCalls == 1 ? Response(400, "{\"error\":{\"code\":\"authorization_pending\"}}") : Response(200, Session))));
            using var controller = new BasisSocialConnectionController(client);
            controller.Changed += () => states.Add(controller.State);
            await controller.StartAsync();
            Assert.That(completeCalls, Is.EqualTo(2));
            Assert.That(states, Does.Contain(BasisSocialConnectionState.AwaitingApproval));
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.Connected));
            Assert.That(client.CurrentUser.username, Is.EqualTo("alice"));
            Assert.That(controller.UserCode, Is.Null);
        }

        [TestCase("access_denied", BasisSocialConnectionState.Denied)]
        [TestCase("expired_token", BasisSocialConnectionState.Expired)]
        [TestCase("identity_unavailable", BasisSocialConnectionState.Failed)]
        public async Task TerminalApprovalErrorsRemainDistinct(string code, BasisSocialConnectionState expected)
        {
            var client = new BasisSocialApiClient(new Transport(request => Task.FromResult(request.Path.EndsWith("/start")
                ? Response(201, Authorization) : Response(400, "{\"error\":{\"code\":\"" + code + "\"}}"))));
            using var controller = new BasisSocialConnectionController(client);
            await controller.StartAsync();
            Assert.That(controller.State, Is.EqualTo(expected));
            Assert.That(controller.Error.ErrorCode, Is.EqualTo(code));
            Assert.That(client.HasSession, Is.False);
        }

        [Test]
        public async Task SlowDownNeverPollsAgainAtOriginalInterval()
        {
            int calls = 0;
            var slowed = new TaskCompletionSource<bool>();
            var client = new BasisSocialApiClient(new Transport(request => Task.FromResult(request.Path.EndsWith("/start")
                ? Response(201, Authorization) : (++calls > 0 ? Response(400, "{\"error\":{\"code\":\"slow_down\"}}") : null))));
            using var controller = new BasisSocialConnectionController(client);
            controller.Changed += () => { if (controller.State == BasisSocialConnectionState.Slowed) slowed.TrySetResult(true); };
            Task run = controller.StartAsync();
            await slowed.Task;
            await Task.Delay(1200);
            Assert.That(calls, Is.EqualTo(1));
            controller.Cancel(); await run;
        }

        [Test]
        public async Task LifetimeExpiresWithoutSendingOutOfWindowPoll()
        {
            int completeCalls = 0;
            var client = new BasisSocialApiClient(new Transport(request =>
            {
                if (!request.Path.EndsWith("/start")) completeCalls++;
                return Task.FromResult(Response(201, Authorization.Replace("\"expiresIn\":600", "\"expiresIn\":1").Replace("\"interval\":1", "\"interval\":5")));
            }));
            using var controller = new BasisSocialConnectionController(client);
            await controller.StartAsync();
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.Expired));
            Assert.That(completeCalls, Is.Zero);
        }

        [Test]
        public async Task ProfilePatchOnlySendsSocialOwnedFields()
        {
            BasisSocialHttpRequest sent = null;
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(new Transport(request =>
            {
                sent = request;
                return Task.FromResult(Response(200, "{\"displayName\":\"Canonical\",\"bio\":\"New bio\",\"statusText\":\"Available\"}"));
            }), tokens);
            var profile = await client.UpdateProfileAsync("New bio", "Available");
            Assert.That(sent.Path, Is.EqualTo("/api/v1/me/profile"));
            Assert.That(sent.Method, Is.EqualTo(BasisSocialHttpMethod.Patch));
            Assert.That(sent.BodyJson, Does.Not.Contain("displayName").And.Not.Contain("avatarUrl"));
            Assert.That(profile.displayName, Is.EqualTo("Canonical"));
        }

        [Test]
        public async Task ReplacingAttemptCannotExposeOlderApprovalCode()
        {
            var delayed = new TaskCompletionSource<BasisSocialHttpResponse>();
            int calls = 0;
            var client = new BasisSocialApiClient(new Transport(_ => ++calls == 1 ? delayed.Task :
                Task.FromResult(Response(201, Authorization.Replace("ABCD-EFGH", "NEW-CODE")))));
            using var controller = new BasisSocialConnectionController(client);
            Task first = controller.StartAsync();
            Task second = controller.StartAsync();
            delayed.SetResult(Response(201, Authorization));
            await first;
            Assert.That(controller.UserCode, Is.EqualTo("NEW-CODE"));
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.AwaitingApproval));
            controller.Cancel(); await second;
        }

        [Test]
        public async Task ProfileResponseCannotUpdateANewerAccount()
        {
            var delayed = new TaskCompletionSource<BasisSocialHttpResponse>();
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("old-access", "old-refresh");
            var client = new BasisSocialApiClient(new Transport(request => request.Method == BasisSocialHttpMethod.Patch
                ? delayed.Task : Task.FromResult(Response(200, Session))), tokens);
            Task patch = client.UpdateProfileAsync("Old account bio", "");
            await client.LoginAsync("alice", "password");
            delayed.SetResult(Response(200, "{\"bio\":\"Old account bio\"}"));
            try { await patch; Assert.Fail("Old profile must be cancelled."); } catch (OperationCanceledException) { }
            Assert.That(client.CurrentUser.username, Is.EqualTo("alice"));
            Assert.That(client.CurrentUser.EffectiveProfile.bio, Is.Null);
        }

        [Test]
        public async Task UnexpectedTransportExceptionBecomesSafeFailedState()
        {
            var client = new BasisSocialApiClient(new Transport(_ => throw new InvalidOperationException("private-token-and-url")));
            using var controller = new BasisSocialConnectionController(client);
            await controller.StartAsync();
            Assert.That(controller.State, Is.EqualTo(BasisSocialConnectionState.Failed));
            Assert.That(controller.Error.ErrorCode, Is.EqualTo("transport_error"));
            Assert.That(controller.Error.ToString(), Does.Not.Contain("private-token-and-url"));
            Assert.That(controller.VerificationUri, Is.Null);
        }

        private static BasisSocialHttpResponse Response(long status, string body) => new() { StatusCode = status, Body = body };
        private sealed class Transport : IBasisSocialHttpTransport
        {
            private readonly Func<BasisSocialHttpRequest, Task<BasisSocialHttpResponse>> send;
            public Transport(Func<BasisSocialHttpRequest, Task<BasisSocialHttpResponse>> send) => this.send = send;
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default) => send(request);
        }
    }
}
