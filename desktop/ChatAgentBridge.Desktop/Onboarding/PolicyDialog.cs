using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Onboarding;

public static class PolicyDialog
{
    private static bool _isOpen;
    public static async Task ShowAsync(XamlRoot xamlRoot, bool privacy)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        if (_isOpen) return;
        _isOpen = true;
        try
        {
            var body = new TextBlock
            {
                Text = $"{(privacy ? PolicyText.Privacy : PolicyText.Terms)}\n\n版: {PolicyText.Version}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            AutomationProperties.SetAutomationId(body, "PolicyBody");

            var scroll = new ScrollViewer
            {
                Content = body,
                MaxHeight = Math.Max(120, Math.Min(440, xamlRoot.Size.Height - 240)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            AutomationProperties.SetAutomationId(scroll, "PolicyScroll");

            var close = new Button { Content = "閉じる", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(close, "PolicyClose");
            var panel = new StackPanel { Spacing = 16 };
            panel.Children.Add(scroll);
            panel.Children.Add(close);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = privacy ? "プライバシーポリシー" : "利用規約",
                Content = panel
            };
            AutomationProperties.SetAutomationId(dialog, "PolicyDialog");
            close.Click += (_, _) => dialog.Hide();
            await dialog.ShowAsync();
        }
        finally { _isOpen = false; }
    }

}
