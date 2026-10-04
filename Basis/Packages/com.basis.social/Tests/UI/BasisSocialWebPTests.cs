using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Basis.Social.UI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialWebPTests
    {
        // NUnit's synchronous ThrowsAsync/CatchAsync blocks UnitySynchronizationContext when a
        // decoder continuation returns from Task.Run. Let the Editor pump while genuinely awaiting.
        private static async Task<Exception> DecodeFailureAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            Texture2D unexpected = null;
            try
            {
                unexpected = await BasisSocialPreviewImage.DecodeAsync(bytes, cancellationToken);
                return null;
            }
            catch (Exception failure) { return failure; }
            finally { if (unexpected != null) UnityEngine.Object.DestroyImmediate(unexpected); }
        }

        private static byte[] Fixture() => File.ReadAllBytes("Packages/com.basis.social/Tests/UI/Fixtures/beeba-preview.webp.bytes");

        [Test]
        public void PinnedNativeDecoderIncludesSecurityFixes()
        {
            Assert.That(WebP.Info.GetDecoderVersion(), Is.EqualTo("1.6.0"));
        }

        [Test]
        public async Task ActualBeeBaWebPDecodesWithCorrectOrientation()
        {
            byte[] bytes = Fixture();
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(bytes), Is.True);
            Texture2D texture = await BasisSocialPreviewImage.DecodeAsync(bytes);
            RenderTexture target = null;
            Texture2D readable = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                Assert.That(texture.width, Is.EqualTo(32));
                Assert.That(texture.height, Is.EqualTo(32));
                Assert.That(texture.isReadable, Is.False, "CPU storage is released after upload.");
                target = RenderTexture.GetTemporary(32, 32);
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                readable.Apply();
                Color top = readable.GetPixel(16, 24), bottom = readable.GetPixel(16, 8);
                Assert.That(top.r, Is.GreaterThan(top.g + 0.3f), "BeeBa's red top must remain at the top.");
                Assert.That(bottom.g, Is.GreaterThan(bottom.r + 0.3f), "BeeBa's green bottom must remain at the bottom.");
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public async Task OversizedWebPIsRejectedBeforePixelAllocation()
        {
            byte[] bytes = Fixture();
            bytes[26] = 0; bytes[27] = 16; // VP8 frame width: 4096.
            WebP.Texture2DExt.GetWebPDimensions(bytes, out int width, out _);
            Assert.That(width, Is.EqualTo(4096));
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(bytes), Is.False);
            Assert.That(await DecodeFailureAsync(bytes), Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task TruncatedAndAnimatedWebPAreRejected()
        {
            byte[] bytes = Fixture();
            Array.Resize(ref bytes, 48);
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(bytes), Is.False, "RIFF length does not match the received payload.");
            Assert.That(await DecodeFailureAsync(bytes), Is.InstanceOf<InvalidOperationException>());
            bytes = new byte[30];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            bytes[4] = 22;
            System.Text.Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(bytes, 8);
            bytes[16] = 10; bytes[20] = 2;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(bytes), Is.False, "Animated previews are not decoded.");
        }

        [Test]
        public async Task CorruptWebPPayloadFailsWithoutCreatingTexture()
        {
            byte[] bytes = Fixture();
            Array.Resize(ref bytes, 80);
            // Keep the RIFF/VP8 lengths internally consistent, but truncate the VP8 token stream.
            // Verified against native libwebp 1.6.0: GetInfo succeeds; full decode fails.
            bytes[4] = 72; bytes[5] = bytes[6] = bytes[7] = 0;
            bytes[16] = 60; bytes[17] = bytes[18] = bytes[19] = 0;
            Assert.That(BasisSocialPreviewImage.HasSafeDimensions(bytes), Is.True, "A valid header does not prove the compressed payload is valid.");
            Assert.That(await DecodeFailureAsync(bytes), Is.Not.Null);
        }

        [Test]
        public async Task CancelledDecodeNeverCreatesTexture()
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.That(await DecodeFailureAsync(Fixture(), cancellation.Token), Is.InstanceOf<OperationCanceledException>());
        }
    }
}
