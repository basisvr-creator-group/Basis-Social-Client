using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialCatalogTests
    {
        private const string World = "c1712d20-4037-4a62-a10f-215055333918";
        private const string Asset = "{\"externalId\":\"asset-1\",\"availability\":\"available\",\"versionId\":\"v1\",\"sha256\":\"hash\",\"sizeBytes\":4294967296,\"unlockPassword\":\"package-key\",\"downloadUrl\":\"https://catalog.example/download?version=v1\"}";

        [Test]
        public async Task SearchEncodesCursorAndFiltersAndDecodesLongSize()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, "{\"items\":[" + Asset + "],\"nextCursor\":\"next+/=\"}");
            var client = new BasisSocialApiClient(transport);
            var page = await client.SearchCatalogAsync(new BasisSocialCatalogQuery { Query = "avatar&includeNsfw=true", Cursor = "x+/=", Tags = new[] { "blue", "red" } });
            Assert.That(page.items[0].sizeBytes, Is.EqualTo(4294967296L));
            Assert.That(page.items[0].Availability, Is.EqualTo(BasisSocialAssetAvailability.Available));
            Assert.That(page.nextCursor, Is.EqualTo("next+/="));
            Assert.That(transport.Requests[0].Path, Does.Contain("q=avatar%26includeNsfw%3Dtrue"));
            Assert.That(transport.Requests[0].Path, Does.Contain("cursor=x%2B%2F%3D"));
            Assert.That(transport.Requests[0].Path, Does.Contain("includeNsfw=false"));
            Assert.That(transport.Requests[0].AccessToken, Is.Null);
        }

        [Test]
        public async Task CatalogAndWorldListsUseBareArrayContract()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, "[{\"code\":\"beeba\",\"enabled\":true}]");
            transport.Enqueue(200, "[{\"asset\":" + Asset + ",\"role\":\"primary\",\"sortOrder\":2}]");
            var client = new BasisSocialApiClient(transport);
            Assert.That((await client.ListCatalogsAsync())[0].code, Is.EqualTo("beeba"));
            var worldAssets = await client.ListWorldAssetsAsync(World);
            Assert.That(worldAssets[0].role, Is.EqualTo("primary"));
            Assert.That(worldAssets[0].sortOrder, Is.EqualTo(2));
            Assert.That(transport.Requests[1].Path, Does.EndWith("/assets?includeNsfw=false"));
        }

        [TestCase("unavailable")]
        [TestCase("update_available")]
        public async Task NonAvailableVersionsCannotReturnLoadCredentials(string state)
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, "[{\"asset\":" + Asset.Replace("available", state) + ",\"role\":\"primary\"}]");
            var result = (await new BasisSocialApiClient(transport).ListWorldAssetsAsync(World))[0].asset;
            Assert.That(result.downloadUrl, Is.Null);
            Assert.That(result.unlockPassword, Is.Null);
            Assert.That(result.sha256, Is.Null);
            Assert.That(result.sizeBytes, Is.Zero);
            Assert.That(result.versionId, Is.EqualTo("v1"));
            Assert.That(result.GetAvailabilityMessage("en"), Is.Not.EqualTo(result.GetAvailabilityMessage("ru")));
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "An update never automatically reattaches a package.");
        }

        [Test]
        public void MissingAvailabilityFailsClosed()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, "{\"items\":[{\"externalId\":\"asset\"}]}");
            var error = Assert.ThrowsAsync<BasisSocialApiException>(async () => await new BasisSocialApiClient(transport).SearchCatalogAsync(new BasisSocialCatalogQuery()));
            Assert.That(error.ErrorCode, Is.EqualTo("invalid_response"));
        }

        [Test]
        public async Task ResolveAndExplicitAttachUseSessionAndNsfwOptIn()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, Asset);
            transport.Enqueue(200, "[]");
            transport.Enqueue(204, "");
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("social-access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            await client.ResolveCatalogAssetAsync("beeba", "asset-1", true);
            await client.AttachWorldAssetAsync(World, "beeba", "asset-1", includeNsfw: true);
            await client.DetachWorldAssetAsync(World, World);
            Assert.That(transport.Requests[0].AccessToken, Is.EqualTo("social-access"));
            Assert.That(transport.Requests[0].BodyJson, Does.Contain("\"includeNsfw\":true"));
            Assert.That(transport.Requests[1].BodyJson, Does.Contain("\"externalId\":\"asset-1\""));
            Assert.That(transport.Requests[1].BodyJson, Does.Not.Contain("downloadUrl"));
            Assert.That(transport.Requests[2].Method, Is.EqualTo(BasisSocialHttpMethod.Delete));
        }

        [Test]
        public async Task OutageDoesNotReturnEarlierResolvedSnapshotOrClearSession()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, Asset);
            transport.Enqueue(503, "{\"error\":{\"code\":\"catalog_unavailable\"}}");
            var tokens = new BasisSocialMemoryTokenStore(); tokens.Save("access", "refresh");
            var client = new BasisSocialApiClient(transport, tokens);
            await client.ResolveCatalogAssetAsync("beeba", "asset-1");
            var error = Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.ResolveCatalogAssetAsync("beeba", "asset-1"));
            Assert.That(error.ErrorCode, Is.EqualTo("catalog_unavailable"));
            Assert.That(client.HasSession, Is.True);
            Assert.That(transport.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void AccountChangeRejectsLateAnonymousSearch()
        {
            var pending = new TaskCompletionSource<BasisSocialHttpResponse>();
            var client = new BasisSocialApiClient(new PendingTransport(pending.Task));
            var searching = client.SearchCatalogAsync(new BasisSocialCatalogQuery());
            client.ClearSession();
            pending.SetResult(new BasisSocialHttpResponse { StatusCode = 200, Body = "{\"items\":[]}" });
            Assert.CatchAsync<OperationCanceledException>(async () => await searching);
        }

        [Test]
        public void CancellationRejectsLateResponseAndInvalidWorldIdsNeverSend()
        {
            var pending = new TaskCompletionSource<BasisSocialHttpResponse>();
            var client = new BasisSocialApiClient(new PendingTransport(pending.Task));
            var cancellation = new CancellationTokenSource();
            var searching = client.SearchCatalogAsync(new BasisSocialCatalogQuery(), cancellation.Token);
            cancellation.Cancel();
            pending.SetResult(new BasisSocialHttpResponse { StatusCode = 200, Body = "{\"items\":[]}" });
            Assert.CatchAsync<OperationCanceledException>(async () => await searching);
            Assert.ThrowsAsync<ArgumentException>(async () => await client.ListWorldAssetsAsync("../private"));
        }

        [Test]
        public async Task PreparingLoadRevalidatesWorldPinAndNeverFallsBackToPreviousCredentials()
        {
            string manifest = "{\"id\":\"" + World + "\",\"externalId\":\"" + World + "\",\"availability\":\"available\",\"versionId\":\"" + World + "\",\"sha256\":\"" + new string('a', 64) + "\",\"sizeBytes\":1024,\"unlockPassword\":\"fixture\",\"downloadUrl\":\"https://catalog.example/api/v1/content/" + World + "/download?version=" + World + "\"}";
            var transport = new FakeTransport();
            transport.Enqueue(200, "[{\"asset\":" + manifest + "}]");
            transport.Enqueue(200, "[{\"asset\":" + manifest.Replace("available", "update_available") + "}]");
            transport.Enqueue(200, "[{\"asset\":" + manifest.Replace("available", "unavailable") + "}]");
            var client = new BasisSocialApiClient(transport);
            var load = await client.PrepareWorldAssetLoadAsync(World, World);
            Assert.That(load.RemoteVersionTag, Is.EqualTo("\"" + new string('a', 64) + "\""));
            Assert.That(load.VersionId, Is.EqualTo(World));
            Assert.That(Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.PrepareWorldAssetLoadAsync(World, World)).ErrorCode, Is.EqualTo("asset_update_available"));
            Assert.That(Assert.ThrowsAsync<BasisSocialApiException>(async () => await client.PrepareWorldAssetLoadAsync(World, World)).ErrorCode, Is.EqualTo("asset_unavailable"));
            Assert.That(transport.Requests.Count, Is.EqualTo(3));
            Assert.That(transport.Requests.TrueForAll(r => r.Method == BasisSocialHttpMethod.Get), Is.True);
        }

        [Test]
        public void LoadPreparationRejectsCredentialBearingOrUnpinnedUrls()
        {
            var transport = new FakeTransport();
            transport.Enqueue(200, "[{\"asset\":" + Asset.Replace("\"externalId\":", "\"id\":\"" + World + "\",\"externalId\":") + "}]");
            var error = Assert.ThrowsAsync<BasisSocialApiException>(async () => await new BasisSocialApiClient(transport).PrepareWorldAssetLoadAsync(World, World));
            Assert.That(error.ErrorCode, Is.EqualTo("invalid_response"));
        }

        private sealed class PendingTransport : IBasisSocialHttpTransport
        {
            private readonly Task<BasisSocialHttpResponse> response;
            public PendingTransport(Task<BasisSocialHttpResponse> response) { this.response = response; }
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default) => response;
        }

        private sealed class FakeTransport : IBasisSocialHttpTransport
        {
            private readonly Queue<BasisSocialHttpResponse> responses = new();
            public readonly List<BasisSocialHttpRequest> Requests = new();
            public void Enqueue(long code, string body) => responses.Enqueue(new BasisSocialHttpResponse { StatusCode = code, Body = body });
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(responses.Dequeue());
            }
        }
    }
}
