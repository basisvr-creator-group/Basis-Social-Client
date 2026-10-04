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
        public const string DefaultBaseUrl = "";
        public const string AllowLoopbackEnvironmentVariable = "BASIS_SOCIAL_ALLOW_LOOPBACK_HTTP";
        public const string AllowLoopbackArgument = "--basis-social-allow-loopback-http";
        public const string BaseUrlEnvironmentVariable = "BASIS_SOCIAL_BASE_URL";
        public const string BaseUrlArgument = "--basis-social-url";

        public static string BaseUrl { get; private set; }
        public static bool IsConfigured => Client != null;
        public static bool AllowLoopbackHttp { get; private set; }
        public static string ConfigurationError { get; private set; }
        public static BasisSocialApiClient Client { get; private set; }

        public static event Action<BasisSocialApiClient> ClientChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            Client?.ClearSession();
            BaseUrl = null;
            Client = null;
            AllowLoopbackHttp = false;
            ConfigurationError = null;
            TryConfigureEnvironment();
        }

        public static BasisSocialApiClient EnsureClient()
        {
            if (Client == null) TryConfigureEnvironment();
            return Client ?? throw new InvalidOperationException("Configure the Social service endpoint before connecting.");
        }

        /// <summary>
        /// Replaces the transport and session atomically. The previous in-memory tokens are
        /// discarded so an Authorization header can never be replayed to a different host.
        /// </summary>
        public static BasisSocialApiClient Configure(
            string baseUrl,
            IBasisSocialTokenStore tokenStore = null,
            bool allowLoopbackHttp = false)
        {
            string normalizedBaseUrl = NormalizeBaseUrl(baseUrl, allowLoopbackHttp);
            var transport = new BasisSocialUnityWebRequestTransport(normalizedBaseUrl, allowLoopbackHttp: allowLoopbackHttp);
            Client?.ClearSession();
            BaseUrl = normalizedBaseUrl;
            AllowLoopbackHttp = allowLoopbackHttp;
            ConfigurationError = null;
            Client = new BasisSocialApiClient(transport, tokenStore);
            ClientChanged?.Invoke(Client);
            return Client;
        }

        public static string ResolveBaseUrl()
        {
            string environmentValue = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue)) return NormalizeBaseUrl(environmentValue, ResolveAllowLoopback());

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
                    return NormalizeBaseUrl(argument.Substring(prefix.Length), ResolveAllowLoopback());

                if (string.Equals(argument, BaseUrlArgument, StringComparison.Ordinal) && index + 1 < arguments.Length)
                    return NormalizeBaseUrl(arguments[index + 1], ResolveAllowLoopback());
            }

            return DefaultBaseUrl;
        }

        private static bool ResolveAllowLoopback()
        {
            if (Environment.GetEnvironmentVariable(AllowLoopbackEnvironmentVariable) == "1") return true;
            return Array.IndexOf(Environment.GetCommandLineArgs(), AllowLoopbackArgument) >= 0;
        }

        private static void TryConfigureEnvironment()
        {
            try
            {
                AllowLoopbackHttp = ResolveAllowLoopback();
                string endpoint = ResolveBaseUrl();
                if (!string.IsNullOrWhiteSpace(endpoint)) Configure(endpoint, allowLoopbackHttp: AllowLoopbackHttp);
            }
            catch (ArgumentException)
            {
                // User-facing configuration state; never print an invalid URL containing credentials.
                ConfigurationError = "invalid_endpoint";
            }
        }

        public static string NormalizeBaseUrl(string baseUrl, bool allowLoopbackHttp = false)
        {
            if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)) ||
                !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment) || parsed.AbsolutePath != "/")
            {
                throw new ArgumentException(
                    "Use an HTTPS service origin. HTTP loopback requires explicit development mode.",
                    nameof(baseUrl));
            }
            return parsed.GetLeftPart(UriPartial.Authority);
        }
    }
}
