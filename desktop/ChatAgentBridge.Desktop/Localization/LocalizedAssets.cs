using ChatAgentBridge.Core.Localization;
using System.Globalization;

namespace ChatAgentBridge_Desktop.Localization;

public static class LocalizedAssets
{
    public static string OverviewArchitecture => AppLanguage.IsJapanese
        ? "ms-appx:///Assets/OverviewArchitecture.svg"
        : "ms-appx:///Assets/OverviewArchitecture.en.svg";

    public static string GetOverviewArchitecture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)
            ? "ms-appx:///Assets/OverviewArchitecture.svg"
            : "ms-appx:///Assets/OverviewArchitecture.en.svg";
}
