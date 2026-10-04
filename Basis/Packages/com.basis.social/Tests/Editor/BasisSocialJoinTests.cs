using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialJoinTests
    {
        private const string Id = "88888888-8888-4888-8888-888888888888";
        private const string Did = "did:key:z6MkeTGwHmLmuCmgg4ABYhzWVh6ZX7hTwWt8gguAretUfc9c";
        private const string Ticket = "bvr_jt_0123456789012345678901234567890123456789012";

        [Test]
        public async Task TicketRequestBindsClientKeyAndDefaultsToPrivatePresence()
        {
            var transport = new Transport();
            var result = await Client(transport).IssueJoinTicketAsync(Id, Did);
            Assert.That(result.ticket, Is.EqualTo(Ticket));
            Assert.That(transport.Request.Path, Is.EqualTo("/api/v1/instances/" + Id + "/join-tickets"));
            Assert.That(transport.Request.BodyJson, Does.Contain("\"clientDid\":\"" + Did + "\""));
            Assert.That(transport.Request.BodyJson, Does.Contain("\"presenceVisibility\":\"nobody\""));
            Assert.That(transport.Request.BodyJson, Does.Contain("\"showExactInstance\":false"));
            Assert.That(transport.Request.AccessToken, Is.EqualTo("access"));
        }

        [TestCase("77777777-7777-4777-8777-777777777777", "2100-01-01T00:00:00Z")]
        [TestCase(Id, "2000-01-01T00:00:00Z")]
        [TestCase(Id, "not a date")]
        public async Task TicketResponsesCannotSubstituteTargetOrReturnExpiredCredentials(string responseId, string expiry)
        {
            var transport = new Transport { Id = responseId, Expiry = expiry };
            try { await Client(transport).IssueJoinTicketAsync(Id, Did); Assert.Fail("Unsafe ticket accepted."); }
            catch (BasisSocialApiException error) { Assert.That(error.ErrorCode, Is.EqualTo("invalid_response")); }
        }

        [Test]
        public async Task InvalidIdentityFailsBeforeHttp()
        {
            var transport = new Transport();
            try { await Client(transport).IssueJoinTicketAsync(Id, "not-a-key"); Assert.Fail("Invalid identity accepted."); }
            catch (ArgumentException) { }
            Assert.That(transport.Request, Is.Null);
        }

        private static BasisSocialApiClient Client(Transport transport)
        {
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            return new BasisSocialApiClient(transport, tokens);
        }
        private sealed class Transport : IBasisSocialHttpTransport
        {
            public string Id = BasisSocialJoinTests.Id;
            public string Expiry = "2100-01-01T00:00:00Z";
            public BasisSocialHttpRequest Request;
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default)
            {
                Request = request;
                return Task.FromResult(new BasisSocialHttpResponse { StatusCode = 201, Body = "{\"ticket\":\"" + Ticket + "\",\"expiresAt\":\"" + Expiry + "\",\"instance\":{\"id\":\"" + Id + "\"}}" });
            }
        }
    }
}
