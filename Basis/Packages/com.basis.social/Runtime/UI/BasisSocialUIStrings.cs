using System;
using Basis.BasisUI;

namespace Basis.Social.UI
{
    public static class BasisSocialUIStrings
    {
        public static string Locale => BasisLocalization.CurrentLanguage?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true ? "ru" : "en";
        public static string Text(string en, string ru) => Locale == "ru" ? ru : en;
        public static void SetPlaceholder(TMPro.TMP_Text placeholder, string en, string ru)
        {
            if (placeholder == null) return;
            placeholder.richText = false;
            placeholder.text = Text(en, ru);
        }
        public static string Error(Exception exception) => exception is BasisSocialApiException api
            ? api.GetUserMessage(Locale)
            : Text("Could not connect. Check your connection and try again.", "Не удалось подключиться. Проверьте соединение и повторите попытку.");
    }
}
