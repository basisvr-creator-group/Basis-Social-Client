using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.Social.UI;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialCatalogSafetyTests
    {
        private const string Package = "http://127.0.0.1:18088/api/v1/content/11111111-1111-4111-8111-111111111111/download?version=22222222-2222-4222-8222-222222222222";

        [Test]
        public void DevelopmentPermitIsExactAndScopedAndReferenceCounted()
        {
            Assert.That(BasisDevelopmentCatalogPermit.Allows(Package), Is.False);
            using (var first = BasisDevelopmentCatalogPermit.Acquire(Package))
            {
                using (BasisDevelopmentCatalogPermit.Acquire(Package))
                {
                    Assert.That(BasisDevelopmentCatalogPermit.Allows(Package), Is.True);
                    Assert.That(BasisDevelopmentCatalogPermit.Allows(Package.Replace(":18088", ":8080")), Is.False);
                    Assert.That(BasisDevelopmentCatalogPermit.Allows(Package.Replace("22222222", "33333333")), Is.False);
                    Assert.That(BasisDevelopmentCatalogPermit.Allows("http://127.0.0.1:18088/admin"), Is.False);
                }
                Assert.That(BasisDevelopmentCatalogPermit.Allows(Package), Is.True);
                first.Dispose(); first.Dispose();
            }
            Assert.That(BasisDevelopmentCatalogPermit.Allows(Package), Is.False);
        }

        [TestCase("http://192.168.1.1/api/v1/content/11111111-1111-4111-8111-111111111111/download?version=22222222-2222-4222-8222-222222222222")]
        [TestCase("http://localhost/admin")]
        [TestCase("http://127.0.0.1/admin")]
        [TestCase(Package + "&access=private")]
        [TestCase(Package + "#fragment")]
        public void DevelopmentPermitCannotAuthorizeOtherLocalResources(string url)
        {
            Assert.Throws<ArgumentException>(() => BasisDevelopmentCatalogPermit.Acquire(url));
        }

        [TestCase("http://example.org/preview.png")]
        [TestCase("https://127.0.0.1/preview.png")]
        [TestCase("https://192.168.0.1/preview.png")]
        [TestCase("https://user:password@example.org/preview.png")]
        [TestCase("file:///etc/passwd")]
        public void PreviewPolicyRejectsPrivateAndCredentialAddresses(string url)
        {
            Assert.That(BasisSocialPreviewImage.IsAllowedUri(new Uri(url)), Is.False);
        }

        [Test]
        public void PreviewChecksDimensionsBeforeNativeDecode()
        {
            byte[] png = new byte[24];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
            png[18] = 4; png[22] = 4;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(png), Is.True);
            png[16] = 1;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(png), Is.False, "Oversized PNG must never reach the native decoder.");
            png[16] = 0; png[22] = 0;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(png), Is.False);
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(new byte[BasisSocialPreviewImage.MaxBytes + 1]), Is.False);
        }

        [Test]
        public void PreviewAcceptsBoundedJpegAndRejectsMalformedSegments()
        {
            var jpeg = new byte[24];
            jpeg[0] = 255; jpeg[1] = 216; jpeg[2] = 255; jpeg[3] = 192;
            jpeg[5] = 17; jpeg[6] = 8; jpeg[7] = 4; jpeg[9] = 4;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(jpeg), Is.True);
            jpeg[7] = 32;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(jpeg), Is.False);
            jpeg[5] = 1;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(jpeg), Is.False);
        }

        [Test]
        public async Task AvatarBridgeRejectsPrivateManifestBeforeTouchingPlayer()
        {
            const string id = "11111111-1111-4111-8111-111111111111";
            var client = new BasisSocialApiClient(new PrivateManifestTransport());
            var manifest = await client.PrepareWorldAssetLoadAsync(id, id);
            BasisSocialApiException failure = null;
            try { await BasisSocialAvatarLoader.WearAsync(manifest, null, CancellationToken.None); }
            catch (BasisSocialApiException error) { failure = error; }
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.ErrorCode, Is.EqualTo("asset_unavailable"), "The private URL must fail before the player-not-ready path.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task WorldWithoutPrimaryAndUsableBuiltInFailsExplicitly(bool hasHolder, bool enabled)
        {
            var previous = BundledContentHolder.Instance;
            UnityEngine.GameObject owner = null;
            try
            {
                BundledContentHolder.Instance = null;
                if (hasHolder)
                {
                    owner = new UnityEngine.GameObject("world-configuration-test");
                    var holder = owner.AddComponent<BundledContentHolder>();
                    holder.UseSceneProvidedHere = enabled;
                    holder.DefaultScene = new BasisLoadableBundle();
                }
                var loader = new BasisSocialWorldLoader();
                BasisSocialApiException failure = null;
                try { await loader.LoadAsync(new BasisSocialApiClient(new EmptyWorldTransport()), "11111111-1111-4111-8111-111111111111", CancellationToken.None); }
                catch (BasisSocialApiException error) { failure = error; }
                Assert.That(failure, Is.Not.Null);
                Assert.That(failure.ErrorCode, Is.EqualTo("asset_unavailable"));
                Assert.That(loader.IsBuiltIn, Is.False, "A no-op must never be reported as a loaded built-in world.");
            }
            finally
            {
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                BundledContentHolder.Instance = previous;
            }
        }

        private sealed class EmptyWorldTransport : IBasisSocialHttpTransport
        {
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new BasisSocialHttpResponse { StatusCode = 200, Body = "[]" });
        }

        [Test]
        public void BuiltInWorldRequiresAnExplicitLocalPackagedScenePath()
        {
            Assert.That(BasisSocialWorldLoader.ResolveBuiltInScenePath(new[] { "Basis.exe" }), Is.Null);
            Assert.That(BasisSocialWorldLoader.ResolveBuiltInScenePath(new[] { "--basis-social-built-in-scene=Packages/com.basis.examples/Scenes/DemoScene.unity" }),
                Is.EqualTo("Packages/com.basis.examples/Scenes/DemoScene.unity"));
        }

        [TestCase("https://evil.example/scene.unity")]
        [TestCase("Assets/../scene.unity")]
        [TestCase("/tmp/scene.unity")]
        [TestCase("Assets/scene.bee")]
        public void BuiltInWorldCannotLoadRemoteOrTraversalPaths(string path)
        {
            Assert.Throws<BasisSocialApiException>(() => BasisSocialWorldLoader.ResolveBuiltInScenePath(
                new[] { "--basis-social-built-in-scene=" + path }));
        }

        private sealed class PrivateManifestTransport : IBasisSocialHttpTransport
        {
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default)
            {
                const string id = "11111111-1111-4111-8111-111111111111";
                return Task.FromResult(new BasisSocialHttpResponse
                {
                    StatusCode = 200,
                    Body = "[{\"asset\":{\"id\":\"" + id + "\",\"externalId\":\"" + id + "\",\"availability\":\"available\",\"versionId\":\"" + id + "\",\"sha256\":\"" + new string('a', 64) + "\",\"sizeBytes\":1024,\"unlockPassword\":\"fixture\",\"downloadUrl\":\"https://127.0.0.1/api/v1/content/" + id + "/download?version=" + id + "\"}}]"
                });
            }
        }

        [Test]
        public void AvatarBridgeRejectsAbsentLiveManifest()
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () => await BasisSocialAvatarLoader.WearAsync(null, null, CancellationToken.None));
        }
    }
}
