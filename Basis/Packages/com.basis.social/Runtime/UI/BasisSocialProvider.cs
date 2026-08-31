using Basis.BasisUI;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social.UI
{
    /// <summary>
    /// Basis-native Social entry point. All controls come from the shared Panel* system,
    /// so active Basis themes, desktop input, and VR input are inherited automatically.
    /// </summary>
    public sealed class BasisSocialProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new BasisSocialProvider());
        }

        public override string Title => "Social";
        public override string IconAddress => AddressableAssets.Sprites.People;
        public override int Order => 40;
        public override bool Hidden => false;

        private BasisMenuPanel panel;
        private PanelTextField endpointField;
        private PanelTextField loginField;
        private PanelPasswordField passwordField;
        private PanelButton loginButton;
        private PanelButton refreshButton;
        private PanelButton logoutButton;
        private PanelElementDescriptor statusDescriptor;
        private PanelElementDescriptor loginGroup;
        private PanelElementDescriptor accountGroup;
        private CancellationTokenSource requestCancellation;
        private bool busy;
        private int panelGeneration;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            panelGeneration++;
            panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            panel.OnInstanceReleased += OnPanelClosed;

            RectTransform root = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical,
                root);
            BuildPanel(scroll.ContentParent);
            ApplySessionState(BasisSocialRuntime.EnsureClient().CurrentUser);
        }

        public override void OnReleaseEvent()
        {
            OnPanelClosed();
        }

        private void BuildPanel(RectTransform root)
        {
            PanelElementDescriptor connectionGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group,
                root);
            connectionGroup.SetTitle("Basis Social service");
            connectionGroup.SetDescription(
                "The endpoint is process-local. Changing it discards the current in-memory session.");

            endpointField = PanelTextField.CreateNewEntry(connectionGroup.ContentParent);
            endpointField.Descriptor.SetTitle("Service endpoint");
            endpointField.Descriptor.SetDescription("Absolute HTTP(S) URL; production services should use HTTPS.");
            endpointField.SetValueWithoutNotify(BasisSocialRuntime.BaseUrl ?? BasisSocialRuntime.DefaultBaseUrl);
            if (endpointField._inputField != null)
                endpointField._inputField.SetTextWithoutNotify(endpointField.Value);
            endpointField.SetValidator(ValidateEndpoint, false);

            statusDescriptor = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group,
                root);
            statusDescriptor.SetTitle("Not signed in");
            statusDescriptor.SetDescription("Sign in to load your Basis Social profile.");

            loginGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group,
                root);
            loginGroup.SetTitle("Sign in");
            loginGroup.SetDescription("Credentials are sent only to the endpoint shown above and are never serialized.");

            loginField = PanelTextField.CreateNewEntry(loginGroup.ContentParent);
            loginField.Descriptor.SetTitle("Email or username");
            loginField.SetRequired("Enter your email or username.", false);

            passwordField = PanelPasswordField.CreateNewEntry(loginGroup.ContentParent);
            passwordField.Descriptor.SetTitle("Password");
            passwordField.SetPassword(string.Empty);
            passwordField.OnSubmit += ignored => { _ = LoginAsync(); };

            loginButton = PanelButton.CreateNew(loginGroup.ContentParent);
            loginButton.Descriptor.SetTitle("Sign in");
            loginButton.Descriptor.SetDescription("Create an in-memory Basis Social session.");
            loginButton.OnClicked += () => _ = LoginAsync();

            accountGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group,
                root);
            accountGroup.SetTitle("Account");

            refreshButton = PanelButton.CreateNew(accountGroup.ContentParent);
            refreshButton.Descriptor.SetTitle("Refresh profile");
            refreshButton.OnClicked += () => _ = RefreshProfileAsync();

            logoutButton = PanelButton.CreateNew(accountGroup.ContentParent);
            logoutButton.Descriptor.SetTitle("Sign out");
            logoutButton.Descriptor.SetDescription("Revoke the session and erase local in-memory credentials.");
            logoutButton.OnClicked += () => _ = LogoutAsync();
        }

        private async Task LoginAsync()
        {
            if (busy || panel == null) return;
            int generation = panelGeneration;

            bool endpointValid = endpointField != null && endpointField.Validate();
            bool loginValid = loginField != null && loginField.Validate();
            string password = passwordField?.Password;
            if (!endpointValid || !loginValid || string.IsNullOrEmpty(password))
            {
                SetStatus("Sign-in details required", "Enter a valid endpoint, account name, and password.");
                return;
            }

            SetBusy(true, "Signing in…");
            var cancellation = new CancellationTokenSource();
            requestCancellation = cancellation;
            try
            {
                string endpoint = endpointField._inputField != null
                    ? endpointField._inputField.text
                    : endpointField.Value;
                if (!string.Equals(
                        BasisSocialRuntime.NormalizeBaseUrl(endpoint),
                        BasisSocialRuntime.BaseUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    BasisSocialRuntime.Configure(endpoint);
                }

                BasisSocialUser user = await BasisSocialRuntime.Client.LoginAsync(
                    loginField._inputField != null ? loginField._inputField.text : loginField.Value,
                    password,
                    cancellation.Token);
                if (generation == panelGeneration)
                {
                    passwordField?.SetPassword(string.Empty);
                    ApplySessionState(user);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (generation == panelGeneration) ShowError(exception);
            }
            finally
            {
                if (ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
                cancellation.Dispose();
                if (generation == panelGeneration) SetBusy(false);
            }
        }

        private async Task RefreshProfileAsync()
        {
            if (busy || panel == null) return;
            int generation = panelGeneration;
            SetBusy(true, "Refreshing profile…");
            var cancellation = new CancellationTokenSource();
            requestCancellation = cancellation;
            try
            {
                BasisSocialUser user = await BasisSocialRuntime.EnsureClient()
                    .GetMeAsync(cancellation.Token);
                if (generation == panelGeneration) ApplySessionState(user);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (generation == panelGeneration) ShowError(exception);
            }
            finally
            {
                if (ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
                cancellation.Dispose();
                if (generation == panelGeneration) SetBusy(false);
            }
        }

        private async Task LogoutAsync()
        {
            if (busy || panel == null) return;
            int generation = panelGeneration;
            SetBusy(true, "Signing out…");
            var cancellation = new CancellationTokenSource();
            requestCancellation = cancellation;
            try
            {
                await BasisSocialRuntime.EnsureClient().LogoutAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (generation == panelGeneration) ShowError(exception);
            }
            finally
            {
                if (ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
                cancellation.Dispose();
                if (generation == panelGeneration)
                {
                    ApplySessionState(BasisSocialRuntime.EnsureClient().CurrentUser);
                    SetBusy(false);
                }
            }
        }

        private void ApplySessionState(BasisSocialUser user)
        {
            if (panel == null) return;

            bool signedIn = user != null;
            if (loginGroup != null) loginGroup.gameObject.SetActive(!signedIn);
            if (accountGroup != null) accountGroup.gameObject.SetActive(signedIn);

            if (!signedIn)
            {
                SetStatus("Not signed in", "Sign in to load your Basis Social profile.");
                return;
            }

            BasisSocialProfile profile = user.EffectiveProfile;
            string displayName = FirstNonEmpty(profile?.displayName, user.username, user.acct, "Basis Social user");
            string account = FirstNonEmpty(user.acct, user.username, user.email, user.id);
            string description = string.IsNullOrEmpty(account)
                ? "Authenticated with Basis Social."
                : account;
            if (!string.IsNullOrWhiteSpace(profile?.statusText))
                description += "\n" + profile.statusText;
            SetStatus(displayName, description);
        }

        private void SetBusy(bool value, string message = null)
        {
            busy = value;
            loginButton?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            refreshButton?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            logoutButton?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            endpointField?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            loginField?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            passwordField?.SetInteractable(!value, value ? "A Basis Social request is in progress." : null);
            if (value && !string.IsNullOrEmpty(message)) SetStatus(message, "Please wait.");
        }

        private void ShowError(Exception exception)
        {
            if (exception is BasisSocialApiException apiException)
            {
                SetStatus("Basis Social request failed", apiException.Message);
                return;
            }
            SetStatus("Basis Social request failed", exception.Message);
        }

        private void SetStatus(string title, string description)
        {
            if (statusDescriptor == null) return;
            statusDescriptor.SetTitle(title);
            statusDescriptor.SetDescription(description);
        }

        private void OnPanelClosed()
        {
            panelGeneration++;
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            requestCancellation = null;
            busy = false;
            panel = null;
            endpointField = null;
            loginField = null;
            passwordField = null;
            loginButton = null;
            refreshButton = null;
            logoutButton = null;
            statusDescriptor = null;
            loginGroup = null;
            accountGroup = null;
        }

        private static string ValidateEndpoint(string value)
        {
            try
            {
                BasisSocialRuntime.NormalizeBaseUrl(value);
                return null;
            }
            catch (ArgumentException)
            {
                return "Enter an absolute HTTP(S) URL without embedded credentials.";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }
    }
}
