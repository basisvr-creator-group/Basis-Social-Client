using System;

namespace Basis.Social
{
    public enum BasisSocialAssetAvailability { Unknown, Available, Unavailable, UpdateAvailable }

    [Serializable]
    public sealed class BasisSocialCatalog
    {
        public string id;
        public string code;
        public string name;
        public string kind;
        public string baseUrl;
        public string apiBaseUrl;
        public bool enabled;
    }

    // Catalog responses are snapshots, never an offline authorization to load cached bytes.
    [Serializable]
    public sealed class BasisSocialCatalogAsset
    {
        public string id;
        public BasisSocialCatalog catalog;
        public string externalId;
        public string externalUrl;
        public string contentType;
        public string title;
        public string description;
        public string previewUrl;
        public string downloadUrl;
        public string authorName;
        public string license;
        public bool nsfw;
        public string[] tags;
        public string versionId;
        public string sha256;
        public long sizeBytes;
        public string unlockPassword;
        public string availability;
        public string revision;

        public BasisSocialAssetAvailability Availability => availability switch
        {
            "available" => BasisSocialAssetAvailability.Available,
            "unavailable" => BasisSocialAssetAvailability.Unavailable,
            "update_available" => BasisSocialAssetAvailability.UpdateAvailable,
            _ => BasisSocialAssetAvailability.Unknown
        };

        public string GetAvailabilityMessage(string locale = "en")
        {
            bool ru = string.Equals(locale, "ru", StringComparison.OrdinalIgnoreCase);
            return Availability switch
            {
                BasisSocialAssetAvailability.Available => ru ? "Доступно" : "Available",
                BasisSocialAssetAvailability.UpdateAvailable => ru ? "Доступна новая версия. Владелец мира должен подтвердить обновление." : "An update is available. The world owner must confirm the new version.",
                BasisSocialAssetAvailability.Unavailable => ru ? "Материал недоступен." : "This content is unavailable.",
                _ => ru ? "Не удалось проверить доступность." : "Availability could not be verified."
            };
        }
    }

    [Serializable]
    public sealed class BasisSocialCatalogPage
    {
        public BasisSocialCatalog catalog;
        public BasisSocialCatalogAsset[] items;
        public string nextCursor;
    }

    [Serializable]
    public sealed class BasisSocialWorldAsset
    {
        public BasisSocialCatalogAsset asset;
        public string role;
        public int sortOrder;
    }

    public sealed class BasisSocialCatalogQuery
    {
        public string Catalog;
        public string Query;
        public string ContentType;
        public string[] Tags;
        public bool IncludeNSFW;
        public string Sort;
        public int Limit = 20;
        public string Cursor;
    }

    /// <summary>Ephemeral result of a live access check. Never serialize, share with peers or use as an offline grant.</summary>
    public sealed class BasisSocialPackageLoad
    {
        public string ContentType { get; }
        public string DownloadUrl { get; }
        public string UnlockPassword { get; }
        public string RemoteVersionTag { get; }
        public string VersionId { get; }
        public long SizeBytes { get; }

        internal BasisSocialPackageLoad(BasisSocialCatalogAsset asset)
        {
            ContentType = asset.contentType;
            DownloadUrl = asset.downloadUrl;
            UnlockPassword = asset.unlockPassword;
            RemoteVersionTag = "\"" + asset.sha256.ToLowerInvariant() + "\"";
            VersionId = asset.versionId;
            SizeBytes = asset.sizeBytes;
        }
    }

    [Serializable] internal sealed class BasisSocialCatalogList { public BasisSocialCatalog[] items; }
    [Serializable] internal sealed class BasisSocialWorldAssetList { public BasisSocialWorldAsset[] items; }
    [Serializable] internal sealed class BasisSocialResolveAssetRequest { public string catalog; public string externalId; public bool includeNsfw; }
    [Serializable] internal sealed class BasisSocialAttachAssetRequest
    {
        public string catalog;
        public string externalId;
        public string role;
        public int sortOrder;
        public bool includeNsfw;
    }
}
