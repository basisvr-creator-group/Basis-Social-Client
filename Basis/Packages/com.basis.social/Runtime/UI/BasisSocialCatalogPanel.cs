using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using static Basis.Social.UI.BasisSocialUIStrings;

namespace Basis.Social.UI
{
    /// <summary>Bounded, reusable native catalog controls. Every avatar load obtains a new public manifest.</summary>
    public sealed class BasisSocialCatalogPanel : IDisposable
    {
        private const int PageSize = 12;
        private readonly PanelElementDescriptor group, status, results, details;
        private readonly PanelDropdown catalog, category;
        private readonly PanelTextField search;
        private readonly PanelToggle nsfw;
        private readonly PanelImage preview;
        private Texture2D previewTexture;
        private Sprite previewSprite;
        private CancellationTokenSource previewRequest;
        private readonly PanelButton find, previous, next, retry, wear, cancel, back;
        private readonly PanelButton[] cards = new PanelButton[PageSize];
        private readonly List<string> cursors = new List<string> { null };
        private BasisSocialCatalogAsset[] items = Array.Empty<BasisSocialCatalogAsset>();
        private BasisSocialCatalogAsset selected;
        private string nextCursor;
        private int pageIndex, generation;
        private bool disposed, busy, loadingAvatar, pageReady;
        private CancellationTokenSource request;
        private CancellationTokenSource avatarRequest;
        private Func<Task> retryAction;
        private Exception lastError;

        public BasisSocialCatalogPanel(RectTransform parent)
        {
            group = Group(parent, Text("BeeBa catalog", "Каталог BeeBa"), Text("Choose an avatar or explore community content.", "Выберите аватар или изучите материалы сообщества."));
            catalog = PanelDropdown.CreateNewEntry(group.ContentParent);
            catalog.Descriptor.SetTitle(Text("Catalog", "Каталог"));
            if (catalog.DropdownComponent.captionText != null) catalog.DropdownComponent.captionText.richText = false;
            if (catalog.DropdownComponent.itemText != null) catalog.DropdownComponent.itemText.richText = false;
            category = PanelDropdown.CreateNewEntry(group.ContentParent);
            category.Descriptor.SetTitle(Text("Category", "Категория"));
            category.AssignEntries(new List<string> { "avatars", "worlds", "props", "prefabs" }, new List<string> { Text("Avatars", "Аватары"), Text("Worlds", "Миры"), Text("Props", "Предметы"), Text("Prefabs", "Префабы") });
            category.SetValueWithoutNotify("avatars");
            search = PanelTextField.CreateNewEntry(group.ContentParent);
            search.Descriptor.SetTitle(Text("Search", "Поиск"));
            SetPlaceholder(search._placeholderLabel, "Search", "Поиск");
            if (search._inputField != null) search._inputField.characterLimit = 200;
            nsfw = PanelToggle.CreateNewEntry(group.ContentParent);
            nsfw.Descriptor.SetTitle(Text("Show mature content", "Показывать контент 18+"));
            nsfw.SetValueWithoutNotify(false);
            find = Button(group.ContentParent, Text("Search catalog", "Найти материалы"), () => _ = SearchAsync(0, true));
            category.OnValueChanged += ignored => { _ = SearchAsync(0, true); };
            catalog.OnValueChanged += ignored => { _ = SearchAsync(0, true); };
            nsfw.OnValueChanged += ignored => { _ = SearchAsync(0, true); };
            status = Group(group.ContentParent, string.Empty, string.Empty);
            Plain(status);
            retry = Button(status.ContentParent, Text("Retry", "Повторить"), () => { if (retryAction != null) _ = retryAction(); });
            results = Group(group.ContentParent, Text("Results", "Результаты"), string.Empty);
            for (int i = 0; i < cards.Length; i++)
            {
                int index = i;
                cards[i] = Button(results.ContentParent, string.Empty, () => { if (index < items.Length) ShowDetails(items[index]); });
                Plain(cards[i].Descriptor);
                cards[i].gameObject.SetActive(false);
            }
            previous = Button(results.ContentParent, Text("Previous page", "Предыдущая страница"), () => _ = SearchAsync(pageIndex - 1, false));
            next = Button(results.ContentParent, Text("Next page", "Следующая страница"), () => _ = SearchAsync(pageIndex + 1, false));
            details = Group(group.ContentParent, string.Empty, string.Empty);
            Plain(details);
            back = Button(details.ContentParent, Text("Back to results", "К результатам"), BackToResults);
            preview = PanelImage.CreateNew(details.ContentParent);
            preview.SetHeight(180);
            preview.Image.type = UnityEngine.UI.Image.Type.Simple;
            preview.Image.preserveAspect = true;
            preview.Image.raycastTarget = false;
            preview.gameObject.SetActive(false);
            wear = Button(details.ContentParent, Text("Wear avatar", "Надеть аватар"), () => _ = WearAsync());
            cancel = Button(details.ContentParent, Text("Cancel loading", "Отменить загрузку"), CancelAvatar);
            details.gameObject.SetActive(false);
            BasisLocalization.OnLanguageChanged += Translate;
            ReloadConnection();
        }

