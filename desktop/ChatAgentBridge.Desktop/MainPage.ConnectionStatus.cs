using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    public static string ConnectionNavigationCompactLabel(string? provider, string tone) =>
        $"{ConnectionProviderLabel(provider)} · {DesktopText.Get(tone switch
        {
            "success" => "SidebarConnectionConnected",
            "running" => "SidebarConnectionConnecting",
            "error" => "SidebarConnectionError",
            _ => "SidebarConnectionDisconnected"
        })}";

    public static Style ConnectionNavigationStyle(string tone) => (Style)Application.Current.Resources[tone switch
    {
        "success" => "OverviewReadyIconStyle",
        "running" => "OverviewAttentionIconStyle",
        "error" => "OverviewErrorIconStyle",
        _ => "OverviewNeutralIconStyle"
    }];

    public static string ConnectionNavigationLabel(string? provider, string connectionLabel) =>
        $"{ConnectionProviderLabel(provider)} · {connectionLabel}";

    public static string ConnectionNavigationTooltip(
        string? provider,
        string connectionLabel,
        string runtimeStatus)
    {
        var providerLabel = ConnectionProviderLabel(provider);
        return string.IsNullOrWhiteSpace(runtimeStatus) || string.Equals(runtimeStatus, connectionLabel, StringComparison.CurrentCulture)
            ? $"{providerLabel} · {connectionLabel}"
            : $"{providerLabel} · {connectionLabel}\n{runtimeStatus}";
    }

    public static string ConnectionNavigationGlyph(string tone) => tone switch
    {
        "success" => "\uE73E", // Check mark
        "running" => "\uE895", // Sync / connecting
        "error" => "\uE783",   // Warning
        _ => "\uE711"          // Disconnected
    };

    private static string ConnectionProviderLabel(string? provider) => ProviderName(provider ?? string.Empty);

    private static string ProviderName(string provider) => provider switch
    {
        "mattermost" => "Mattermost",
        "slack" => "Slack",
        _ => MrtText.Get("NavChat.Content")
    };
}
