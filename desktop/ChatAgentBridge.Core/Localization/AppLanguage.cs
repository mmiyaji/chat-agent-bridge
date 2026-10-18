using System.Globalization;

namespace ChatAgentBridge.Core.Localization;

public static class AppLanguage
{
    public static CultureInfo Select(string? preference, string? systemLanguage)
    {
        if (string.Equals(preference?.Trim(), "ja", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo("ja-JP");
        if (string.Equals(preference?.Trim(), "en", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo("en-US");

        var language = systemLanguage?.Trim();
        return language is not null && (language.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase))
            ? CultureInfo.GetCultureInfo("ja-JP")
            : CultureInfo.GetCultureInfo("en-US");
    }

    public static CultureInfo Apply(string? preference, string? systemLanguage)
    {
        var culture = Select(preference, systemLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return culture;
    }

    public static bool IsJapanese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase);
}
