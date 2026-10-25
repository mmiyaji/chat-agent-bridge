using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

/// <summary>Warns before folder and agent switch commands from chat may run without approval on this PC.</summary>
internal static class SwitchApprovalWarningDialog
{
    /// <returns>True only when the user explicitly accepted the risk.</returns>
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot)
    {
        var body = new StackPanel { Spacing = 12 };
        AddText(body, "SwitchApprovalWarningBody", DesktopText.Get("SwitchApprovalWarningBody"));
        AddText(body, "SwitchApprovalWarningRisk", "• " + DesktopText.Get("SwitchApprovalWarningRisk"));
        AddText(body, "SwitchApprovalWarningLimits", "• " + DesktopText.Get("SwitchApprovalWarningLimits"));
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = DesktopText.Get("SwitchApprovalWarningTitle"),
            PrimaryButtonText = DesktopText.Get("SwitchApprovalWarningEnable"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = Math.Max(120, Math.Min(420, xamlRoot.Size.Height - 240)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        AutomationProperties.SetAutomationId(dialog, "SwitchApprovalWarningDialog");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void AddText(StackPanel panel, string id, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(block, id);
        panel.Children.Add(block);
    }
}
