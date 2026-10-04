using Basis.BasisUI;
using Basis.Scripts.Networking;
using System;
using UnityEngine;

namespace Basis.Social.UI
{
    /// <summary>Untrusted OS links select an instance for review; authentication and explicit Join stay in the native UI.</summary>
    public static class BasisSocialDeepLink
    {
        public static string PendingInstanceId { get; private set; }
        public static event Action Requested;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            Application.deepLinkActivated -= Receive;
            Application.deepLinkActivated += Receive;
            BasisNetworkManagement.OnIstanceCreated -= ShowPending;
            BasisNetworkManagement.OnIstanceCreated += ShowPending;
            if (!string.IsNullOrEmpty(Application.absoluteURL)) Receive(Application.absoluteURL);
            foreach (string arg in Environment.GetCommandLineArgs())
                if (TryParse(arg, out _)) { Receive(arg); break; }
        }

        public static bool TryParse(string value, out string instanceId)
        {
            instanceId = null;
            if (value == null || value.Length > 80 || !Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                uri.Scheme != BasisDeepLinkProvider.SocialDeepLinkScheme || uri.Host != "join" || uri.Port != -1 || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.Length != 37 ||
                !Guid.TryParseExact(uri.AbsolutePath.Substring(1), "D", out Guid id) || id == Guid.Empty) return false;
            instanceId = id.ToString("D");
            return true;
        }

        public static void Clear() => PendingInstanceId = null;
        private static void Receive(string url)
        {
            if (!TryParse(url, out string id)) return;
            PendingInstanceId = id;
            Requested?.Invoke();
            ShowPending();
        }
        private static void ShowPending()
        {
            if (PendingInstanceId != null && BasisNetworkManagement.IsInitialized && BasisMainMenu.ActiveMenuTitle != "Social") BasisMainMenu.OpenWithProvider("Social");
        }
    }
}
