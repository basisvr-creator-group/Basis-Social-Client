using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using UnityEngine;
using static Basis.Social.UI.BasisSocialUIStrings;

namespace Basis.Social.UI
{
    /// <summary>One bounded native list with reusable detail actions and explicit server-authorized joins.</summary>
    public sealed class BasisSocialCommunityPanel : IDisposable
    {
        private const int PageSize = 12;
        private readonly RectTransform parent;
        private readonly PanelElementDescriptor group, connection, status, results, details, joinState, invitation, requestFriend, directJoin;
        private readonly PanelDropdown section, presenceStatus, visibility;
        private readonly PanelTextField targetAcct, inviteAcct, inviteMessage, instanceId;
        private readonly PanelButton refresh, worldsBack, previous, next, back, primary, secondary, sendInvite, addFriend, reviewInstance, joinCancel, leave, retry;
        private readonly PanelButton[] cards = new PanelButton[PageSize];
        private readonly List<Action> translations = new();
        private readonly List<PanelElementDescriptor> layoutNodes = new();
        private readonly List<string> cursors = new() { null };
        private object[] items = Array.Empty<object>();
        private object selected;
        private BasisSocialWorld world;
        private string nextCursor;
        private int pageIndex, generation;
        private bool busy, disposed, ready, refreshQueued;
        private CancellationTokenSource request, lifetime = new();
        private BasisSocialRealtime realtime;
        private Exception lastError;
        private Func<Task> retryAction;
        private string statusEn, statusRu, descriptionEn, descriptionRu;

