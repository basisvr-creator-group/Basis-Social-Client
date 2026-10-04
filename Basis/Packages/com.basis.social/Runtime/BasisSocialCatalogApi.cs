using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social
{
    public sealed partial class BasisSocialApiClient
    {
        public async Task<BasisSocialCatalog[]> ListCatalogsAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Get, "/api/v1/assets/catalogs", null, false, cancellationToken);
            return ReadArray<BasisSocialCatalogList>(response).items ?? Array.Empty<BasisSocialCatalog>();
        }

        public async Task<BasisSocialCatalogPage> SearchCatalogAsync(BasisSocialCatalogQuery query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (query.Limit < 1 || query.Limit > 50) throw new ArgumentOutOfRangeException(nameof(query.Limit));
            var parameters = new List<string>();
            AddQuery(parameters, "catalog", query.Catalog);
            AddQuery(parameters, "q", query.Query);
            AddQuery(parameters, "type", query.ContentType);
            AddQuery(parameters, "tags", query.Tags == null ? null : string.Join(",", query.Tags));
            AddQuery(parameters, "sort", query.Sort);
            AddQuery(parameters, "cursor", query.Cursor);
            AddQuery(parameters, "limit", query.Limit.ToString(CultureInfo.InvariantCulture));
            AddQuery(parameters, "includeNsfw", query.IncludeNSFW ? "true" : "false");
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Get, "/api/v1/assets/search?" + string.Join("&", parameters), null, false, cancellationToken);
            var page = Deserialize<BasisSocialCatalogPage>(response.Body, response.StatusCode);
            page.items ??= Array.Empty<BasisSocialCatalogAsset>();
            foreach (var asset in page.items) ValidateCatalogAsset(asset);
            return page;
        }

        /// <summary>Fetch live public package metadata before loading, including before reusing a cached package. Never falls back to an older snapshot.</summary>
        public async Task<BasisSocialCatalogAsset> ResolveCatalogAssetAsync(string catalog, string externalId, bool includeNsfw = false, CancellationToken cancellationToken = default)
        {
            RequireAssetID(externalId);
            var request = new BasisSocialResolveAssetRequest { catalog = catalog, externalId = externalId, includeNsfw = includeNsfw };
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Post, "/api/v1/assets/resolve", JsonUtility.ToJson(request), true, cancellationToken);
            var asset = Deserialize<BasisSocialCatalogAsset>(response.Body, response.StatusCode);
            ValidateCatalogAsset(asset);
            return asset;
        }

        public async Task<BasisSocialWorldAsset[]> ListWorldAssetsAsync(string worldId, bool includeNsfw = false, CancellationToken cancellationToken = default)
        {
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Get, WorldAssetsPath(worldId) + "?includeNsfw=" + (includeNsfw ? "true" : "false"), null, false, cancellationToken);
            return ReadWorldAssets(response);
        }

        /// <summary>Explicitly chooses the current version. Never call automatically after receiving update_available.</summary>
        public async Task<BasisSocialWorldAsset[]> AttachWorldAssetAsync(string worldId, string catalog, string externalId, string role = "primary", int sortOrder = 0, bool includeNsfw = false, CancellationToken cancellationToken = default)
        {
            RequireAssetID(externalId);
            if (role != "primary" && role != "dependency" && role != "preview" && role != "spawn" && role != "environment")
                throw new ArgumentException("A supported world asset role is required.", nameof(role));
            var request = new BasisSocialAttachAssetRequest { catalog = catalog, externalId = externalId, role = role, sortOrder = sortOrder, includeNsfw = includeNsfw };
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Post, WorldAssetsPath(worldId), JsonUtility.ToJson(request), true, cancellationToken);
            return ReadWorldAssets(response);
        }

        public async Task DetachWorldAssetAsync(string worldId, string assetRefId, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(assetRefId, out _)) throw new ArgumentException("A valid asset reference ID is required.", nameof(assetRefId));
            var response = await SendCatalogAsync(BasisSocialHttpMethod.Delete, WorldAssetsPath(worldId) + "/" + Uri.EscapeDataString(assetRefId), null, true, cancellationToken);
            EnsureSuccess(response, 204);
        }

        /// <summary>Call for every load, including cache hits. Rechecks world access and the owner's pinned version.</summary>
        public async Task<BasisSocialPackageLoad> PrepareWorldAssetLoadAsync(string worldId, string assetRefId, bool includeNsfw = false, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(assetRefId, out _)) throw new ArgumentException("A valid asset reference ID is required.", nameof(assetRefId));
            long epoch = SnapshotGeneration();
            var assets = await ListWorldAssetsAsync(worldId, includeNsfw, cancellationToken);
            AssertCurrent(epoch);
            foreach (var item in assets)
                if (string.Equals(item.asset.id, assetRefId, StringComparison.OrdinalIgnoreCase)) return PreparePackage(item.asset);
            throw new BasisSocialApiException(404, "asset_unavailable", "The world package is unavailable.");
        }

        /// <summary>Call for each direct catalog load. Does not authorize loading a world's different pinned version.</summary>
        public async Task<BasisSocialPackageLoad> PrepareCatalogAssetLoadAsync(string catalog, string externalId, bool includeNsfw = false, CancellationToken cancellationToken = default)
        {
            long epoch = SnapshotGeneration();
            var asset = await ResolveCatalogAssetAsync(catalog, externalId, includeNsfw, cancellationToken);
            AssertCurrent(epoch);
            return PreparePackage(asset);
        }

        private static BasisSocialPackageLoad PreparePackage(BasisSocialCatalogAsset asset)
        {
            if (asset.Availability != BasisSocialAssetAvailability.Available)
                throw new BasisSocialApiException(409, asset.Availability == BasisSocialAssetAvailability.UpdateAvailable ? "asset_update_available" : "asset_unavailable", "The package is unavailable or requires the owner's explicit update.");
            if (!Guid.TryParse(asset.versionId, out Guid version) || !Guid.TryParse(asset.externalId, out Guid content) ||
                string.IsNullOrWhiteSpace(asset.unlockPassword) || asset.sizeBytes <= 0 || asset.sha256?.Length != 64 ||
                !Uri.TryCreate(asset.downloadUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath != "/api/v1/content/" + content.ToString("D") + "/download" ||
                uri.Query != "?version=" + version.ToString("D"))
                throw new BasisSocialApiException(0, "invalid_response", "The catalog did not return a public immutable package manifest.");
            foreach (char c in asset.sha256)
                if (!Uri.IsHexDigit(c)) throw new BasisSocialApiException(0, "invalid_response", "The package hash is invalid.");
            return new BasisSocialPackageLoad(asset);
        }

        private async Task<BasisSocialHttpResponse> SendCatalogAsync(BasisSocialHttpMethod method, string path, string body, bool requireSession, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long epoch = SnapshotGeneration();
            var response = requireSession || HasSession
                ? await SendAuthorizedAsync(method, path, body, true, cancellationToken)
                : await transport.SendAsync(new BasisSocialHttpRequest { Method = method, Path = path, BodyJson = body }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AssertCurrent(epoch);
            EnsureSuccess(response);
            return response;
        }

        private static void AddQuery(List<string> values, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) values.Add(key + "=" + Uri.EscapeDataString(value));
        }

        private static void RequireAssetID(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An external asset ID is required.", nameof(id));
        }

        private static string WorldAssetsPath(string id)
        {
            if (!Guid.TryParse(id, out _)) throw new ArgumentException("A valid world ID is required.", nameof(id));
            return "/api/v1/worlds/" + Uri.EscapeDataString(id) + "/assets";
        }

        private static T ReadArray<T>(BasisSocialHttpResponse response)
        {
            string json = response.Body?.Trim();
            if (string.IsNullOrEmpty(json) || !json.StartsWith("[", StringComparison.Ordinal) || !json.EndsWith("]", StringComparison.Ordinal))
                throw new BasisSocialApiException(response.StatusCode, "invalid_response", "Expected a catalog list.");
            return Deserialize<T>("{\"items\":" + json + "}", response.StatusCode);
        }

        private static BasisSocialWorldAsset[] ReadWorldAssets(BasisSocialHttpResponse response)
        {
            var items = ReadArray<BasisSocialWorldAssetList>(response).items ?? Array.Empty<BasisSocialWorldAsset>();
            foreach (var item in items) ValidateCatalogAsset(item?.asset);
            return items;
        }

        private static void ValidateCatalogAsset(BasisSocialCatalogAsset asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.externalId) || asset.Availability == BasisSocialAssetAvailability.Unknown)
                throw new BasisSocialApiException(0, "invalid_response", "The catalog returned incomplete availability information.");
            // A stale or unexpected server response cannot turn a tombstone/update into a loadable package.
            if (asset.Availability != BasisSocialAssetAvailability.Available)
            {
                asset.downloadUrl = asset.unlockPassword = asset.sha256 = null;
                asset.sizeBytes = 0;
                if (asset.Availability == BasisSocialAssetAvailability.Unavailable)
                {
                    asset.title = asset.description = asset.previewUrl = asset.externalUrl = asset.authorName = asset.license = null;
                    asset.tags = Array.Empty<string>();
                }
            }
        }
    }
}
