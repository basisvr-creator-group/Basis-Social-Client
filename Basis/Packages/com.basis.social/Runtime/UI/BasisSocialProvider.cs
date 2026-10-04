using Basis.BasisUI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static Basis.Social.UI.BasisSocialUIStrings;

namespace Basis.Social.UI
{
    /// <summary>Social UI composed from the same themed controls as the desktop and VR menus.</summary>
    public sealed class BasisSocialProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu() => BasisMenuBase<BasisMainMenu>.AddProvider(new BasisSocialProvider());
        public override string Title => "Social";
        public override string IconAddress => AddressableAssets.Sprites.People;
        public override int Order => 40;
        public override bool Hidden => false;

        private BasisMenuPanel panel;
        private RectTransform root;
        private BasisSocialCatalogPanel catalog;
        private BasisSocialCommunityPanel community;
        private BasisSocialApiClient client;
        private BasisSocialConnectionController connection;
        private CancellationTokenSource request;
        private PanelElementDescriptor status, account, signIn, legacy, settings;
        private PanelTextField endpoint, login, bio, statusText;
        private PanelPasswordField password;
        private PanelImage profileImage;
        private Texture2D profileTexture;
        private Sprite profileSprite;
        private CancellationTokenSource imageRequest;
        private string displayedUserId, serverBio, serverStatus, displayedImageUrl;
        private PanelButton connect, copyCode, openBrowser, cancel, save, refresh, logout, applyEndpoint, legacyLogin;
        private readonly List<Action> translations = new();
        private bool busy;
        private int generation;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) { BasisMainMenu.CloseActivePanel(); return; }
            generation++;
            panel = BasisMainMenu.CreateActiveMenu(BasisMenuPanel.PanelData.Standard(Title), BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            panel.OnInstanceReleased += OnPanelClosed;
            var scroll = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, panel.Descriptor.ContentParent);
            // This native prefab leaves clipping to its caller (as in the library details dialog).
            var viewport = scroll.GetComponent<UnityEngine.UI.ScrollRect>().viewport;
            if (!viewport.TryGetComponent<UnityEngine.UI.RectMask2D>(out _)) viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            root = scroll.ContentParent;
            BuildPanel();
            BasisSocialRuntime.ClientChanged += OnClientChanged;
            BasisLocalization.OnLanguageChanged += Translate;
            OnClientChanged(BasisSocialRuntime.Client);
        }

        public override void OnReleaseEvent() => OnPanelClosed();

        private void BuildPanel()
        {
            status = Group(root, "Account", "Аккаунт");
            Button(root, "Connection settings", "Настройки подключения", () => Toggle(settings));
            settings = Group(root, "Social service", "Сервис Social", "Changes apply for this run and sign you out. Use the address provided by your service administrator.", "Изменения действуют до перезапуска и сбрасывают вход. Используйте адрес, выданный администратором сервиса.");
            endpoint = Field(settings.ContentParent, "Service address", "Адрес сервиса", BasisSocialRuntime.BaseUrl ?? "", 2048);
            endpoint.SetValidator(value => { try { BasisSocialRuntime.NormalizeBaseUrl(value, BasisSocialRuntime.AllowLoopbackHttp); return null; } catch (ArgumentException) { return Text("Enter an HTTPS service address without a path.", "Введите HTTPS-адрес сервиса без пути."); } }, false);
            applyEndpoint = Button(settings.ContentParent, "Apply address and sign out", "Применить адрес и выйти", ApplyEndpoint);
            if (BasisSocialRuntime.AllowLoopbackHttp)
                Group(settings.ContentParent, "Local development mode", "Локальный режим разработки",
                    "HTTP is permitted only for a loopback address in this run.", "В этом запуске HTTP разрешён только для адреса текущего устройства.");
            settings.SetActive(!BasisSocialRuntime.IsConfigured);

            signIn = Group(root, "Sign in with BeeBa", "Войти через BeeBa", "Approve this device on BeeBa. After restarting Basis, sign in again.", "Подтвердите это устройство на BeeBa. После перезапуска Basis потребуется войти снова.");
            connect = Button(signIn.ContentParent, "Sign in with BeeBa", "Войти через BeeBa", () => _ = ConnectAsync());
            copyCode = Button(signIn.ContentParent, "Copy code", "Скопировать код", CopyApprovalCode);
            openBrowser = Button(signIn.ContentParent, "Open BeeBa", "Открыть BeeBa", () => { if (!string.IsNullOrEmpty(connection?.VerificationUri)) Application.OpenURL(connection.VerificationUri); });
            cancel = Button(signIn.ContentParent, "Cancel sign-in", "Отменить вход", () => connection?.Cancel());
            Button(signIn.ContentParent, "Existing Social account", "Существующий аккаунт Social", () => Toggle(legacy));
            legacy = Group(signIn.ContentParent, "Social account", "Аккаунт Social", "Use this only for an existing Social password account. For a BeeBa account, use the button above.", "Для существующего аккаунта Social с паролем. Для аккаунта BeeBa используйте кнопку выше.");
            login = Field(legacy.ContentParent, "Email or username", "Почта или имя пользователя", "", 254);
            password = PanelPasswordField.CreateNewEntry(legacy.ContentParent);
            Label(password.Descriptor, "Password", "Пароль", placeholder: password._placeholderField);
            password.SetPassword("");
            password.OnSubmit += ignored => _ = LegacyLoginAsync();
            legacyLogin = Button(legacy.ContentParent, "Sign in", "Войти", () => _ = LegacyLoginAsync());
            legacy.SetActive(false);

            account = Group(root, "Profile", "Профиль", "Your name and image are managed on BeeBa. Your status and bio are shared with Social.", "Имя и изображение изменяются на BeeBa. Статус и описание доступны в Social.");
            profileImage = PanelImage.CreateNew(account.ContentParent);
            profileImage.SetHeight(128);
            profileImage.Image.type = UnityEngine.UI.Image.Type.Simple;
            profileImage.Image.preserveAspect = true;
            profileImage.Image.raycastTarget = false;
            profileImage.gameObject.SetActive(false);
            statusText = Field(account.ContentParent, "Status", "Статус", "", 500);
            bio = Field(account.ContentParent, "About me", "О себе", "", 4000);
            save = Button(account.ContentParent, "Save profile", "Сохранить профиль", () => _ = RequestAsync(async token => { await client.UpdateProfileAsync(Value(bio), Value(statusText), token); }, () => SetStatus(Text("Profile saved", "Профиль сохранён"), Text("Your changes are available in Social.", "Изменения доступны в Social."))));
            refresh = Button(account.ContentParent, "Refresh profile", "Обновить профиль", () => _ = RequestAsync(async token => { await client.GetMeAsync(token); }));
            logout = Button(account.ContentParent, "Sign out on this device", "Выйти на этом устройстве", () => _ = RequestAsync(token => client.LogoutCurrentAsync(token)));
            community = new BasisSocialCommunityPanel(root);
            catalog = new BasisSocialCatalogPanel(root);
        }

        private void ApplyEndpoint()
        {
            if (busy || !endpoint.Validate()) return;
            try { BasisSocialRuntime.Configure(Value(endpoint), allowLoopbackHttp: BasisSocialRuntime.AllowLoopbackHttp); settings.SetActive(false); Rebuild(); }
            catch (ArgumentException) { SetStatus(Text("Invalid service address", "Неверный адрес сервиса"), Text("Check the address and try again.", "Проверьте адрес и повторите попытку.")); }
        }

        private void OnClientChanged(BasisSocialApiClient value)
        {
            generation++;
            request?.Cancel();
            if (client != null) client.SessionChanged -= OnSessionChanged;
            if (connection != null) { connection.Changed -= RenderConnection; connection.Dispose(); }
            client = value;
            displayedUserId = null; serverBio = null; serverStatus = null;
            ClearProfileImage(); displayedImageUrl = null;
            connection = value == null ? null : new BasisSocialConnectionController(value, BasisSocialRuntime.AllowLoopbackHttp);
            if (connection != null) connection.Changed += RenderConnection;
            if (client != null) client.SessionChanged += OnSessionChanged;
            busy = false;
            OnSessionChanged(client?.CurrentUser);
            if (client?.CurrentUser == null) { catalog?.ReloadConnection(); community?.ReloadConnection(); }
        }

        private void OnSessionChanged(BasisSocialUser user)
        {
            if (panel == null) return;
            bool signedIn = user != null;
            bool identityChanged = !string.Equals(displayedUserId, user?.id, StringComparison.Ordinal);
            bool keepDraft = !identityChanged && (Value(bio) != (serverBio ?? "") || Value(statusText) != (serverStatus ?? ""));
            displayedUserId = user?.id;
            account.SetActive(signedIn);
            signIn.SetActive(!signedIn);
            if (signedIn)
            {
                var profile = user.EffectiveProfile;
                serverBio = profile?.bio ?? ""; serverStatus = profile?.statusText ?? "";
                if (!keepDraft) { SetField(bio, serverBio); SetField(statusText, serverStatus); }
                if (!string.Equals(displayedImageUrl, profile?.avatarUrl, StringComparison.Ordinal))
                {
                    ClearProfileImage(); displayedImageUrl = profile?.avatarUrl;
                    if (!string.IsNullOrWhiteSpace(displayedImageUrl)) _ = LoadProfileImageAsync(displayedImageUrl);
                }
                SetStatus(string.IsNullOrWhiteSpace(profile?.displayName) ? user.username : profile.displayName, user.acct ?? user.username);
                password.SetPassword("");
            }
            else
            {
                serverBio = null; serverStatus = null;
                SetField(bio, ""); SetField(statusText, "");
                ClearProfileImage(); displayedImageUrl = null;
                RenderConnection();
            }
            if (identityChanged) { catalog?.ReloadConnection(); community?.ReloadConnection(); }
            SetBusy(busy);
            Rebuild();
        }

        private async Task ConnectAsync()
        {
            if (busy || connection == null) return;
            int current = generation;
            var attempt = connection;
            SetBusy(true);
            try { await attempt.StartAsync(); }
            finally { if (current == generation) { SetBusy(false); RenderConnection(); } }
        }

        private Task LegacyLoginAsync()
        {
            if (string.IsNullOrWhiteSpace(Value(login)) || string.IsNullOrEmpty(password.Password))
            { SetStatus(Text("Enter your account details", "Введите данные аккаунта"), Text("Email or username and password are required.", "Укажите почту или имя пользователя и пароль.")); return Task.CompletedTask; }
            return RequestAsync(async token => { string secret = password.Password; password.SetPassword(""); await client.LoginAsync(Value(login), secret, token); });
        }

        private async Task RequestAsync(Func<CancellationToken, Task> action, Action success = null)
        {
            if (busy || client == null || panel == null) return;
            int current = generation;
            using var source = new CancellationTokenSource();
            request = source;
            SetBusy(true);
            SetStatus(Text("Please wait…", "Подождите…"), "");
            try { await action(source.Token); if (current == generation && !source.IsCancellationRequested) success?.Invoke(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (current == generation) SetStatus(Text("Request failed", "Не удалось выполнить запрос"), Error(error)); }
            finally { if (ReferenceEquals(request, source)) request = null; if (current == generation) SetBusy(false); }
        }

        private void RenderConnection()
        {
            if (panel == null) return;
            bool waiting = connection?.State == BasisSocialConnectionState.AwaitingApproval || connection?.State == BasisSocialConnectionState.Slowed;
            copyCode.gameObject.SetActive(waiting);
            if (!waiting) copyCode.Descriptor.SetTitle(Text("Copy code", "Скопировать код"));
            openBrowser.gameObject.SetActive(waiting);
            cancel.gameObject.SetActive(waiting || connection?.State == BasisSocialConnectionState.Starting);
            if (client?.CurrentUser != null) return;
            if (client == null)
                SetStatus(Text("Service not configured", "Сервис не настроен"), Text("Open connection settings and enter your service address.", "Откройте настройки подключения и укажите адрес сервиса."));
            else if (waiting)
                SetStatus(Text("Confirm on BeeBa: ", "Подтвердите на BeeBa: ") + connection.UserCode,
                    Text("Open BeeBa and enter this code. Keep this window open.", "Откройте BeeBa и введите этот код. Оставьте это окно открытым.") + "\n" +
                    new Uri(connection.VerificationUri).GetLeftPart(UriPartial.Authority) + "\n" +
                    Text("Valid until ", "Действует до ") + connection.ExpiresAt?.ToLocalTime().ToString("HH:mm") +
                    (connection.State == BasisSocialConnectionState.Slowed ? "\n" + Text("Still waiting. Checking less often at the service's request.", "Ожидаем подтверждение. По запросу сервиса проверяем реже.") : ""));
            else switch (connection?.State)
            {
                case BasisSocialConnectionState.Starting: SetStatus(Text("Preparing sign-in…", "Подготовка входа…"), ""); break;
                case BasisSocialConnectionState.Denied: SetStatus(Text("Sign-in declined", "Вход отклонён"), Text("Start again when you are ready.", "Повторите вход, когда будете готовы.")); break;
                case BasisSocialConnectionState.Expired: SetStatus(Text("Code expired", "Код истёк"), Text("Sign in again to get a new code.", "Начните вход заново, чтобы получить новый код.")); break;
                case BasisSocialConnectionState.Cancelled: SetStatus(Text("Sign-in cancelled", "Вход отменён"), ""); break;
                case BasisSocialConnectionState.Failed: SetStatus(Text("Sign-in failed", "Не удалось войти"), Error(connection.Error)); break;
                default: SetStatus(Text("Not signed in", "Вы не вошли"), Text("Sign in to use your Social profile and wear catalog avatars.", "Войдите, чтобы использовать профиль Social и аватары из каталога.")); break;
            }
            Rebuild();
        }

        private void CopyApprovalCode()
        {
            // Copy only the displayed, live approval code, never the device credential or tokens.
            if (connection == null ||
                (connection.State != BasisSocialConnectionState.AwaitingApproval && connection.State != BasisSocialConnectionState.Slowed) ||
                !connection.ExpiresAt.HasValue || connection.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrEmpty(connection.UserCode)) return;
            GUIUtility.systemCopyBuffer = connection.UserCode;
            copyCode?.Descriptor.SetTitle(Text("Code copied", "Код скопирован"));
        }

        private void SetBusy(bool value)
        {
            busy = value;
            bool configured = client != null;
            connect?.SetInteractable(!value && configured);
            legacyLogin?.SetInteractable(!value && configured);
            save?.SetInteractable(!value); refresh?.SetInteractable(!value); logout?.SetInteractable(!value);
            applyEndpoint?.SetInteractable(!value); endpoint?.SetInteractable(!value);
            login?.SetInteractable(!value); password?.SetInteractable(!value);
            bio?.SetInteractable(!value); statusText?.SetInteractable(!value);
        }

        private void Translate()
        {
            foreach (var translate in translations) translate();
            if (client?.CurrentUser != null)
            {
                var user = client.CurrentUser;
                SetStatus(string.IsNullOrWhiteSpace(user.EffectiveProfile?.displayName) ? user.username : user.EffectiveProfile.displayName, user.acct ?? user.username);
            }
            else RenderConnection();
            Rebuild();
        }

        private async Task LoadProfileImageAsync(string url)
        {
            int current = generation;
            using var source = new CancellationTokenSource(); imageRequest = source;
            try
            {
                var texture = await BasisSocialPreviewImage.LoadAsync(url, source.Token);
                if (source.IsCancellationRequested || current != generation || panel == null || profileImage == null)
                { UnityEngine.Object.Destroy(texture); return; }
                profileTexture = texture;
                profileSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                profileImage.SetIcon(profileSprite, false); profileImage.gameObject.SetActive(true);
                Rebuild();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!source.IsCancellationRequested && current == generation && profileImage != null)
                {
                    profileImage.gameObject.SetActive(true);
                    profileImage.Descriptor.SetDescription(Text("Profile image unavailable", "Изображение профиля недоступно"));
                    Rebuild();
                }
            }
            finally { if (ReferenceEquals(imageRequest, source)) imageRequest = null; }
        }

        private void ClearProfileImage()
        {
            imageRequest?.Cancel(); imageRequest = null;
            if (profileImage != null)
            {
                profileImage.gameObject.SetActive(false);
                profileImage.SetIcon((Sprite)null, false);
                profileImage.Descriptor.SetDescription("");
            }
            if (profileSprite != null) UnityEngine.Object.Destroy(profileSprite);
            if (profileTexture != null) UnityEngine.Object.Destroy(profileTexture);
            profileSprite = null; profileTexture = null;
        }

        private void SetStatus(string title, string description) { if (status == null) return; status.SetTitle(title); status.SetDescription(description); Rebuild(); }
        private void Toggle(PanelElementDescriptor group) { group.SetActive(!group.gameObject.activeSelf); Rebuild(); }
        private void Rebuild() { if (root != null) PanelElementDescriptor.RebuildLayoutChain(root, root); }
        private PanelElementDescriptor Group(RectTransform parent, string en, string ru, string enDescription = "", string ruDescription = "")
        {
            var group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            Label(group, en, ru, enDescription, ruDescription); return group;
        }
        private PanelButton Button(RectTransform parent, string en, string ru, Action clicked)
        {
            var button = PanelButton.CreateNew(parent); Label(button.Descriptor, en, ru); button.OnClicked += clicked; return button;
        }
        private PanelTextField Field(RectTransform parent, string en, string ru, string value, int limit)
        {
            var field = PanelTextField.CreateNewEntry(parent); Label(field.Descriptor, en, ru, placeholder: field._placeholderLabel);
            if (field._inputField != null) { field._inputField.contentType = TMPro.TMP_InputField.ContentType.Standard; field._inputField.characterLimit = limit; field._inputField.richText = false; }
            SetField(field, value); return field;
        }
        private void Label(PanelElementDescriptor descriptor, string en, string ru, string enDescription = "", string ruDescription = "", TMPro.TMP_Text placeholder = null)
        {
            descriptor.DisableRichText();
            Action translate = () => { descriptor.SetTitle(Text(en, ru)); descriptor.SetDescription(Text(enDescription, ruDescription)); SetPlaceholder(placeholder, en, ru); };
            translations.Add(translate); translate();
        }
        private static string Value(PanelTextField field) => field?._inputField != null ? field._inputField.text : field?.Value ?? "";
        private static void SetField(PanelTextField field, string value) { field.SetValueWithoutNotify(value); if (field._inputField != null) field._inputField.SetTextWithoutNotify(value); }

        private void OnPanelClosed()
        {
            generation++;
            BasisLocalization.OnLanguageChanged -= Translate;
            BasisSocialRuntime.ClientChanged -= OnClientChanged;
            if (client != null) client.SessionChanged -= OnSessionChanged;
            if (connection != null) { connection.Changed -= RenderConnection; connection.Dispose(); connection = null; }
            request?.Cancel(); request = null;
            catalog?.Dispose(); catalog = null;
            community?.Dispose(); community = null;
            ClearProfileImage(); profileImage = null; displayedImageUrl = null;
            displayedUserId = null; serverBio = null; serverStatus = null;
            status = null; account = null; signIn = null; legacy = null; settings = null;
            endpoint = null; login = null; bio = null; statusText = null; password = null;
            connect = null; copyCode = null; openBrowser = null; cancel = null; save = null; refresh = null; logout = null; applyEndpoint = null; legacyLogin = null;
            translations.Clear(); client = null; busy = false; panel = null; root = null;
        }
    }
}