        public void ReloadConnection()
        {
            if (disposed) return;
            generation++;
            request?.Cancel();
            ClearPreview();
            CancelAvatar();
            catalog.AssignEntries(new List<string>());
            catalog.SetValueWithoutNotify(string.Empty);
            cursors.Clear(); cursors.Add(null); pageIndex = 0; nextCursor = null; pageReady = false;
            items = Array.Empty<BasisSocialCatalogAsset>(); selected = null;
            RenderItems(); details.gameObject.SetActive(false); results.gameObject.SetActive(true);
            if (!BasisSocialRuntime.IsConfigured || BasisSocialRuntime.Client == null)
            {
                SetBusy(false);
                SetStatus(Text("Service not connected", "Сервис не подключён"), Text("Connect to your Social service to browse BeeBa.", "Подключитесь к сервису Social, чтобы открыть BeeBa."));
                find.SetInteractable(false);
                return;
            }
            _ = LoadCatalogsAsync();
        }

        private async Task LoadCatalogsAsync()
        {
            int version = ++generation;
            request?.Cancel();
            using var cancellation = new CancellationTokenSource(); request = cancellation;
            SetBusy(true);
            SetStatus(Text("Loading catalogs…", "Загружаем каталоги…"), string.Empty);
            try
            {
                var available = await BasisSocialRuntime.Client.ListCatalogsAsync(cancellation.Token);
                if (!Current(version)) return;
                var codes = new List<string>(); var labels = new List<string>();
                foreach (var entry in available)
                    if (entry != null && entry.enabled && !string.IsNullOrWhiteSpace(entry.code)) { codes.Add(entry.code); labels.Add(entry.name ?? entry.code); }
                catalog.AssignEntries(codes, labels);
                catalog.SetValueWithoutNotify(codes.Count == 0 ? string.Empty : codes[0]);
                if (codes.Count == 0)
                {
                    SetStatus(Text("No catalogs available", "Нет доступных каталогов"), Text("The service administrator has not enabled a catalog yet.", "Администратор сервиса пока не включил каталог."));
                    find.SetInteractable(false);
                    return;
                }
                catalog.SetValueWithoutNotify(codes[0]);
                await SearchAsync(0, true);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && Current(version))
            {
                Fail(new BasisSocialApiException(409, "session_changed", "The session changed during the request."), LoadCatalogsAsync);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (Current(version)) Fail(error, LoadCatalogsAsync); }
            finally { if (ReferenceEquals(request, cancellation)) request = null; if (Current(version)) SetBusy(false); }
        }

        private async Task SearchAsync(int targetPage, bool reset)
        {
            if (disposed || loadingAvatar || !BasisSocialRuntime.IsConfigured || BasisSocialRuntime.Client == null || string.IsNullOrEmpty(catalog.Value)) return;
            if (targetPage < 0 || (!reset && targetPage > pageIndex + 1)) return;
            string cursor = reset ? null : targetPage < cursors.Count ? cursors[targetPage] : nextCursor;
            if (!reset && targetPage > pageIndex && string.IsNullOrEmpty(cursor)) return;
            int version = ++generation;
            request?.Cancel();
            using var cancellation = new CancellationTokenSource(); request = cancellation;
            selected = null; ClearPreview(); details.gameObject.SetActive(false); results.gameObject.SetActive(true);
            // Old results are never actionable while a filter or page is being refreshed.
            items = Array.Empty<BasisSocialCatalogAsset>(); pageReady = false; RenderItems(); SetBusy(true);
            SetStatus(Text("Searching…", "Ищем материалы…"), string.Empty);
            try
            {
                var page = await BasisSocialRuntime.Client.SearchCatalogAsync(new BasisSocialCatalogQuery
                {
                    Catalog = catalog.Value, ContentType = category.Value, IncludeNSFW = nsfw.Value,
                    Query = search._inputField != null ? search._inputField.text : search.Value, Limit = PageSize, Cursor = cursor
                }, cancellation.Token);
                if (!Current(version)) return;
                if (reset) { cursors.Clear(); cursors.Add(null); }
                if (targetPage == cursors.Count) cursors.Add(cursor);
                pageIndex = targetPage; pageReady = true;
                items = page.items ?? Array.Empty<BasisSocialCatalogAsset>(); nextCursor = page.nextCursor;
                RenderItems();
                SetStatus(items.Length == 0 ? Text("Nothing found", "Ничего не найдено") : Text("Page ", "Страница ") + (pageIndex + 1),
                    items.Length == 0 ? Text("Try another search or category.", "Измените запрос или категорию.") : Text("Select a card for details.", "Выберите карточку, чтобы узнать подробнее."));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && Current(version))
            {
                Fail(new BasisSocialApiException(409, "session_changed", "The session changed during the request."), () => SearchAsync(targetPage, reset));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (Current(version)) Fail(error, () => SearchAsync(targetPage, reset)); }
            finally { if (ReferenceEquals(request, cancellation)) request = null; if (Current(version)) SetBusy(false); }
        }