        public BasisSocialCommunityPanel(RectTransform parent)
        {
            this.parent = parent;
            group = Group(parent, "Community", "Сообщество", "Meet friends and explore live worlds.", "Общайтесь с друзьями и посещайте миры.");
            connection = Group(group.ContentParent, "Live updates", "Обновления");
            section = Dropdown(group.ContentParent, "Browse", "Раздел", new[] { "friends", "incoming", "outgoing", "online", "worlds", "invites" },
                new[] { "Friends", "Incoming requests", "Sent requests", "Friends online", "Worlds", "Invitations" },
                new[] { "Друзья", "Входящие заявки", "Отправленные заявки", "Друзья в сети", "Миры", "Приглашения" });
            section.SetValueWithoutNotify("worlds");
            section.OnValueChanged += ignored => { world = null; _ = LoadAsync(0, true); };
            refresh = Button(group.ContentParent, "Refresh", "Обновить", () => _ = LoadAsync(pageIndex, false));
            worldsBack = Button(group.ContentParent, "Back to worlds", "К списку миров", () => { world = null; _ = LoadAsync(0, true); });
            requestFriend = Group(group.ContentParent, "Add a friend", "Добавить друга");
            targetAcct = Field(requestFriend.ContentParent, "Account (name@service)", "Аккаунт (имя@сервис)", 320);
            targetAcct.SetValidator(value => string.IsNullOrWhiteSpace(value) ? Text("Enter your friend’s account.", "Введите аккаунт друга.") : null, false);
            addFriend = Button(requestFriend.ContentParent, "Send friend request", "Отправить заявку", () => { if (targetAcct.Validate()) _ = MutateAsync(token => Client.FriendActionAsync("request", acct: Value(targetAcct), cancellationToken: token), "Friend request sent", "Заявка отправлена"); });
            Button(group.ContentParent, "Connection and privacy settings", "Подключение и приватность", () => { directJoin.gameObject.SetActive(!directJoin.gameObject.activeSelf); Rebuild(directJoin); });
            directJoin = Group(group.ContentParent, "Open an invitation", "Открыть приглашение", "Paste an instance ID to review it before connecting.", "Вставьте ID инстанса, чтобы проверить его перед подключением.");
            instanceId = Field(directJoin.ContentParent, "Instance ID", "ID инстанса", 36);
            reviewInstance = Button(directJoin.ContentParent, "Review instance", "Посмотреть инстанс", () => _ = ReviewInstanceAsync(Value(instanceId)));
            presenceStatus = Dropdown(directJoin.ContentParent, "Status when joining", "Статус при входе", new[] { "online", "away", "busy", "invisible" }, new[] { "Online", "Away", "Busy", "Invisible" }, new[] { "В сети", "Отошёл", "Занят", "Невидимый" });
            visibility = Dropdown(directJoin.ContentParent, "Share presence when joining", "Кому виден статус при входе", new[] { "nobody", "friends", "followers", "public" }, new[] { "Nobody", "Friends", "Followers", "Everyone" }, new[] { "Никому", "Друзьям", "Подписчикам", "Всем" });
            presenceStatus.SetValueWithoutNotify(BasisSocialJoinController.PresenceStatus);
            visibility.SetValueWithoutNotify(BasisSocialJoinController.PresenceVisibility);
            directJoin.gameObject.SetActive(false);
            presenceStatus.OnValueChanged += value => BasisSocialJoinController.PresenceStatus = value;
            visibility.OnValueChanged += value => BasisSocialJoinController.PresenceVisibility = value;
            joinState = Group(group.ContentParent, "World connection", "Подключение к миру");
            joinCancel = Button(joinState.ContentParent, "Cancel connection", "Отменить подключение", BasisSocialJoinController.Cancel);
            leave = Button(joinState.ContentParent, "Leave instance", "Покинуть инстанс", () => _ = LeaveAsync());
            status = Group(group.ContentParent, "", "");
            retry = Button(status.ContentParent, "Retry", "Повторить", () => { if (retryAction != null) _ = retryAction(); });
            results = Group(group.ContentParent, "Results", "Результаты");
            for (int i = 0; i < cards.Length; i++)
            {
                int index = i;
                cards[i] = Button(results.ContentParent, "", "", () => { if (index < items.Length) ShowDetails(items[index]); });
                cards[i].gameObject.SetActive(false);
            }
            previous = Button(results.ContentParent, "Previous page", "Предыдущая страница", () => _ = LoadAsync(pageIndex - 1, false));
            next = Button(results.ContentParent, "Next page", "Следующая страница", () => _ = LoadAsync(pageIndex + 1, false));
            details = Group(group.ContentParent, "", "");
            back = Button(details.ContentParent, "Back to results", "К результатам", Back);
            primary = Button(details.ContentParent, "", "", () => _ = PrimaryAsync());
            secondary = Button(details.ContentParent, "", "", () => _ = SecondaryAsync());
            invitation = Group(details.ContentParent, "Invite a friend", "Пригласить друга");
            inviteAcct = Field(invitation.ContentParent, "Friend account", "Аккаунт друга", 320);
            inviteAcct.SetValidator(value => string.IsNullOrWhiteSpace(value) ? Text("Enter your friend’s account.", "Введите аккаунт друга.") : null, false);
            inviteMessage = Field(invitation.ContentParent, "Message (optional)", "Сообщение (необязательно)", 2000);
            sendInvite = Button(invitation.ContentParent, "Send invitation", "Отправить приглашение", () => _ = SendInviteAsync());
            details.gameObject.SetActive(false);
            BasisLocalization.OnLanguageChanged += Translate;
            BasisSocialJoinController.Changed += RenderJoin;
            BasisSocialDeepLink.Requested += ReviewPendingLink;
            ReloadConnection();
        }
        private void ReviewPendingLink()
        {
            if (disposed || string.IsNullOrEmpty(BasisSocialDeepLink.PendingInstanceId)) return;
            instanceId.SetValueWithoutNotify(BasisSocialDeepLink.PendingInstanceId);
            if (instanceId._inputField != null) instanceId._inputField.SetTextWithoutNotify(BasisSocialDeepLink.PendingInstanceId);
            directJoin.gameObject.SetActive(true);
            Rebuild(directJoin);
            if (Client?.HasSession == true && !busy) { string id = BasisSocialDeepLink.PendingInstanceId; BasisSocialDeepLink.Clear(); _ = ReviewInstanceAsync(id); }
            else SetStatus("Sign in, then review the invited instance.", "Войдите, затем проверьте инстанс из приглашения.");
        }
        private BasisSocialApiClient Client => BasisSocialRuntime.Client;
        public void ReloadConnection()
        {
            if (disposed) return;
            generation++; request?.Cancel(); realtime?.Dispose(); realtime = null;
            selected = null; world = null; cursors.Clear(); cursors.Add(null); pageIndex = 0; nextCursor = null;
            items = Array.Empty<object>(); ready = false; busy = false; RenderItems(); Back();
            if (BasisSocialRuntime.IsConfigured && Client?.HasSession == true)
            {
                realtime = new BasisSocialRealtime(Client, BasisSocialRuntime.BaseUrl, BasisSocialRuntime.AllowLoopbackHttp);
                realtime.Changed += RenderRealtime;
                realtime.Event += OnEvent;
                realtime.Start();
            }
            RenderRealtime(); RenderJoin();
            if (Client?.HasSession == true && !string.IsNullOrEmpty(BasisSocialDeepLink.PendingInstanceId)) ReviewPendingLink();
            else _ = LoadAsync(0, true);
        }
        private async Task LoadAsync(int targetPage, bool reset)
        {
            if (disposed || !BasisSocialRuntime.IsConfigured || Client == null) { SetStatus("Connect your Social service to continue.", "Подключите сервис Social, чтобы продолжить."); SetBusy(false); return; }
            if (section.Value != "worlds" && !Client.HasSession) { items = Array.Empty<object>(); RenderItems(); SetStatus("Sign in to view friends and invitations.", "Войдите, чтобы увидеть друзей и приглашения."); SetBusy(false); return; }
            if (targetPage < 0 || (!reset && targetPage > pageIndex + 1)) return;
            string cursor = reset ? null : targetPage < cursors.Count ? cursors[targetPage] : nextCursor;
            if (!reset && targetPage > pageIndex && string.IsNullOrEmpty(cursor)) return;
            int version = ++generation; request?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            items = Array.Empty<object>(); ready = false; selected = null; RenderItems(); Back(); SetBusy(true); SetStatus("Loading…", "Загрузка…");
            try
            {
                object[] values; BasisSocialPagination pagination;
                switch (section.Value)
                {
                    case "worlds":
                        if (world == null) { var page = await Client.ListWorldsAsync(cursor, PageSize, cancellation.Token); values = page.data; pagination = page.pagination; }
                        else { var page = await Client.ListInstancesAsync(world.id, cursor, PageSize, cancellation.Token); values = page.data; pagination = page.pagination; }
                        break;
                    case "invites": { var page = await Client.ListInvitesAsync(cursor, PageSize, cancellation.Token); values = page.data; pagination = page.pagination; break; }
                    case "online": { var page = await Client.ListFriendPresenceAsync(cursor, PageSize, cancellation.Token); values = page.data; pagination = page.pagination; break; }
                    default:
                        { var page = await Client.ListFriendsAsync(section.Value == "friends" ? "accepted" : "pending", section.Value == "incoming" ? "incoming" : section.Value == "outgoing" ? "outgoing" : "", cursor, PageSize, cancellation.Token); values = page.data; pagination = page.pagination; break; }
                }
                if (!Current(version)) return;
                if (reset) { cursors.Clear(); cursors.Add(null); }
                if (targetPage == cursors.Count) cursors.Add(cursor);
                items = values; pageIndex = targetPage; nextCursor = pagination.nextCursor; ready = true; RenderItems();
                SetStatus(values.Length == 0 ? "Nothing here yet" : "Select a card for details", values.Length == 0 ? "Здесь пока пусто" : "Выберите карточку для просмотра");
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && Current(version)) { Fail(new BasisSocialApiException(409, "session_changed", "Session changed."), () => LoadAsync(targetPage, reset)); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (Current(version)) Fail(error, () => LoadAsync(targetPage, reset)); }
            finally { if (ReferenceEquals(request, cancellation)) request = null; if (Current(version)) SetBusy(false); }
        }
        private void RenderItems()
        {
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].gameObject.SetActive(i < items.Length);
                if (i >= items.Length) continue;
                cards[i].Descriptor.SetTitle(Title(items[i])); cards[i].Descriptor.SetDescription(Description(items[i]));
            }
            worldsBack.gameObject.SetActive(world != null);
            results.SetTitle(world == null ? Text("Results", "Результаты") : world.name);
            results.SetDescription(world == null ? "" : Text("Choose a live instance.", "Выберите инстанс."));
            Rebuild(results);
        }
        private void ShowDetails(object item)
        {
            selected = item; details.gameObject.SetActive(true); results.gameObject.SetActive(false);
            details.SetTitle(Title(item)); details.SetDescription(Description(item));
            bool signedIn = Client?.HasSession == true;
            primary.gameObject.SetActive(false); secondary.gameObject.SetActive(false); invitation.gameObject.SetActive(false);
            switch (item)
            {
                case BasisSocialWorld _: Action(primary, "View instances", "Посмотреть инстансы", true); invitation.gameObject.SetActive(signedIn); break;
                case BasisSocialInstance instance:
                    Action(primary, "Join instance", "Войти в инстанс", signedIn && !JoinActive() && instance.status == "active" && (instance.capacity == 0 || instance.currentUsers < instance.capacity)); invitation.gameObject.SetActive(signedIn); break;
                case BasisSocialActor _:
                    if (section.Value == "incoming") { Action(primary, "Accept request", "Принять заявку", signedIn); Action(secondary, "Decline request", "Отклонить заявку", signedIn); }
                    else if (section.Value == "friends") Action(secondary, "Remove friend", "Удалить из друзей", signedIn);
                    break;
                case BasisSocialInvite invite:
                    bool recipient = string.Equals(invite.to?.actorId, Client?.CurrentUser?.actorId, StringComparison.OrdinalIgnoreCase);
                    if (recipient && invite.state == "pending" && !invite.IsExpired) { Action(primary, "Accept invitation", "Принять приглашение", signedIn); Action(secondary, "Decline invitation", "Отклонить приглашение", signedIn); }
                    else if (recipient && invite.state == "accepted" && !invite.IsExpired && !string.IsNullOrEmpty(invite.instanceId)) Action(primary, "Review instance", "Посмотреть инстанс", signedIn);
                    break;
                case BasisSocialPresence presence:
                    if (!string.IsNullOrEmpty(presence.instanceId)) Action(primary, "Review instance", "Посмотреть инстанс", signedIn);
                    break;
            }
            Rebuild(details);
        }
        private async Task PrimaryAsync()
        {
            if (busy) return;
            switch (selected)
            {
                case BasisSocialWorld value: world = value; await LoadAsync(0, true); break;
                case BasisSocialInstance value:
                    await MutateAsync(async token => {
                        await BasisSocialJoinController.JoinAsync(value, token);
                        if (BasisSocialJoinController.State == BasisSocialJoinState.Cancelled) throw new OperationCanceledException();
                        if (BasisSocialJoinController.State != BasisSocialJoinState.Connected || BasisSocialJoinController.CurrentInstanceId != value.id)
                            throw BasisSocialJoinController.Error ?? new BasisSocialApiException(0, "join_failed", "The instance was not joined.");
                    }, "Connected to the instance", "Вы подключены к инстансу", false); break;
                case BasisSocialActor value: await MutateAsync(token => Client.FriendActionAsync("accept", value.actorId, cancellationToken: token), "Friend request accepted", "Заявка принята"); break;
                case BasisSocialInvite value:
                    if (value.state == "pending") await MutateAsync(token => Client.DecideInviteAsync(value.id, true, token), "Invitation accepted. Open it to review the instance.", "Приглашение принято. Откройте его, чтобы посмотреть инстанс.");
                    else await ReviewInstanceAsync(value.instanceId); break;
                case BasisSocialPresence value: await ReviewInstanceAsync(value.instanceId); break;
            }
        }
        private Task SecondaryAsync()
        {
            if (selected is BasisSocialActor actor) return MutateAsync(token => Client.FriendActionAsync(section.Value == "incoming" ? "reject" : "remove", actor.actorId, cancellationToken: token), "Friend list updated", "Список друзей обновлён");
            if (selected is BasisSocialInvite invite) return MutateAsync(token => Client.DecideInviteAsync(invite.id, false, token), "Invitation declined", "Приглашение отклонено");
            return Task.CompletedTask;
        }
        private Task SendInviteAsync()
        {
            if (!inviteAcct.Validate()) return Task.CompletedTask;
            string worldId = (selected as BasisSocialWorld)?.id; string targetInstance = (selected as BasisSocialInstance)?.id;
            return MutateAsync(async token => { await Client.SendInviteAsync(Value(inviteAcct), worldId, targetInstance, Value(inviteMessage), token); }, "Invitation sent", "Приглашение отправлено", false);
        }
        private async Task ReviewInstanceAsync(string id)
        {
            if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty) { Fail(new BasisSocialApiException(400, "invalid_instance", "Invalid instance."), () => ReviewInstanceAsync(Value(instanceId))); return; }
            BasisSocialInstance result = null;
            await MutateAsync(async token => result = await Client.GetInstanceAsync(id, token), "Review the instance before connecting", "Проверьте инстанс перед подключением", false);
            if (!disposed && result != null) ShowDetails(result);
        }
        private Task LeaveAsync() => MutateAsync(token => BasisSocialJoinController.LeaveAsync(token), "Disconnected from the instance", "Вы вышли из инстанса", false);
        private async Task MutateAsync(Func<CancellationToken, Task> action, string successEn, string successRu, bool reload = true)
        {
            if (disposed || busy || Client?.HasSession != true) return;
            int version = ++generation; request?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            SetBusy(true); SetStatus("Please wait…", "Подождите…");
            try
            {
                await action(cancellation.Token); if (!Current(version)) return;
                if (reload) { await LoadAsync(0, true); return; }
                if (Current(version)) SetStatus(successEn, successRu);
            }
            catch (OperationCanceledException) { if (Current(version)) SetStatus("Operation cancelled", "Операция отменена"); }
            catch (Exception error) { if (Current(version)) Fail(error, () => MutateAsync(action, successEn, successRu, reload)); }
            finally { if (ReferenceEquals(request, cancellation)) request = null; if (Current(version) || reload) SetBusy(false); }
        }
        private void Back() { selected = null; details.gameObject.SetActive(false); results.gameObject.SetActive(true); Rebuild(results); }
        private void OnEvent(BasisSocialRealtimeEvent item)
        {
            if (item.type == "user.suspended") { Client?.ClearSession(); return; }
            if (item.type == "realtime.connected" || item.type == "realtime.resync_required" || item.type.StartsWith("friend.", StringComparison.Ordinal) || item.type.StartsWith("invite.", StringComparison.Ordinal) || item.type.StartsWith("presence.", StringComparison.Ordinal) || item.type.StartsWith("instance.", StringComparison.Ordinal)) QueueRefresh();
        }
        private async void QueueRefresh()
        {
            if (disposed || refreshQueued) return; refreshQueued = true;
            try
            {
                await Task.Delay(500, lifetime.Token);
                while (busy && !disposed) await Task.Delay(500, lifetime.Token);
                if (disposed) return;
                // Keep the currently reviewed target stable. Its join is independently authorized again.
                if (selected == null) await LoadAsync(pageIndex, false);
                else SetStatus("Updates available. Return to the list and refresh.", "Есть обновления. Вернитесь к списку и обновите его.");
            }
            catch (OperationCanceledException) { }
            finally { refreshQueued = false; }
        }
        private void RenderRealtime()
        {
            if (disposed) return;
            string title = realtime?.State switch
            {
                BasisSocialRealtimeState.Connected => Text("Live updates connected", "Обновления подключены"),
                BasisSocialRealtimeState.Connecting => Text("Connecting live updates…", "Подключаем обновления…"),
                BasisSocialRealtimeState.Reconnecting => Text("Live updates disconnected — reconnecting…", "Обновления отключены — переподключаемся…"),
                _ => Text("Sign in for live updates", "Войдите для получения обновлений")
            };
            connection.SetTitle(title); connection.SetDescription(Text("Refresh retrieves the latest server state.", "Кнопка «Обновить» получает актуальные данные сервера.")); Rebuild(connection);
        }
        private static bool JoinActive() => BasisSocialJoinController.State == BasisSocialJoinState.Preparing || BasisSocialJoinController.State == BasisSocialJoinState.Connecting || BasisSocialJoinController.State == BasisSocialJoinState.Connected;
        private void RenderJoin()
        {
            if (disposed) return;
            var state = BasisSocialJoinController.State;
            bool connecting = state == BasisSocialJoinState.Preparing || state == BasisSocialJoinState.Connecting;
            bool connected = state == BasisSocialJoinState.Connected;
            string title = state switch
            {
                BasisSocialJoinState.Preparing => Text("Checking access…", "Проверяем доступ…"),
                BasisSocialJoinState.Connecting => Text("Connecting to the world…", "Подключаемся к миру…"),
                BasisSocialJoinState.Connected => BasisSocialJoinController.UsingBuiltInWorld ? Text("Connected · built-in world", "Подключено · встроенный мир") : Text("Connected to the world", "Вы подключены к миру"),
                BasisSocialJoinState.Failed => Text("Connection failed", "Не удалось подключиться"),
                BasisSocialJoinState.Cancelled => Text("Connection cancelled", "Подключение отменено"),
                _ => Text("Not connected to a world", "Нет подключения к миру")
            };
            joinState.SetTitle(title); joinState.SetDescription(BasisSocialJoinController.Error == null ? "" : Error(BasisSocialJoinController.Error));
            joinCancel.gameObject.SetActive(connecting); leave.gameObject.SetActive(connected);
            presenceStatus.SetInteractable(!connecting && !connected); visibility.SetInteractable(!connecting && !connected);
            if (selected is BasisSocialInstance current) primary.SetInteractable(!busy && !JoinActive() && Client?.HasSession == true && current.status == "active" && (current.capacity == 0 || current.currentUsers < current.capacity));
            Rebuild(joinState);
        }
        private void SetBusy(bool value)
        {
            if (disposed) return; busy = value;
            bool signedIn = Client?.HasSession == true;
            section.SetInteractable(!value); worldsBack.SetInteractable(!value); refresh.SetInteractable(!value && BasisSocialRuntime.IsConfigured);
            previous.SetInteractable(!value && ready && pageIndex > 0); next.SetInteractable(!value && ready && !string.IsNullOrEmpty(nextCursor));
            addFriend.SetInteractable(!value && signedIn); reviewInstance.SetInteractable(!value && signedIn); sendInvite.SetInteractable(!value && signedIn);
            targetAcct.SetInteractable(!value); inviteAcct.SetInteractable(!value); inviteMessage.SetInteractable(!value); instanceId.SetInteractable(!value);
            back.SetInteractable(!value); retry.SetInteractable(!value);
            foreach (var card in cards) card.SetInteractable(!value);
            if (selected != null && !value) ShowDetails(selected);
            else if (value) { primary.SetInteractable(false); secondary.SetInteractable(false); }
            requestFriend.gameObject.SetActive(section.Value == "friends" || section.Value == "incoming" || section.Value == "outgoing"); RenderJoin(); Rebuild(requestFriend);
        }
        private void SetStatus(string en, string ru, string enDescription = "", string ruDescription = "")
        {
            statusEn = en; statusRu = ru; descriptionEn = enDescription; descriptionRu = ruDescription;
            lastError = null; retryAction = null; status.SetTitle(Text(en, ru)); status.SetDescription(Text(enDescription, ruDescription)); retry.gameObject.SetActive(false); Rebuild(status);
        }
        private void Fail(Exception error, Func<Task> action)
        {
            SetStatus("Could not complete the request", "Не удалось выполнить запрос"); lastError = error; retryAction = action;
            status.SetDescription(Error(error)); retry.gameObject.SetActive(true); Rebuild(status);
        }
        private static string Title(object value) => value switch
        {
            BasisSocialActor actor => actor.DisplayName,
            BasisSocialWorld world => world.name,
            BasisSocialInstance instance => instance.name,
            BasisSocialInvite invite => invite.from?.DisplayName ?? Text("Invitation", "Приглашение"),
            BasisSocialPresence presence => string.IsNullOrEmpty(presence.displayName) ? presence.acct : presence.displayName,
            _ => ""
        };
        private static string Description(object value) => value switch
        {
            BasisSocialActor actor => actor.acct,
            BasisSocialWorld world => world.description,
            BasisSocialInstance instance => instance.currentUsers + " / " + (instance.capacity == 0 ? "∞" : instance.capacity.ToString()) + " · " + StateName(instance.status) + " · " + StateName(instance.visibility),
            BasisSocialInvite invite => (invite.IsExpired ? Text("Expired", "Истекло") : StateName(invite.state)) + " · " + Text("To: ", "Кому: ") + invite.to?.DisplayName + "\n" + invite.message,
            BasisSocialPresence presence => StateName(presence.status) + (string.IsNullOrEmpty(presence.instanceId) ? "" : " · " + Text("In a world", "В мире")),
            _ => ""
        };
        private static string StateName(string state) => state switch
        {
            "active" => Text("Open", "Открыт"), "closed" => Text("Closed", "Закрыт"), "full" => Text("Full", "Заполнен"),
            "pending" => Text("Pending", "Ожидает ответа"), "accepted" => Text("Accepted", "Принято"), "declined" => Text("Declined", "Отклонено"), "expired" => Text("Expired", "Истекло"),
            "online" => Text("Online", "В сети"), "away" => Text("Away", "Отошёл"), "busy" => Text("Busy", "Занят"), "invisible" => Text("Invisible", "Невидимый"),
            "public" => Text("Public", "Публичный"), "friends" => Text("Friends", "Для друзей"), "private" => Text("Private", "Закрытый"), "unlisted" => Text("Unlisted", "По ссылке"),
            _ => Text("Unavailable", "Недоступно")
        };
        private void Translate()
        {
            foreach (var translation in translations) translation();
            RenderItems(); RenderRealtime(); RenderJoin();
            status.SetTitle(Text(statusEn ?? "", statusRu ?? "")); status.SetDescription(lastError == null ? Text(descriptionEn ?? "", descriptionRu ?? "") : Error(lastError));
            if (selected != null) ShowDetails(selected); Rebuild();
        }
        private PanelElementDescriptor Group(Component parent, string en, string ru, string descriptionEn = "", string descriptionRu = "")
        {
            var item = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent); Label(item, en, ru, descriptionEn, descriptionRu); return item;
        }
        private PanelButton Button(Component parent, string en, string ru, Action clicked)
        { var button = PanelButton.CreateNew(parent); Label(button.Descriptor, en, ru); button.OnClicked += clicked; return button; }
        private PanelTextField Field(Component parent, string en, string ru, int limit)
        {
            var field = PanelTextField.CreateNewEntry(parent); Label(field.Descriptor, en, ru, placeholder: field._placeholderLabel);
            if (field._inputField != null) { field._inputField.characterLimit = limit; field._inputField.richText = false; }
            return field;
        }
        private PanelDropdown Dropdown(Component parent, string en, string ru, string[] keys, string[] english, string[] russian)
        {
            var dropdown = PanelDropdown.CreateNewEntry(parent); Label(dropdown.Descriptor, en, ru);
            if (dropdown.DropdownComponent.captionText != null) dropdown.DropdownComponent.captionText.richText = false;
            if (dropdown.DropdownComponent.itemText != null) dropdown.DropdownComponent.itemText.richText = false;
            Action translate = () => { var value = dropdown.Value; dropdown.AssignEntries(new List<string>(keys), new List<string>(Locale == "ru" ? russian : english)); dropdown.SetValueWithoutNotify(string.IsNullOrEmpty(value) ? keys[0] : value); };
            translations.Add(translate); translate(); return dropdown;
        }
        private void Label(PanelElementDescriptor descriptor, string en, string ru, string descriptionEn = "", string descriptionRu = "", TMPro.TMP_Text placeholder = null)
        {
            descriptor.DisableRichText(); Action translate = () => { descriptor.SetTitle(Text(en, ru)); descriptor.SetDescription(Text(descriptionEn, descriptionRu)); SetPlaceholder(placeholder, en, ru); }; translations.Add(translate); translate();
        }
        private void Action(PanelButton button, string en, string ru, bool enabled) { button.Descriptor.SetTitle(Text(en, ru)); button.gameObject.SetActive(true); button.SetInteractable(enabled && !busy); }
        private static string Value(PanelTextField field) => (field._inputField != null ? field._inputField.text : field.Value)?.Trim() ?? "";
        private bool Current(int version) => !disposed && version == generation;
        private void Rebuild(PanelElementDescriptor changed = null)
        {
            if (disposed || parent == null) return;
            changed ??= group;
            if (changed == null) return;
            // Each native card has nested text fitters. Settle the changed subtree from its
            // leaves before its ancestors measure it; rebuilding only the page retains old heights.
            // Reuse the buffer and existing panel API without enabling hidden content containers.
            layoutNodes.Clear();
            changed.GetComponentsInChildren(false, layoutNodes);
            for (int i = layoutNodes.Count - 1; i >= 0; i--)
            {
                var node = layoutNodes[i];
                var from = node.TitleLabel != null ? node.TitleLabel.rectTransform : node.rectTransform;
                PanelElementDescriptor.RebuildLayoutChain(from, changed.rectTransform);
            }
            PanelElementDescriptor.RebuildLayoutChain(changed.rectTransform, parent);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; generation++; lifetime.Cancel(); request?.Cancel(); realtime?.Dispose();
            BasisLocalization.OnLanguageChanged -= Translate; BasisSocialJoinController.Changed -= RenderJoin; BasisSocialDeepLink.Requested -= ReviewPendingLink;
        }
    }
}
