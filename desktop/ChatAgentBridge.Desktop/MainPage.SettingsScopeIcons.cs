using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

public sealed partial class MainPage
{
    private static Grid ScopeNavigationLabel(string key, string text)
    {
        var slug = key switch
        {
            "common" => "folder",
            "codex" => "window_console",
            "claude" => "code",
            "gemini" => "sparkle",
            "github-copilot" => "bot",
            "antigravity" => "arch",
            "mattermost" => "chat",
            "slack" => "number_symbol",
            "discord" => "people_community",
            _ => "apps"
        };
        // Scale the original geometry into its own box. A glyph with a smaller
        // fixed Width/Height clips; it does not scale its internal drawing.
        // Each template creates its own geometry; WinUI geometry cannot be
        // attached to multiple Path/PathIcon instances.
        var icon = (FrameworkElement)((DataTemplate)Application.Current.Resources["SettingsScopeIcon_" + slug]).LoadContent();
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }
}