        private void RenderItems()
        {
            for (int i = 0; i < cards.Length; i++)
            {
                bool visible = i < items.Length;
                cards[i].gameObject.SetActive(visible);
                if (!visible) continue;
                var asset = items[i];
                cards[i].Descriptor.SetTitle(asset.title ?? Text("Unavailable content", "Недоступный материал"));
                cards[i].Descriptor.SetDescription((asset.authorName ?? string.Empty) + (asset.nsfw ? " · 18+" : string.Empty));
            }
            previous.SetInteractable(!busy && pageReady && pageIndex > 0);
            next.SetInteractable(!busy && pageReady && !string.IsNullOrEmpty(nextCursor));
        }

        private void ShowDetails(BasisSocialCatalogAsset asset)
        {
            if (busy || loadingAvatar) return;
            selected = asset;
            ClearPreview();
            if (!string.IsNullOrEmpty(asset.previewUrl) && asset.Availability == BasisSocialAssetAvailability.Available) _ = LoadPreviewAsync(asset);
            details.gameObject.SetActive(true);
            results.gameObject.SetActive(false);
            details.SetTitle(asset.title ?? Text("Unavailable content", "Недоступный материал"));
            string description = (asset.description ?? string.Empty) + "\n" + asset.GetAvailabilityMessage(Locale);
            if (!string.IsNullOrEmpty(asset.authorName)) description += "\n" + Text("By ", "Автор: ") + asset.authorName;
            if (!string.IsNullOrEmpty(asset.license)) description += "\n" + Text("License: ", "Лицензия: ") + asset.license;
            if (asset.sizeBytes > 0) description += "\n" + (asset.sizeBytes / 1048576d).ToString("0.0") + " MB";
            bool avatar = asset.contentType == "avatars" || asset.contentType == "avatar";
            if (!avatar) description += "\n" + Text("Preview only. World joining and spawning are not available here yet.", "Только просмотр. Вход в миры и создание объектов здесь пока недоступны.");
            else if (BasisSocialRuntime.Client?.HasSession != true) description += "\n" + Text("Sign in to wear this avatar.", "Войдите, чтобы надеть этот аватар.");
            details.SetDescription(description);
            wear.gameObject.SetActive(avatar);
            wear.SetInteractable(asset.Availability == BasisSocialAssetAvailability.Available && BasisSocialRuntime.Client?.HasSession == true);
            cancel.gameObject.SetActive(false);
        }

        private void BackToResults()
        {
            if (loadingAvatar) return;
            selected = null; ClearPreview(); details.gameObject.SetActive(false); results.gameObject.SetActive(true);
            SetStatus(Text("Page ", "Страница ") + (pageIndex + 1), Text("Select a card for details.", "Выберите карточку, чтобы узнать подробнее."));
        }

