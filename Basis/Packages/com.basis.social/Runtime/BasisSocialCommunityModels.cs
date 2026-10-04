using System;

namespace Basis.Social
{
    [Serializable] public sealed class BasisSocialPagination { public string nextCursor; public int limit; }
    [Serializable] public sealed class BasisSocialPage<T> { public T[] data; public BasisSocialPagination pagination; }
    [Serializable] public sealed class BasisSocialActor
    {
        public string actorId, acct, username, displayName, avatarUrl;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? acct ?? username : displayName;
    }
    [Serializable] public sealed class BasisSocialWorld
    {
        public string id, slug, name, description, previewUrl, launchUrl, visibility;
        public int capacity;
        public string[] tags;
        public BasisSocialActor owner;
    }
    [Serializable] public sealed class BasisSocialInstance
    {
        public string id, worldId, hostActorId, instanceKey, name, visibility, launchUrl, status, expiresAt;
        public int capacity, currentUsers;
    }
    [Serializable] public sealed class BasisSocialInvite
    {
        public string id, worldId, instanceId, eventId, message, visibility, state, expiresAt, createdAt;
        public BasisSocialActor from, to;
        public bool IsExpired => !DateTimeOffset.TryParse(expiresAt, out var expiry) || expiry <= DateTimeOffset.UtcNow;
    }
    [Serializable] public sealed class BasisSocialPresence
    {
        public string id, actorId, acct, displayName, worldId, instanceId, status, visibility, expiresAt, updatedAt;
        public bool showExactInstance;
    }
    [Serializable] internal sealed class BasisSocialActorIdRequest { public string targetActorId; }
    [Serializable] internal sealed class BasisSocialAccountRequest { public string targetAcct; }
    [Serializable] internal sealed class BasisSocialWorldInviteRequest { public string toAcct, worldId, message; public string visibility = "direct"; }
    [Serializable] internal sealed class BasisSocialInstanceInviteRequest { public string toAcct, instanceId, message; public string visibility = "direct"; }
    [Serializable] internal sealed class BasisSocialPresenceRequest { public string status, visibility; public bool showExactInstance; }
}
