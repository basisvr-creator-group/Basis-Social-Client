using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social
{
    [Serializable]
    public sealed class BasisSocialInstanceTicket
    {
        public string ticket;
        public string expiresAt;
        public BasisSocialInstance instance;
    }

    [Serializable]
    internal sealed class BasisSocialIssueTicketRequest
    {
        public string clientDid;
        public string presenceVisibility;
        public bool showExactInstance;
    }

    public sealed partial class BasisSocialApiClient
    {
        public async Task<BasisSocialInstanceTicket> IssueJoinTicketAsync(string instanceId, string clientDid,
            string presenceVisibility = "nobody", bool showExactInstance = false, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(instanceId, out Guid id) || id == Guid.Empty) throw new ArgumentException("Valid instance required.", nameof(instanceId));
            if (string.IsNullOrEmpty(clientDid) || clientDid.Length > 128 || !clientDid.StartsWith("did:key:z", StringComparison.Ordinal))
                throw new ArgumentException("A client key identity is required.", nameof(clientDid));
            if (presenceVisibility != "nobody" && presenceVisibility != "friends" && presenceVisibility != "followers" && presenceVisibility != "public")
                throw new ArgumentException("Invalid presence visibility.", nameof(presenceVisibility));
            long epoch = SnapshotGeneration();
            var response = await SendAuthorizedAsync(BasisSocialHttpMethod.Post, "/api/v1/instances/" + id.ToString("D") + "/join-tickets",
                JsonUtility.ToJson(new BasisSocialIssueTicketRequest { clientDid = clientDid, presenceVisibility = presenceVisibility, showExactInstance = showExactInstance }), true, cancellationToken);
            AssertCurrent(epoch);
            EnsureSuccess(response);
            var result = Deserialize<BasisSocialInstanceTicket>(response.Body, response.StatusCode);
            if (result.instance?.id != id.ToString("D") || string.IsNullOrEmpty(result.ticket) || result.ticket.Length != 50 ||
                !result.ticket.StartsWith("bvr_jt_", StringComparison.Ordinal) || !DateTimeOffset.TryParse(result.expiresAt, out var expiry) || expiry <= DateTimeOffset.UtcNow)
                throw new BasisSocialApiException(0, "invalid_response", "The service returned an invalid instance ticket.");
            return result;
        }
    }
}
