using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    // Tone also refreshes this label when the nullable provider is cleared.
    // Without a provider the footer names the connection, not the Chat page listed above it.
    public static string ConnectionNavigationServiceLabel(string? provider, string tone) =>
        string.IsNullOrEmpty(provider) ? DesktopText.Get("SidebarConnectionNone") : ConnectionProviderLabel(provider);

    public static string ConnectionNavigationStateLabel(string tone) =>
        DesktopText.Get(tone switch
        {
            "success" => "SidebarConnectionConnected",
            "running" => "SidebarConnectionConnecting",
            "error" => "SidebarConnectionError",
            _ => "SidebarConnectionDisconnected"
        });

    public static Style ConnectionNavigationBadgeStyle(string tone) => (Style)Application.Current.Resources[tone switch
    {
        "success" => "ConnectionNavigationReadyBadgeStyle",
        "running" => "ConnectionNavigationWorkingBadgeStyle",
        "error" => "ConnectionNavigationErrorBadgeStyle",
        _ => "ConnectionNavigationNeutralBadgeStyle"
    }];

    // Fresh vector geometry preserves theme foregrounds without bitmap resampling.
    // The tone also triggers refresh: compiled function
    // bindings skip a nullable provider's transition to null on disconnection.
    public static IconElement ConnectionNavigationIcon(string? provider, string tone)
    {
        IconElement icon = provider switch
        {
            "mattermost" or "slack" or "discord" =>
                (IconElement)((DataTemplate)Application.Current.Resources["ConnectionProviderIcon_" + provider]).LoadContent(),
            _ => new SymbolIcon(Symbol.Message)
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

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

    private static string ConnectionProviderLabel(string? provider) => ProviderName(provider ?? string.Empty);

    private static string ProviderName(string provider) => provider switch
    {
        "mattermost" => "Mattermost",
        "slack" => "Slack",
        "discord" => "Discord",
        _ => MrtText.Get("NavChat.Content")
    };
}
