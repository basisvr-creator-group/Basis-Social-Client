using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialCommunityTests
    {
        private const string Id = "77777777-7777-4777-8777-777777777777";
        [Test]
        public async Task FriendPagesEncodeCursorAndPreserveCanonicalNames()
        {
            var transport = new Transport(); transport.Replies.Enqueue(new BasisSocialHttpResponse { StatusCode = 200, Body = "{\"data\":[{\"actorId\":\"" + Id + "\",\"displayName\":\"Друг\",\"acct\":\"friend@example.org\"}],\"pagination\":{\"nextCursor\":\"next\",\"limit\":12}}" });
            var result = await Client(transport).ListFriendsAsync("pending", "incoming", "a&state=accepted");
            Assert.That(result.data[0].DisplayName, Is.EqualTo("Друг"));
            Assert.That(result.pagination.nextCursor, Is.EqualTo("next"));
            Assert.That(transport.Requests[0].Path, Does.Contain("cursor=a%26state%3Daccepted"));
            Assert.That(transport.Requests[0].Path, Does.Contain("state=pending&direction=incoming"));
            Assert.That(transport.Requests[0].AccessToken, Is.EqualTo("access"));
        }
        [Test]
        public async Task RequestsAndInvitesOmitEmptyGuidFields()
        {
            var transport = new Transport();
            transport.Replies.Enqueue(new BasisSocialHttpResponse { StatusCode = 200, Body = "{}" });
            transport.Replies.Enqueue(new BasisSocialHttpResponse { StatusCode = 204, Body = "" });
            transport.Replies.Enqueue(new BasisSocialHttpResponse { StatusCode = 201, Body = "{\"id\":\"" + Id + "\"}" });
            var client = Client(transport);
            await client.FriendActionAsync("request", acct: "friend@example.org");
            await client.FriendActionAsync("remove", Id);
            await client.SendInviteAsync("friend@example.org", instanceId: Id);
            Assert.That(transport.Requests[0].BodyJson, Does.Not.Contain("targetActorId"));
            Assert.That(transport.Requests[1].BodyJson, Does.Not.Contain("targetAcct"));
            Assert.That(transport.Requests[2].BodyJson, Does.Not.Contain("worldId"));
            Assert.That(transport.Requests[2].BodyJson, Does.Contain("\"instanceId\":\"" + Id + "\""));
        }
        [Test]
        public void InvalidPagesAndTargetsFailClosed()
        {
            var transport = new Transport(); var client = Client(transport);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.ListWorldsAsync(limit: 51));
            Assert.Throws<ArgumentException>(() => client.ListInstancesAsync("https://untrusted.example/path"));
            Assert.Throws<ArgumentException>(() => client.ListFriendsAsync("all"));
            Assert.That(transport.Requests, Is.Empty);
        }
        [Test]
        public async Task SuccessfulMalformedPageDoesNotAppearEmpty()
        {
            var transport = new Transport(); transport.Replies.Enqueue(new BasisSocialHttpResponse { StatusCode = 200, Body = "{}" });
            try { await Client(transport).ListWorldsAsync(); Assert.Fail("Malformed page was accepted."); }
            catch (BasisSocialApiException error) { Assert.That(error.ErrorCode, Is.EqualTo("invalid_response")); }
        }
        [Test]
        public void SseHandlesUtf8ChunkBoundariesReplayAndResync()
        {
            var events = new List<BasisSocialRealtimeEvent>(); var parser = new BasisSocialEventParser(events.Add, "100-1");
            var data = Encoding.UTF8.GetBytes("data: {\"id\":\"old\",\"cursor\":\"100-1\",\"type\":\"friend.requested\"}\n\ndata: {\"id\":\"new\",\"cursor\":\"100-2\",\"type\":\"friend.accepted\",\"payload\":\"Друг\"}\r\n\r\ndata: {\"id\":\"new\",\"cursor\":\"100-2\",\"type\":\"friend.accepted\"}\n\ndata: {\"type\":\"realtime.resync_required\"}\n\n");
            foreach (byte value in data) parser.Feed(new[] { value }, 1);
            Assert.That(events.Count, Is.EqualTo(2)); Assert.That(events[0].type, Is.EqualTo("friend.accepted"));
            Assert.That(events[1].type, Is.EqualTo("realtime.resync_required")); Assert.That(parser.Cursor, Is.EqualTo("100-2"));
        }
        [Test]
        public void SseRejectsOversizedAndInvalidEvents()
        {
            var parser = new BasisSocialEventParser(_ => { }); var oversized = Encoding.UTF8.GetBytes(new string('a', 65537));
            Assert.Throws<FormatException>(() => parser.Feed(oversized, oversized.Length));
            var invalid = Encoding.UTF8.GetBytes("data: {\"type\":\"friend.accepted\",\"cursor\":\"header\\nattack\"}\n\n");
            Assert.Throws<FormatException>(() => new BasisSocialEventParser(_ => { }).Feed(invalid, invalid.Length));
        }
        [Test]
        public void InvitationsWithoutValidExpiryCannotBeAcceptedFromCachedUi()
        {
            Assert.That(new BasisSocialInvite { expiresAt = "invalid" }.IsExpired, Is.True);
            Assert.That(new BasisSocialInvite { expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") }.IsExpired, Is.True);
            Assert.That(new BasisSocialInvite { expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") }.IsExpired, Is.False);
        }
        [Test]
        public void RealtimeDiagnosticsNeverEchoExceptionMessagesOrServerCodes()
        {
            Assert.That(BasisSocialRealtime.FailureCode(new BasisSocialApiException(500, "streaming_unsupported", "remote secret")), Is.EqualTo("http_500"));
            Assert.That(BasisSocialRealtime.FailureCode(new BasisSocialApiException(0, "remote secret", "access token")), Is.EqualTo("network_error"));
            Assert.That(BasisSocialRealtime.FailureCode(new Exception("private payload")), Is.EqualTo("network_error"));
            Assert.That(BasisSocialRealtime.FailureCode(new BasisSocialApiException(0, "realtime_invalid_event", "payload")), Is.EqualTo("invalid_event"));
            Assert.That(BasisSocialRealtime.FailureCode(new TimeoutException("signed URL")), Is.EqualTo("stream_timeout"));
        }
        [Test]
        public void SseParsesRealConnectedEnvelopeWithSeventeenByteChunks()
        {
            var events = new List<BasisSocialRealtimeEvent>();
            var parser = new BasisSocialEventParser(events.Add);
            var wire = Encoding.UTF8.GetBytes("event: realtime.connected\ndata: {\"id\":\"1758233820000000000\",\"type\":\"realtime.connected\",\"actorId\":\"" + Id + "\",\"payload\":{\"username\":\"fixture\"},\"createdAt\":\"2026-09-19T10:00:00Z\"}\n\n: keepalive\n\n");
            for (int offset = 0; offset < wire.Length; offset += 17)
            {
                var chunk = new byte[Math.Min(17, wire.Length - offset)]; Array.Copy(wire, offset, chunk, 0, chunk.Length); parser.Feed(chunk, chunk.Length);
            }
            Assert.That(events.Count, Is.EqualTo(1)); Assert.That(events[0].type, Is.EqualTo("realtime.connected"));
            Assert.That(parser.Cursor, Is.Null, "Connection acknowledgements are not replay cursors.");
        }
        private static BasisSocialApiClient Client(Transport transport) { var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh"); return new BasisSocialApiClient(transport, tokens); }
        private sealed class Transport : IBasisSocialHttpTransport
        {
            public readonly Queue<BasisSocialHttpResponse> Replies = new(); public readonly List<BasisSocialHttpRequest> Requests = new();
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Replies.Dequeue()); }
        }
    }
}
