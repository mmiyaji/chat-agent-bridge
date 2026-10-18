using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge_Desktop.Localization;

public static class LocalizedAssets
{
    public static string OverviewArchitecture => AppLanguage.IsJapanese
        ? "ms-appx:///Assets/OverviewArchitecture.png"
        : "ms-appx:///Assets/OverviewArchitecture.en.png";
}
