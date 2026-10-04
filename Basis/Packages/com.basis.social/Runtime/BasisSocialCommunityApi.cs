using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social
{
    public sealed partial class BasisSocialApiClient
    {
        public Task<BasisSocialPage<BasisSocialActor>> ListFriendsAsync(string state = "accepted", string direction = "", string cursor = null, int limit = 12, CancellationToken cancellationToken = default)
        {
            if (state != "accepted" && state != "pending") throw new ArgumentException("Unsupported friendship state.", nameof(state));
            if (direction != "" && direction != "incoming" && direction != "outgoing") throw new ArgumentException("Unsupported friendship direction.", nameof(direction));
            return CommunityPageAsync<BasisSocialActor>("/api/v1/friends" + PageQuery(cursor, limit) + "&state=" + state + "&direction=" + direction, true, cancellationToken);
        }
        public async Task FriendActionAsync(string action, string actorId = null, string acct = null, CancellationToken cancellationToken = default)
        {
            if (action != "request" && action != "accept" && action != "reject" && action != "remove") throw new ArgumentException("Unsupported friendship action.", nameof(action));
            if (!string.IsNullOrEmpty(actorId)) RequireCommunityId(actorId);
            else if (string.IsNullOrWhiteSpace(acct) || acct.Length > 320) throw new ArgumentException("An account is required.", nameof(acct));
            await SendCatalogAsync(BasisSocialHttpMethod.Post, "/api/v1/friends/" + action, !string.IsNullOrEmpty(actorId) ? JsonUtility.ToJson(new BasisSocialActorIdRequest { targetActorId = actorId }) : JsonUtility.ToJson(new BasisSocialAccountRequest { targetAcct = acct.Trim() }), true, cancellationToken);
        }
        public Task<BasisSocialPage<BasisSocialWorld>> ListWorldsAsync(string cursor = null, int limit = 12, CancellationToken cancellationToken = default) =>
            CommunityPageAsync<BasisSocialWorld>("/api/v1/worlds" + PageQuery(cursor, limit), false, cancellationToken);
        public async Task<BasisSocialWorld> GetWorldAsync(string slug, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200) throw new ArgumentException("A world slug is required.", nameof(slug));
            return Deserialize<BasisSocialWorld>((await SendCatalogAsync(BasisSocialHttpMethod.Get, "/api/v1/worlds/" + Uri.EscapeDataString(slug), null, false, cancellationToken)).Body, 200);
        }
        public Task<BasisSocialPage<BasisSocialInstance>> ListInstancesAsync(string worldId, string cursor = null, int limit = 12, CancellationToken cancellationToken = default)
        {
            RequireCommunityId(worldId);
            return CommunityPageAsync<BasisSocialInstance>("/api/v1/worlds/" + Uri.EscapeDataString(worldId) + "/instances" + PageQuery(cursor, limit), false, cancellationToken);
        }
        public async Task<BasisSocialInstance> GetInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            RequireCommunityId(instanceId);
            return Deserialize<BasisSocialInstance>((await SendCatalogAsync(BasisSocialHttpMethod.Get, "/api/v1/instances/" + Uri.EscapeDataString(instanceId), null, true, cancellationToken)).Body, 200);
        }
        public Task<BasisSocialPage<BasisSocialInvite>> ListInvitesAsync(string cursor = null, int limit = 12, CancellationToken cancellationToken = default) =>
            CommunityPageAsync<BasisSocialInvite>("/api/v1/invites" + PageQuery(cursor, limit) + "&direction=incoming", true, cancellationToken);
        public async Task<BasisSocialInvite> SendInviteAsync(string acct, string worldId = null, string instanceId = null, string message = "", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(acct) || acct.Length > 320) throw new ArgumentException("An account is required.", nameof(acct));
            if (!string.IsNullOrEmpty(worldId)) RequireCommunityId(worldId);
            if (!string.IsNullOrEmpty(instanceId)) RequireCommunityId(instanceId);
            if (string.IsNullOrEmpty(worldId) && string.IsNullOrEmpty(instanceId)) throw new ArgumentException("An invitation target is required.");
            if (message?.Length > 2000) throw new ArgumentException("The message is too long.", nameof(message));
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Post, "/api/v1/invites", !string.IsNullOrEmpty(instanceId) ? JsonUtility.ToJson(new BasisSocialInstanceInviteRequest { toAcct = acct.Trim(), instanceId = instanceId, message = message }) : JsonUtility.ToJson(new BasisSocialWorldInviteRequest { toAcct = acct.Trim(), worldId = worldId, message = message }), true, cancellationToken);
            return Deserialize<BasisSocialInvite>(response.Body, response.StatusCode);
        }
        public async Task DecideInviteAsync(string inviteId, bool accept, CancellationToken cancellationToken = default)
        {
            RequireCommunityId(inviteId);
            await SendCatalogAsync(BasisSocialHttpMethod.Post, "/api/v1/invites/" + Uri.EscapeDataString(inviteId) + (accept ? "/accept" : "/decline"), "{}", true, cancellationToken);
        }
        public Task<BasisSocialPage<BasisSocialPresence>> ListFriendPresenceAsync(string cursor = null, int limit = 12, CancellationToken cancellationToken = default) =>
            CommunityPageAsync<BasisSocialPresence>("/api/v1/presence/friends" + PageQuery(cursor, limit), true, cancellationToken);
        public async Task<BasisSocialPresence> SetPresenceAsync(string status, string visibility, CancellationToken cancellationToken = default)
        {
            if (status != "online" && status != "away" && status != "busy" && status != "invisible") throw new ArgumentException("Unsupported presence.", nameof(status));
            if (visibility != "nobody" && visibility != "friends" && visibility != "followers" && visibility != "public") throw new ArgumentException("Unsupported visibility.", nameof(visibility));
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Post, "/api/v1/presence", JsonUtility.ToJson(new BasisSocialPresenceRequest { status = status, visibility = visibility, showExactInstance = false }), true, cancellationToken);
            return Deserialize<BasisSocialPresence>(response.Body, response.StatusCode);
        }
        public async Task RemovePresenceAsync(CancellationToken cancellationToken = default) =>
            await SendCatalogAsync(BasisSocialHttpMethod.Delete, "/api/v1/presence", null, true, cancellationToken);
        private async Task<BasisSocialPage<T>> CommunityPageAsync<T>(string path, bool authenticated, CancellationToken cancellationToken)
        {
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Get, path, null, authenticated, cancellationToken);
            var page = Deserialize<BasisSocialPage<T>>(response.Body, response.StatusCode);
            if (page.pagination == null || page.data == null || page.data.Length > 50) throw new BasisSocialApiException(0, "invalid_response", "The service returned an invalid page.");
            return page;
        }
        private static string PageQuery(string cursor, int limit)
        {
            if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException(nameof(limit));
            if (cursor?.Length > 2048) throw new ArgumentException("The cursor is too long.", nameof(cursor));
            return "?limit=" + limit + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        }
        private static void RequireCommunityId(string id)
        {
            if (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty) throw new ArgumentException("A valid ID is required.", nameof(id));
        }
    }
}
