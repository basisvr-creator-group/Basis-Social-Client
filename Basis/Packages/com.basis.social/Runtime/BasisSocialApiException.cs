using System;

namespace Basis.Social
{
    public enum BasisSocialRecoveryAction
    {
        TryAgainLater,
        CheckCredentials,
        WaitForApproval,
        SlowPolling,
        StartNewAuthorization,
        SignIn,
        ReviewLinkedAccounts,
        RevokeExistingDevice,
        ContactSupport
    }

    public sealed class BasisSocialApiException : Exception
    {
        public BasisSocialApiException(long statusCode, string errorCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public long StatusCode { get; }
        public string ErrorCode { get; }

        // Advisory UI classification, never permission to automatically replay a mutation.
        public BasisSocialRecoveryAction RecoveryAction => ErrorCode switch
        {
            "authorization_pending" => BasisSocialRecoveryAction.WaitForApproval,
            "slow_down" => BasisSocialRecoveryAction.SlowPolling,
            "access_denied" or "expired_token" or "invalid_grant" => BasisSocialRecoveryAction.StartNewAuthorization,
            "invalid_credentials" => BasisSocialRecoveryAction.CheckCredentials,
            "unauthorized" or "token_revoked" or "missing_refresh_token" => BasisSocialRecoveryAction.SignIn,
            "identity_already_linked" => BasisSocialRecoveryAction.ReviewLinkedAccounts,
            "session_limit" => BasisSocialRecoveryAction.RevokeExistingDevice,
            "account_inactive" => BasisSocialRecoveryAction.ContactSupport,
            _ when StatusCode == 429 => BasisSocialRecoveryAction.SlowPolling,
            _ => BasisSocialRecoveryAction.TryAgainLater
        };

        /// <summary>Safe EN/RU presentation text. Does not expose arbitrary remote messages or bodies.</summary>
        public string GetUserMessage(string locale = "en")
        {
            bool ru = string.Equals(locale, "ru", StringComparison.OrdinalIgnoreCase);
            switch (ErrorCode)
            {
                case "unverified_resource": return ru ? "Сервер запросил непроверенный ресурс. Загрузка заблокирована." : "The server requested an unverified resource. It was not loaded.";
                case "invalid_account": return ru ? "Введите аккаунт друга в формате имя@сервис." : "Enter your friend's account as name@service.";
                case "invalid_instance": return ru ? "Введите корректный ID инстанса из приглашения." : "Enter a valid instance ID from your invitation.";
                case "not_found": case "friend_not_found": return ru ? "Пользователь или материал недоступен. Проверьте выбранный аккаунт и обновите список." : "This account or content is unavailable. Check the account and refresh the list.";
                case "invite_expired": return ru ? "Срок приглашения истёк. Попросите отправить новое." : "This invitation expired. Ask for a new invitation.";
                case "instance_full": return ru ? "Инстанс заполнен. Выберите другой или повторите позже." : "This instance is full. Choose another or try again later.";
                case "instance_closed": case "instance_not_active": case "instance_unbound": return ru ? "Инстанс сейчас недоступен для подключения. Обновите список миров." : "This instance is not available for connection. Refresh the world list.";
                case "invalid_instance_endpoint": return ru ? "Сервер вернул неподдерживаемый адрес подключения. Сообщите владельцу мира." : "The server returned an unsupported connection address. Contact the world owner.";
                case "instance_changed": return ru ? "Адрес инстанса изменился. Обновите его данные перед подключением." : "The instance address changed. Refresh its details before connecting.";
                case "ticket_expired": case "ticket_consumed": return ru ? "Разрешение на вход истекло или уже использовано. Повторите подключение." : "The join permission expired or was already used. Try connecting again.";
                case "ticket_identity_mismatch": case "ticket_instance_mismatch": return ru ? "Не удалось подтвердить доступ к инстансу. Войдите заново и обновите приглашение." : "Instance access could not be verified. Sign in again and refresh the invitation.";
                case "join_failed": case "instance_disconnected": return ru ? "Соединение с миром прервано. Проверьте подключение и повторите попытку." : "The world connection was interrupted. Check your connection and try again.";
                case "player_not_ready": return ru ? "Локальный игрок ещё загружается. Дождитесь появления персонажа и повторите выбор аватара." : "Your local player is still loading. Wait for your character to appear, then choose the avatar again.";
                case "avatar_load_failed": return ru ? "Не удалось загрузить этот аватар на вашем устройстве. Выберите другой аватар или попросите автора проверить сборку для вашей платформы." : "This avatar could not be loaded on your device. Choose another avatar or ask its author to check the build for your platform.";
                case "session_changed": return ru ? "Подключение изменилось. Обновите данные." : "The connection changed. Refresh the data.";
                case "managed_identity": return ru ? "Имя и изображение профиля изменяются в BeeBa." : "Change your profile name and image in BeeBa.";
                case "primary_asset_exists": return ru ? "Сначала отсоедините текущий основной пакет мира." : "Detach the current primary world package first.";
                case "asset_limit": return ru ? "Удалите лишнюю связь с пакетом перед добавлением нового." : "Remove an existing package attachment before adding another.";
                case "invalid_asset_type": return ru ? "Для основного пакета выберите мир." : "Choose a world as the primary package.";
                case "asset_update_available": return ru ? "Владелец мира должен подтвердить новую версию пакета." : "The world owner must confirm the new package version.";
                case "asset_version_changed": return ru ? "Версия пакета изменилась. Обновите данные каталога." : "The package version changed. Refresh its catalog information.";
                case "asset_unavailable": return ru ? "Материал больше недоступен." : "This content is no longer available.";
                case "catalog_unavailable": return ru ? "Каталог временно недоступен. Повторите попытку позже." : "The catalog is temporarily unavailable. Try again later.";
            }
            return RecoveryAction switch
            {
                BasisSocialRecoveryAction.WaitForApproval => ru ? "Подтвердите запрос в BeeBa, затем вернитесь в приложение." : "Approve the request in BeeBa, then return to the app.",
                BasisSocialRecoveryAction.SlowPolling => ru ? "Слишком много запросов. Подождите перед повторной проверкой." : "Too many requests. Wait longer before checking again.",
                BasisSocialRecoveryAction.StartNewAuthorization => ru ? "Этот запрос отклонён, просрочен или уже использован. Начните новое подключение." : "This request was denied, expired or already used. Start a new connection request.",
                BasisSocialRecoveryAction.CheckCredentials => ru ? "Проверьте логин и пароль. Для связанного аккаунта используйте вход через BeeBa." : "Check your login and password. For a linked account, use BeeBa sign-in.",
                BasisSocialRecoveryAction.SignIn => ru ? "Сеанс завершён. Войдите снова." : "Your session has ended. Sign in again.",
                BasisSocialRecoveryAction.ReviewLinkedAccounts => ru ? "Аккаунт уже связан. Проверьте выбранные аккаунты BeeBa и Social." : "An account is already linked. Check the selected BeeBa and Social accounts.",
                BasisSocialRecoveryAction.RevokeExistingDevice => ru ? "Достигнут лимит устройств. Завершите один из существующих сеансов." : "The device limit has been reached. Sign out an existing device.",
                BasisSocialRecoveryAction.ContactSupport => ru ? "Аккаунт недоступен. Обратитесь в поддержку." : "This account is unavailable. Contact support.",
                _ => ru ? "Не удалось выполнить запрос. Попробуйте позже." : "The request could not be completed. Please try again later."
            };
        }
    }
}
