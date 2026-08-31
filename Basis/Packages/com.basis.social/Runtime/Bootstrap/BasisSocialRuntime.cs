using System;
using UnityEngine;

namespace Basis.Social
{
    /// <summary>
    /// Package-owned composition root. It deliberately has no scene or prefab dependency,
    /// matching the removable bootstrap pattern used by the other optional Basis providers.
    /// </summary>
    public static class BasisSocialRuntime
    {
        public const string DefaultBaseUrl = "http://127.0.0.1:8080";
        public const string BaseUrlEnvironmentVariable = "BASIS_SOCIAL_BASE_URL";
        public const string BaseUrlArgument = "--basis-social-url";

        public static string BaseUrl { get; private set; }
        public static BasisSocialApiClient Client { get; private set; }

        public static event Action<BasisSocialApiClient> ClientChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            BaseUrl = null;
            Client = null;
            Configure(ResolveBaseUrl());
        }

        public static BasisSocialApiClient EnsureClient()
        {
            if (Client == null) Configure(ResolveBaseUrl());
            return Client;
        }

        /// <summary>
        /// Replaces the transport and session atomically. The previous in-memory tokens are
        /// discarded so an Authorization header can never be replayed to a different host.
        /// </summary>
        public static BasisSocialApiClient Configure(
            string baseUrl,
            IBasisSocialTokenStore tokenStore = null)
        {
            string normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            var transport = new BasisSocialUnityWebRequestTransport(normalizedBaseUrl);
            BaseUrl = normalizedBaseUrl;
            Client = new BasisSocialApiClient(transport, tokenStore);
            ClientChanged?.Invoke(Client);
            return Client;
        }

        public static string ResolveBaseUrl()
        {
            string environmentValue = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue)) return NormalizeBaseUrl(environmentValue);

            string[] arguments;
            try
            {
                arguments = Environment.GetCommandLineArgs();
            }
            catch
            {
                arguments = Array.Empty<string>();
            }

            string prefix = BaseUrlArgument + "=";
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                if (argument.StartsWith(prefix, StringComparison.Ordinal))
                    return NormalizeBaseUrl(argument.Substring(prefix.Length));

                if (string.Equals(argument, BaseUrlArgument, StringComparison.Ordinal) && index + 1 < arguments.Length)
                    return NormalizeBaseUrl(arguments[index + 1]);
            }

            return DefaultBaseUrl;
        }

        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(parsed.UserInfo))
            {
                throw new ArgumentException(
                    "Basis Social base URL must be an absolute HTTP(S) URL without embedded credentials.",
                    nameof(baseUrl));
            }

            return baseUrl.Trim().TrimEnd('/');
        }
    }
}
