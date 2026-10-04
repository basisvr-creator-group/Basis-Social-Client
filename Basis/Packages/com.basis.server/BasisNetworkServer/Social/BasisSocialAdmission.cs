using Basis.Network.Core;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server.Social
{
    /// <summary>Server-only Social admission and authoritative membership lifecycle; never receives a user's access token.</summary>
    public sealed class BasisSocialAdmission : IDisposable
    {
        private readonly HttpClient http;
        private readonly string instanceId;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly ConcurrentDictionary<NetPeer, Membership> members = new ConcurrentDictionary<NetPeer, Membership>();
        private readonly ConcurrentDictionary<string, Membership> actors = new ConcurrentDictionary<string, Membership>(StringComparer.Ordinal);
        private readonly SemaphoreSlim admissionSlots = new SemaphoreSlim(16, 16);
        private volatile bool healthy;
        private int disposed;

        private sealed class Membership
        {
            internal NetPeer Peer;
            internal string ActorId;
            internal readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            internal int Leaving;
        }

        public static BasisSocialAdmission FromEnvironment(Configuration configuration)
        {
            string origin = Environment.GetEnvironmentVariable("BASIS_WORLD_SOCIAL_URL");
            string instance = Environment.GetEnvironmentVariable("BASIS_WORLD_INSTANCE_ID");
            string credential = Environment.GetEnvironmentVariable("BASIS_WORLD_CREDENTIAL");
            if (string.IsNullOrEmpty(origin) && string.IsNullOrEmpty(instance) && string.IsNullOrEmpty(credential)) return null;
            if (!configuration.UseAuthIdentity || !Guid.TryParse(instance, out Guid id) || id == Guid.Empty ||
                string.IsNullOrEmpty(credential) || !credential.StartsWith("bvr_ws_", StringComparison.Ordinal) || credential.Length > 256 ||
                !TryValidateOrigin(origin, Environment.GetEnvironmentVariable("BASIS_WORLD_ALLOW_LOOPBACK_HTTP") == "1", out Uri endpoint))
                throw new InvalidOperationException("Social world configuration requires an HTTPS origin, instance, server credential and DID authentication.");
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("X-Basis-Service-Token", credential);
            return new BasisSocialAdmission(client, id.ToString("D"));
        }

        internal static bool TryValidateOrigin(string origin, bool allowLoopback, out Uri endpoint)
        {
            endpoint = null;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri uri) || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            bool local = IPAddress.TryParse(uri.Host, out IPAddress address) && IPAddress.IsLoopback(address);
            if (uri.Scheme != "https" && !(allowLoopback && local && uri.Scheme == "http")) return false;
            endpoint = uri;
            return true;
        }

        internal BasisSocialAdmission(HttpClient client, string instance)
        {
            http = client;
            instanceId = instance;
            _ = RunHeartbeatsAsync();
        }

        /// <summary>Invoke only after DID signature verification; isCurrent fences disconnected/replaced peers after every await.</summary>
        public async Task<bool> AdmitAsync(NetPeer peer, string ticket, string verifiedDid, Func<bool> isCurrent)
        {
            if (!BasisSocialJoinTicket.IsValid(ticket) || !isCurrent() || lifetime.IsCancellationRequested || !admissionSlots.Wait(0)) return false;
            try
            {
                if (!healthy || !isCurrent()) return false;
                ConsumeResponse result = await SendAsync<ConsumeResponse>(HttpMethod.Post, "/api/v1/service/instance-join-tickets/consume",
                    new ConsumeRequest { ticket = ticket, instanceId = instanceId, clientDid = verifiedDid }, lifetime.Token).ConfigureAwait(false);
                if (result.state != "joined" || !Guid.TryParse(result.actorId, out _) || result.instance?.id != instanceId) return false;
                var member = new Membership { Peer = peer, ActorId = result.actorId };
                // Keep an actor reserved through its leave HTTP request: late disconnect cleanup cannot erase a replacement's presence.
                if (!actors.TryAdd(member.ActorId, member)) return false;
                members[peer] = member;
                if (!isCurrent() || !healthy || lifetime.IsCancellationRequested)
                {
                    await LeaveAsync(member).ConfigureAwait(false);
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is SerializationException || ex is InvalidDataException || ex is ObjectDisposedException)
            {
                BNL.LogWarning("Social admission unavailable or denied.");
                return false;
            }
            finally { admissionSlots.Release(); }
        }

        public void PeerLeft(NetPeer peer)
        {
            if (peer != null && members.TryGetValue(peer, out Membership member)) _ = LeaveAsync(member);
        }

        private async Task LeaveAsync(Membership member)
        {
            if (Interlocked.Exchange(ref member.Leaving, 1) != 0) return;
            await member.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync<EmptyResponse>(HttpMethod.Delete, MemberPath(member), null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is SerializationException || ex is ObjectDisposedException)
            {
                BNL.LogWarning("Social leave could not be confirmed; membership lease will expire.");
            }
            finally
            {
                members.TryRemove(member.Peer, out _);
                ((ICollection<KeyValuePair<string, Membership>>)actors).Remove(new KeyValuePair<string, Membership>(member.ActorId, member));
                member.Gate.Release();
            }
        }

        private async Task RenewLeaseAsync(CancellationToken token)
        {
            await SendAsync<EmptyResponse>(HttpMethod.Post, "/api/v1/service/instances/" + instanceId + "/heartbeat", new LeaseRequest(), token).ConfigureAwait(false);
            healthy = true;
        }

        private async Task RunHeartbeatsAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await RenewLeaseAsync(lifetime.Token).ConfigureAwait(false);
                    // Bound concurrency without one task per member or a serial round trip per population.
                    var batch = new List<Task>(16);
                    foreach (Membership member in members.Values)
                    {
                        batch.Add(RenewMemberAsync(member));
                        if (batch.Count == 16) { await Task.WhenAll(batch).ConfigureAwait(false); batch.Clear(); }
                    }
                    await Task.WhenAll(batch).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is SerializationException || ex is InvalidDataException || ex is ObjectDisposedException)
                {
                    bool wasHealthy = healthy;
                    healthy = false;
                    if (wasHealthy && !lifetime.IsCancellationRequested) BNL.LogWarning("Social world lease unavailable; current connections closed.");
                    foreach (Membership member in members.Values)
                    {
                        BasisServerHandleEvents.RejectWithReason(member.Peer, "Social instance is unavailable. Please reconnect.");
                        _ = LeaveAsync(member);
                    }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(healthy ? 20 : 5), lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RenewMemberAsync(Membership member)
        {
            if (Volatile.Read(ref member.Leaving) != 0) return;
            await member.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref member.Leaving) == 0)
                    await SendAsync<EmptyResponse>(HttpMethod.Post, MemberPath(member) + "/heartbeat", new MemberRequest(), lifetime.Token).ConfigureAwait(false);
            }
            finally { member.Gate.Release(); }
        }

        private string MemberPath(Membership member) => "/api/v1/service/instances/" + instanceId + "/members/" + member.ActorId;

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken token) where T : class, new()
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            token = requestTimeout.Token;
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                using var json = new MemoryStream();
                new DataContractJsonSerializer(body.GetType()).WriteObject(json, body);
                request.Content = new ByteArrayContent(json.ToArray());
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Social request denied.");
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(EmptyResponse)) return new T();
            if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("Social response too large.");
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[2048];
            int count;
            while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 16384) throw new InvalidDataException("Social response too large.");
                buffer.Write(bytes, 0, count);
            }
            buffer.Position = 0;
            return new DataContractJsonSerializer(typeof(T)).ReadObject(buffer) as T ?? throw new SerializationException("Invalid Social response.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            healthy = false;
            lifetime.Cancel();
            _ = CloseAsync();
        }

        private async Task CloseAsync()
        {
            var leaves = new List<Task>();
            foreach (Membership member in members.Values) leaves.Add(LeaveAsync(member));
            await Task.WhenAll(leaves).ConfigureAwait(false);
            http.Dispose();
        }

        [DataContract] private sealed class ConsumeRequest
        {
            [DataMember] public string ticket;
            [DataMember] public string instanceId;
            [DataMember] public string clientDid;
        }
        [DataContract] private sealed class ConsumeResponse
        {
            [DataMember] public string state;
            [DataMember] public string actorId;
            [DataMember] public InstanceIdentity instance;
        }
        [DataContract] private sealed class InstanceIdentity { [DataMember] public string id; }
        [DataContract] private sealed class LeaseRequest { [DataMember] public int ttlSeconds = 90; }
        [DataContract] private sealed class MemberRequest { }
        [DataContract] private sealed class EmptyResponse { }
    }
}
