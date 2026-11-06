using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private static string ScopeIconSlug(string key) => key switch
        {
            "common" => "folder",
            "codex" => "provider_codex",
            "claude" => "provider_claude",
            "gemini" => "provider_gemini",
            "github-copilot" => "provider_copilot",
            "antigravity" => "provider_antigravity",
            "kiro" => "provider_kiro",
            "qwen" => "provider_qwen",
            "kimi" => "provider_kimi",
            "muse" => "provider_muse",
            "mattermost" => "provider_mattermost",
            "slack" => "provider_slack",
            "discord" => "provider_discord",
            _ => "apps"
        };

    // General tiles show the saved agent's or chat service's own icon; without one, a common glyph.
    public static FrameworkElement OverviewAgentIcon(string provider) => OverviewProviderIcon(provider, "\uE756");
    public static FrameworkElement OverviewChatIcon(string provider) => OverviewProviderIcon(provider, ((char)Symbol.Message).ToString());

    private static FrameworkElement OverviewProviderIcon(string provider, string fallbackGlyph)
    {
        var slug = ScopeIconSlug(provider);
        if (string.IsNullOrEmpty(provider) || !slug.StartsWith("provider_", StringComparison.Ordinal))
            return new FontIcon { Glyph = fallbackGlyph, FontSize = 18, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var icon = (FrameworkElement)((DataTemplate)Application.Current.Resources["SettingsScopeIcon_" + slug]).LoadContent();
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

    private static Grid ScopeNavigationLabel(string key, string text)
    {
        var slug = ScopeIconSlug(key);
        // Scale the original geometry into its own box. A glyph with a smaller
        // fixed Width/Height clips; it does not scale its internal drawing.
        // Each template creates its own geometry; WinUI geometry cannot be
        // attached to multiple Path/PathIcon instances.
        var icon = (FrameworkElement)((DataTemplate)Application.Current.Resources["SettingsScopeIcon_" + slug]).LoadContent();
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var marker = new FontIcon
        {
            Name = "CurrentScopeMarker",
            Glyph = "\uE73E",
            Style = (Style)Application.Current.Resources["CurrentScopeMarkerStyle"],
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAccessibilityView(marker, AccessibilityView.Raw);
        Grid.SetColumn(marker, 2);
        row.Children.Add(marker);
        return row;
    }

    private static void SetScopeNavigationMarker(NavigationView? navigation, string key, bool isCurrent, string tooltip, string currentName)
    {
        if (navigation is null) return;
        // These inner panes have no compact icon-only mode: keep their native compact layout
        // as wide as the open pane even while the outer app navigation changes modes.
        if (navigation.CompactPaneLength != navigation.OpenPaneLength)
            navigation.CompactPaneLength = navigation.OpenPaneLength;
        var item = navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(candidate => Equals(candidate.Tag, key));
        if (item is null) return;
        // Keep nested items sized to their own pane when the outer NavigationView becomes compact.
        item.Width = navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left
            ? Math.Max(40, navigation.OpenPaneLength - 8)
            : double.NaN;
        var label = item.Content as Grid;
        var marker = label?.Children.OfType<FontIcon>().FirstOrDefault(icon => icon.Name == "CurrentScopeMarker");
        var showMarker = isCurrent && navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left;
        if (label?.Children.OfType<TextBlock>().FirstOrDefault() is { } text)
            text.MaxWidth = Math.Max(40, navigation.OpenPaneLength - (showMarker ? 80 : 64));
        if (marker is not null)
        {
            AutomationProperties.SetAutomationId(marker, AutomationProperties.GetAutomationId(item) + "CurrentMarker");
            marker.Visibility = showMarker ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(marker, tooltip);
        }
        AutomationProperties.SetName(item, isCurrent ? $"{GetScopeLabel(label)}, {currentName}" : GetScopeLabel(label));
    }

    private static string GetScopeLabel(Grid? label) => label?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;

}