        private async Task WearAsync()
        {
            if (selected == null || busy || loadingAvatar || BasisSocialRuntime.Client?.HasSession != true) return;
            int version = generation;
            var chosen = selected;
            using var cancellation = new CancellationTokenSource(); avatarRequest = cancellation;
            loadingAvatar = true; SetBusy(true); cancel.gameObject.SetActive(true); cancel.SetInteractable(true);
            SetStatus(Text("Checking access…", "Проверяем доступ…"), string.Empty);
            try
            {
                var manifest = await BasisSocialRuntime.Client.PrepareCatalogAssetLoadAsync(chosen.catalog?.code ?? catalog.Value, chosen.externalId, nsfw.Value, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Current(version)) return;
                // Search cards can be unversioned summaries: Wear explicitly selects the live
                // published package. A card that does carry a version must not silently switch it.
                if (!string.IsNullOrEmpty(chosen.versionId) && !string.Equals(chosen.versionId, manifest.VersionId, StringComparison.OrdinalIgnoreCase))
                    throw new BasisSocialApiException(409, "asset_version_changed", "The selected package version changed.");
                if (manifest.ContentType != "avatars" && manifest.ContentType != "avatar")
                    throw new BasisSocialApiException(409, "asset_unavailable", "This package is no longer an avatar.");
                await BasisSocialAvatarLoader.WearAsync(manifest, progress =>
                {
                    if (Current(version) && !cancellation.IsCancellationRequested) SetStatus(Text("Loading avatar… ", "Загружаем аватар… ") + Mathf.RoundToInt(progress) + "%", Text("You can cancel this operation.", "Загрузку можно отменить."));
                }, cancellation.Token);
                if (Current(version)) SetStatus(Text("Avatar equipped", "Аватар надет"), chosen.title ?? string.Empty);
            }
            catch (OperationCanceledException) { if (Current(version)) SetStatus(Text("Loading cancelled", "Загрузка отменена"), Text("You can try again when ready.", "Вы можете повторить загрузку.")); }
            catch (Exception error)
            {
                if (Current(version))
                {
                    bool refreshRequired = error is BasisSocialApiException api && (api.ErrorCode == "asset_version_changed" || api.ErrorCode == "asset_unavailable");
                    if (refreshRequired)
                    {
                        selected = null; ClearPreview(); details.gameObject.SetActive(false);
                    }
                    Fail(error, refreshRequired ? () => SearchAsync(0, true) : WearAsync);
                }
            }
            finally
            {
                if (ReferenceEquals(avatarRequest, cancellation)) avatarRequest = null;
                if (Current(version)) { loadingAvatar = false; SetBusy(false); cancel.gameObject.SetActive(false); }
            }
        }

        private async Task LoadPreviewAsync(BasisSocialCatalogAsset asset)
        {
            using var cancellation = new CancellationTokenSource(); previewRequest = cancellation;
            try
            {
                var texture = await BasisSocialPreviewImage.LoadAsync(asset.previewUrl, cancellation.Token);
                if (disposed || cancellation.IsCancellationRequested || !ReferenceEquals(selected, asset)) { UnityEngine.Object.Destroy(texture); return; }
                previewTexture = texture;
                previewSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                preview.SetIcon(previewSprite, false); preview.gameObject.SetActive(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!disposed && !cancellation.IsCancellationRequested && ReferenceEquals(selected, asset))
                    {
                    preview.Descriptor.SetDescription(Text("Preview unavailable", "Превью недоступно"));
                    preview.gameObject.SetActive(true);
                }
            }
            finally { if (ReferenceEquals(previewRequest, cancellation)) previewRequest = null; }
        }
        private void ClearPreview()
        {
            previewRequest?.Cancel();
            if (preview != null) { preview.gameObject.SetActive(false); preview.SetIcon((Sprite)null, false); preview.Descriptor.SetDescription(string.Empty); }
            if (previewSprite != null) UnityEngine.Object.Destroy(previewSprite);
            if (previewTexture != null) UnityEngine.Object.Destroy(previewTexture);
            previewSprite = null; previewTexture = null;
        }

