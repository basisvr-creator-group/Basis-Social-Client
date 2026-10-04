using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.Common;
using UnityEngine;
using UnityEngine.Networking;

namespace Basis.Social.UI
{
    /// <summary>Anonymous bounded image previews. Caller owns the returned texture; never follows redirects.</summary>
    public static class BasisSocialPreviewImage
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        public const int MaxDimension = 2048;

        public static async Task<Texture2D> LoadAsync(string url, CancellationToken cancellationToken)
        {
            Uri uri = await ValidateAddressAsync(url, cancellationToken);
            using var handler = new BoundedImageHandler();
            using var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbGET, handler, null);
            request.disposeDownloadHandlerOnDispose = false;
            request.redirectLimit = 0;
            request.timeout = 20;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested) { request.Abort(); cancellationToken.ThrowIfCancellationRequested(); }
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                throw new InvalidOperationException("Image download failed.");
            return await DecodeAsync(handler.Bytes, cancellationToken);
        }

        /// <summary>Decodes bounded previews. WebP CPU work runs off-thread; texture allocation stays on the caller's Unity context.</summary>
        public static async Task<Texture2D> DecodeAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length > MaxBytes || !HasSafeDimensions(bytes))
                throw new InvalidOperationException("Image dimensions or format are unsupported.");
            Texture2D texture;
            if (IsWebP(bytes))
            {
                var decoded = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WebP.Texture2DExt.GetWebPDimensions(bytes, out int width, out int height);
                    if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
                        throw new InvalidOperationException("Image dimensions are unsupported.");
                    byte[] rgba = WebP.Texture2DExt.LoadRGBAFromWebP(bytes, ref width, ref height, false, out var error);
                    if (error != WebP.Error.Success || rgba == null || rgba.Length != checked(width * height * 4))
                        throw new InvalidOperationException("Image could not be decoded.");
                    cancellationToken.ThrowIfCancellationRequested();
                    return (width, height, rgba);
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                texture = new Texture2D(decoded.width, decoded.height, TextureFormat.RGBA32, false);
                try
                {
                    texture.LoadRawTextureData(decoded.rgba);
                    texture.Apply(false, true);
                    return texture;
                }
                catch { UnityEngine.Object.Destroy(texture); throw; }
            }
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, true))
                    throw new InvalidOperationException("Image could not be decoded.");
                return texture;
            }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }

        /// <summary>Shared anonymous image/package address policy, including strict DNS checks in the Editor.</summary>
        public static async Task<Uri> ValidateAddressAsync(string url, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedUri(uri))
                throw new InvalidOperationException("Unsupported image address.");
            if (!IsDevelopmentLoopback(uri))
            {
                string denied = await BasisUrlSecurity.ValidateResolvedHostAsync(uri.AbsoluteUri, allowLoopback: false);
                if (denied != null) throw new InvalidOperationException("Image host is unavailable.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return uri;
        }

        public static bool IsAllowedUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (IsDevelopmentLoopback(uri)) return true;
            return uri.Scheme == Uri.UriSchemeHttps && !BasisUrlSecurity.IsBlockedHost(uri.Host, false, out _);
        }

        private static bool IsDevelopmentLoopback(Uri uri)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return BasisSocialRuntime.AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp &&
                IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
#else
            return false;
#endif
        }

        // Inspect image dimensions before invoking full decoding, to bound decoded allocation.
        public static bool HasSafeDimensions(byte[] data)
        {
            if (data == null || data.Length < 24 || data.Length > MaxBytes) return false;
            if (IsWebP(data))
            {
                // BeeBa publishes still images. Reject animated containers and inconsistent RIFF lengths.
                uint declaredSize = (uint)data[4] | ((uint)data[5] << 8) | ((uint)data[6] << 16) | ((uint)data[7] << 24);
                if (declaredSize != data.Length - 8) return false;
                if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'X' && (data[20] & 2) != 0) return false;
                try
                {
                    WebP.Texture2DExt.GetWebPDimensions(data, out int width, out int height);
                    return width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;
                }
                catch (Exception error) when (!(error is DllNotFoundException) && !(error is EntryPointNotFoundException) && !(error is BadImageFormatException)) { return false; }
            }
            if (data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 && data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10)
            {
                if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return false;
                uint width = Big32(data, 16), height = Big32(data, 20);
                return width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;
            }
            if (data[0] != 255 || data[1] != 216) return false;
            int offset = 2;
            while (offset + 4 <= data.Length)
            {
                if (data[offset++] != 255) return false;
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset + 3 > data.Length) return false;
                int marker = data[offset++];
                if (marker == 217 || marker == 218) return false;
                if (marker == 1 || marker >= 208 && marker <= 215) continue;
                int length = (data[offset] << 8) | data[offset + 1];
                if (length < 2 || offset + length > data.Length) return false;
                if (marker == 192 || marker == 193 || marker == 194)
                {
                    if (length < 8) return false;
                    int height = (data[offset + 3] << 8) | data[offset + 4];
                    int width = (data[offset + 5] << 8) | data[offset + 6];
                    return width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension;
                }
                offset += length;
            }
            return false;
        }
        private static bool IsWebP(byte[] data) => data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';
        private static uint Big32(byte[] data, int offset) => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

        private sealed class BoundedImageHandler : DownloadHandlerScript
        {
            private readonly MemoryStream bufferStream = new MemoryStream();
            private bool tooLarge;
            public byte[] Bytes => bufferStream.ToArray();
            public BoundedImageHandler() : base(new byte[16 * 1024]) { }
            protected override void ReceiveContentLengthHeader(ulong contentLength) => tooLarge = contentLength > MaxBytes;
            protected override bool ReceiveData(byte[] bytes, int length)
            {
                if (tooLarge || bytes == null || length < 0 || bufferStream.Length + length > MaxBytes) return false;
                bufferStream.Write(bytes, 0, length); return true;
            }
        }
    }
}
