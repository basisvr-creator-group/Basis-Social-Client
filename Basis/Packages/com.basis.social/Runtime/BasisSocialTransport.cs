using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Basis.Social
{
    public enum BasisSocialHttpMethod
    {
        Get,
        Post,
        Delete,
        Patch
    }

    public sealed class BasisSocialHttpRequest
    {
        public BasisSocialHttpMethod Method { get; set; }
        public string Path { get; set; }
        public string BodyJson { get; set; }
        public string AccessToken { get; set; }
    }

    public sealed class BasisSocialHttpResponse
    {
        public long StatusCode { get; set; }
        public string Body { get; set; }
        public string TransportError { get; set; }
        public bool IsSuccess => StatusCode >= 200 && StatusCode <= 299;
    }

    public interface IBasisSocialHttpTransport
    {
        Task<BasisSocialHttpResponse> SendAsync(
            BasisSocialHttpRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class BasisSocialUnityWebRequestTransport : IBasisSocialHttpTransport
    {
        private readonly string baseUrl;
        private readonly int timeoutSeconds;

        public BasisSocialUnityWebRequestTransport(string baseUrl, int timeoutSeconds = 20, bool allowLoopbackHttp = false)
        {
            this.baseUrl = BasisSocialRuntime.NormalizeBaseUrl(baseUrl, allowLoopbackHttp);
            this.timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public async Task<BasisSocialHttpResponse> SendAsync(
            BasisSocialHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Path) || !request.Path.StartsWith("/", StringComparison.Ordinal) ||
                request.Path.StartsWith("//", StringComparison.Ordinal) || request.Path.IndexOf('\\') >= 0 ||
                request.Path.IndexOf('#') >= 0)
            {
                throw new ArgumentException("Basis Social request paths must be relative and begin with '/'.", nameof(request));
            }

            string method = request.Method switch
            {
                BasisSocialHttpMethod.Get => UnityWebRequest.kHttpVerbGET,
                BasisSocialHttpMethod.Post => UnityWebRequest.kHttpVerbPOST,
                BasisSocialHttpMethod.Delete => UnityWebRequest.kHttpVerbDELETE,
                BasisSocialHttpMethod.Patch => "PATCH",
                _ => throw new ArgumentOutOfRangeException(nameof(request.Method), request.Method, null)
            };

            using var webRequest = new UnityWebRequest(baseUrl + request.Path, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds,
                redirectLimit = 0
            };

            if (request.BodyJson != null)
            {
                webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.BodyJson));
                webRequest.SetRequestHeader("Content-Type", "application/json");
            }

            webRequest.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(request.AccessToken))
            {
                webRequest.SetRequestHeader("Authorization", "Bearer " + request.AccessToken);
            }

            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    webRequest.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new BasisSocialHttpResponse
            {
                StatusCode = webRequest.responseCode,
                Body = webRequest.downloadHandler?.text,
                TransportError = webRequest.result == UnityWebRequest.Result.ConnectionError ||
                                 webRequest.result == UnityWebRequest.Result.DataProcessingError
                    ? webRequest.error
                    : null
            };
        }
    }
}