        private void CancelAvatar()
        {
            if (avatarRequest != null && !disposed)
            {
                SetStatus(Text("Cancelling loading…", "Отменяем загрузку…"), string.Empty);
                cancel.SetInteractable(false);
            }
            avatarRequest?.Cancel(); loadingAvatar = false;
        }
        private bool Current(int version) => !disposed && version == generation;
        private void SetBusy(bool value)
        {
            busy = value;
            find.SetInteractable(!value && !string.IsNullOrEmpty(catalog.Value));
            catalog.SetInteractable(!value); category.SetInteractable(!value); search.SetInteractable(!value); nsfw.SetInteractable(!value);
            previous.SetInteractable(!value && pageReady && pageIndex > 0); next.SetInteractable(!value && pageReady && !string.IsNullOrEmpty(nextCursor));
            back.SetInteractable(!value);
            wear.SetInteractable(!value && selected?.Availability == BasisSocialAssetAvailability.Available && BasisSocialRuntime.Client?.HasSession == true);
            foreach (var card in cards) card.SetInteractable(!value);
            retry.SetInteractable(!value);
        }
        private void SetStatus(string title, string description)
        {
            status.SetTitle(title); status.SetDescription(description); retry.gameObject.SetActive(false); retryAction = null; lastError = null;
        }
        private void Fail(Exception error, Func<Task> action)
        {
            SetStatus(Text("Could not complete the request", "Не удалось выполнить запрос"), Error(error));
            retryAction = action; lastError = error; retry.gameObject.SetActive(true);
        }
        private void Translate()
        {
            if (disposed) return;
            group.SetTitle(Text("BeeBa catalog", "Каталог BeeBa"));
            group.SetDescription(Text("Choose an avatar or explore community content.", "Выберите аватар или изучите материалы сообщества."));
            catalog.Descriptor.SetTitle(Text("Catalog", "Каталог"));
            category.Descriptor.SetTitle(Text("Category", "Категория"));
            string selectedCategory = category.Value;
            category.AssignEntries(new List<string> { "avatars", "worlds", "props", "prefabs" }, new List<string> { Text("Avatars", "Аватары"), Text("Worlds", "Миры"), Text("Props", "Предметы"), Text("Prefabs", "Префабы") });
            category.SetValueWithoutNotify(selectedCategory);
            search.Descriptor.SetTitle(Text("Search", "Поиск"));
            SetPlaceholder(search._placeholderLabel, "Search", "Поиск");
            nsfw.Descriptor.SetTitle(Text("Show mature content", "Показывать контент 18+"));
            find.Descriptor.SetTitle(Text("Search catalog", "Найти материалы"));
            results.SetTitle(Text("Results", "Результаты"));
            previous.Descriptor.SetTitle(Text("Previous page", "Предыдущая страница"));
            next.Descriptor.SetTitle(Text("Next page", "Следующая страница"));
            retry.Descriptor.SetTitle(Text("Retry", "Повторить"));
            back.Descriptor.SetTitle(Text("Back to results", "К результатам"));
            wear.Descriptor.SetTitle(Text("Wear avatar", "Надеть аватар"));
            cancel.Descriptor.SetTitle(Text("Cancel loading", "Отменить загрузку"));
            RenderItems();
            if (selected != null && !busy) ShowDetails(selected);
            if (lastError != null) { var error = lastError; var action = retryAction; Fail(error, action); }
            else if (loadingAvatar) SetStatus(Text("Loading avatar…", "Загружаем аватар…"), Text("You can cancel this operation.", "Загрузку можно отменить."));
            else if (busy) SetStatus(Text("Loading…", "Загрузка…"), string.Empty);
            else if (!BasisSocialRuntime.IsConfigured) SetStatus(Text("Service not connected", "Сервис не подключён"), Text("Connect to your Social service to browse BeeBa.", "Подключитесь к сервису Social, чтобы открыть BeeBa."));
            else if (string.IsNullOrEmpty(catalog.Value)) SetStatus(Text("No catalogs available", "Нет доступных каталогов"), Text("The service administrator has not enabled a catalog yet.", "Администратор сервиса пока не включил каталог."));
            else SetStatus(items.Length == 0 ? Text("Nothing found", "Ничего не найдено") : Text("Page ", "Страница ") + (pageIndex + 1), items.Length == 0 ? Text("Try another search or category.", "Измените запрос или категорию.") : Text("Select a card for details.", "Выберите карточку, чтобы узнать подробнее."));
        }

        private static PanelElementDescriptor Group(Component parent, string title, string description)
        {
            var descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            descriptor.SetTitle(title); descriptor.SetDescription(description); return descriptor;
        }
        private static PanelButton Button(Component parent, string title, Action clicked)
        {
            var button = PanelButton.CreateNew(parent); button.Descriptor.SetTitle(title); button.OnClicked += clicked; return button;
        }
        private static void Plain(PanelElementDescriptor descriptor)
        {
            if (descriptor.TitleLabel != null) descriptor.TitleLabel.richText = false;
            if (descriptor.DescriptionLabel != null) descriptor.DescriptionLabel.richText = false;
        }
        public void Dispose()
        {
            if (disposed) return;
            BasisLocalization.OnLanguageChanged -= Translate;
            disposed = true; generation++; request?.Cancel(); ClearPreview(); CancelAvatar();
        }
    }
}
