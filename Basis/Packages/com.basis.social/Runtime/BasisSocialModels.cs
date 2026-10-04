using System;

namespace Basis.Social
{
    [Serializable]
    public sealed class BasisSocialAuthSession
    {
        public string accessToken;
        public string refreshToken;
        public long accessTokenExpiresIn;
        public long refreshTokenExpiresIn;
        public BasisSocialUser user;
    }

    [Serializable]
    public sealed class BasisSocialUser
    {
        public string id;
        public string actorId;
        public string email;
        public string username;
        public string acct;
        public string status;
        public BasisSocialProfile profile;
        public BasisSocialActivityPub activityPub;

        // The v1 OpenAPI draft currently describes AuthSession.user as a flat Profile,
        // while the running API returns the account envelope above. Keeping these fields
        // makes the client tolerant of either representation during contract migration.
        public string displayName;
        public string bio;
        public string avatarUrl;
        public string bannerUrl;
        public string statusText;

        public BasisSocialProfile EffectiveProfile
        {
            get
            {
                if (profile != null) return profile;
                return new BasisSocialProfile
                {
                    displayName = displayName,
                    bio = bio,
                    avatarUrl = avatarUrl,
                    bannerUrl = bannerUrl,
                    statusText = statusText
                };
            }
        }
    }

    [Serializable]
    public sealed class BasisSocialProfile
    {
        public string displayName;
        public string bio;
        public string avatarUrl;
        public string bannerUrl;
        public string statusText;
    }

    [Serializable]
    public sealed class BasisSocialActivityPub
    {
        public string actorUri;
        public string inboxUrl;
        public string outboxUrl;
        public string followersUrl;
        public string followingUrl;
    }

    [Serializable]
    public sealed class BasisSocialBeeBaAuthorization
    {
        public string deviceCode;
        public string userCode;
        public string verificationUri;
        public long expiresIn;
        public long interval;
    }

    [Serializable]
    public sealed class BasisSocialDeviceSession
    {
        public string id;
        public string createdAt;
        public string lastUsedAt;
        public string expiresAt;
        public bool current;
    }

    [Serializable]
    internal sealed class BasisSocialSessionList { public BasisSocialDeviceSession[] items; }

    [Serializable]
    internal sealed class BasisSocialBeeBaStartRequest { public string codeChallenge; }

    [Serializable]
    internal sealed class BasisSocialBeeBaCompleteRequest { public string deviceCode; public string codeVerifier; }

    [Serializable]
    internal sealed class BasisSocialProfileTextUpdate { public string bio; public string statusText; }

    [Serializable]
    internal sealed class BasisSocialLoginRequest
    {
        public string login;
        public string password;
    }

    [Serializable]
    internal sealed class BasisSocialRefreshRequest
    {
        public string refreshToken;
    }

    [Serializable]
    internal sealed class BasisSocialErrorEnvelope
    {
        public BasisSocialError error;
    }

    [Serializable]
    internal sealed class BasisSocialError
    {
        public string code;
        public string message;
    }
}
