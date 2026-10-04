using Basis.Network.Core;
using Basis.Network.Server.Social;
using System.Net;
using System.Text;
using Xunit;

namespace BasisServerTests;

public sealed class SocialAdmissionTests
{
    private const string Ticket = "bvr_jt_0123456789012345678901234567890123456789012";
    private const string Instance = "88888888-8888-4888-8888-888888888888";
    private const string Actor = "99999999-9999-4999-8999-999999999999";
    private const string Did = "did:key:z6MkeTGwHmLmuCmgg4ABYhzWVh6ZX7hTwWt8gguAretUfc9c";

    [Fact]
    public void TicketExtensionIsStrictAndKeepsLegacyEmptyPayload()
    {
        var writer = new NetDataWriter();
        BasisSocialJoinTicket.Write(writer, null);
        Assert.Empty(writer.CopyData());
        Assert.True(BasisSocialJoinTicket.TryRead(new NetDataReader(writer.CopyData()), out string absent));
        Assert.Null(absent);
        BasisSocialJoinTicket.Write(writer, Ticket);
        Assert.True(BasisSocialJoinTicket.TryRead(new NetDataReader(writer.CopyData()), out string ticket));
        Assert.Equal(Ticket, ticket);
        writer.Put((byte)1);
        Assert.False(BasisSocialJoinTicket.TryRead(new NetDataReader(writer.CopyData()), out _));
        Assert.Throws<ArgumentException>(() => BasisSocialJoinTicket.Write(new NetDataWriter(), Ticket + "\n"));
    }

    [Theory]
    [InlineData("https://social.example", false, true)]
    [InlineData("http://127.0.0.1:18089", true, true)]
    [InlineData("http://127.0.0.1:18089", false, false)]
    [InlineData("http://localhost:18089", true, false)]
    [InlineData("http://social.example", true, false)]
    [InlineData("https://secret@social.example", false, false)]
    [InlineData("https://social.example/path", false, false)]
    [InlineData("https://social.example?token=x", false, false)]
    public void OperatorOriginCannotRedirectSecrets(string value, bool local, bool expected)
        => Assert.Equal(expected, BasisSocialAdmission.TryValidateOrigin(value, local, out _));

    [Fact]
    public async Task VerifiedPeerSendsBoundIdentityAndLeaveIsIdempotent()
    {
        var handler = new Handler();
        using var bridge = Create(handler);
        var peer = new FakeNetPeer(810, "127.0.0.1");
        Assert.True(await bridge.AdmitAsync(peer, Ticket, Did, () => true));
        Assert.Equal(1, handler.Consumes);
        Assert.Contains("\"clientDid\":\"" + Did + "\"", handler.LastBody);
        Assert.Contains("\"instanceId\":\"" + Instance + "\"", handler.LastBody);
        bridge.PeerLeft(peer);
        bridge.PeerLeft(peer);
        await handler.Leave.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.Leaves);
    }

    [Fact]
    public async Task InvalidExpiredOrWrongInstanceCannotAdmit()
    {
        var handler = new Handler { ConsumeStatus = HttpStatusCode.Conflict };
        using var bridge = Create(handler);
        var peer = new FakeNetPeer(811, "127.0.0.1");
        Assert.False(await bridge.AdmitAsync(peer, "invalid", Did, () => true));
        Assert.Equal(0, handler.Consumes);
        Assert.False(await bridge.AdmitAsync(peer, Ticket, Did, () => true));
        Assert.Equal(1, handler.Consumes);
        handler.ConsumeStatus = HttpStatusCode.OK;
        handler.ResponseInstance = "77777777-7777-4777-8777-777777777777";
        Assert.False(await bridge.AdmitAsync(peer, Ticket, Did, () => true));
    }

    [Fact]
    public async Task DisconnectedPeerDuringConsumeIsReleasedAndNeverAdmitted()
    {
        var handler = new Handler { PendingConsume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var bridge = Create(handler);
        bool current = true;
        Task<bool> admission = bridge.AdmitAsync(new FakeNetPeer(812, "127.0.0.1"), Ticket, Did, () => current);
        await handler.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        current = false;
        handler.PendingConsume.SetResult(true);
        Assert.False(await admission);
        Assert.Equal(1, handler.Leaves);
    }

    [Fact]
    public async Task FailedLeaseRejectsBeforeTicketConsumption()
    {
        using var bridge = Create(new Handler { LeaseStatus = HttpStatusCode.ServiceUnavailable });
        Assert.False(await bridge.AdmitAsync(new FakeNetPeer(813, "127.0.0.1"), Ticket, Did, () => true));
    }

    private static BasisSocialAdmission Create(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://social.example") }, Instance);

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode ConsumeStatus = HttpStatusCode.OK;
        public HttpStatusCode LeaseStatus = HttpStatusCode.OK;
        public string ResponseInstance = Instance;
        public int Consumes, Leaves;
        public string LastBody = "";
        public TaskCompletionSource<bool>? PendingConsume;
        public readonly TaskCompletionSource<bool> ConsumeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Leave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref Leaves);
                Leave.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/consume"))
            {
                Interlocked.Increment(ref Consumes);
                LastBody = await request.Content!.ReadAsStringAsync(ct);
                ConsumeStarted.TrySetResult(true);
                if (PendingConsume != null) await PendingConsume.Task.WaitAsync(ct);
                return new HttpResponseMessage(ConsumeStatus) { Content = new StringContent("{\"state\":\"joined\",\"actorId\":\"" + Actor + "\",\"instance\":{\"id\":\"" + ResponseInstance + "\"}}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(LeaseStatus) { Content = new StringContent("{}") };
        }
    }
}
