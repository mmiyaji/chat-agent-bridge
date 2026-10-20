using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

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
                Text = DesktopText.Get("PolicyDocument", privacy ? PolicyText.Privacy : PolicyText.Terms,
                    Environment.NewLine + Environment.NewLine, PolicyText.Version),
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

            var dialog = new ContentDialog
            {
                RequestedTheme = App.Appearance.RequestedTheme,
                XamlRoot = xamlRoot,
                Title = DesktopText.Get(privacy ? "PrivacyPolicy" : "TermsOfUse"),
                Content = scroll,
                CloseButtonText = DesktopText.Get("Close"),
                DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "PolicyDialog");
            await dialog.ShowAsync();
        }
        finally { _isOpen = false; }
    }

}
