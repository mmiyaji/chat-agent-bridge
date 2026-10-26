using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

internal static class CliTrustDialog
{
    private static bool _isOpen;

    public static async Task ShowAsync(MainPageViewModel viewModel, XamlRoot xamlRoot, bool recheck = true)
    {
        var recovery = viewModel.GetCliTrustRecovery();
        if (_isOpen || recovery is null) return;
        _isOpen = true;
        try
        {
            var body = new StackPanel { Spacing = 12 };
            AddText(body, "CliTrustExplanation", DesktopText.Get("CliTrustExplanation"));
            if (recovery.UsesLinkTarget)
            {
                AddText(body, "CliTrustLinkExplanation", DesktopText.Get("CliTrustLinkExplanation"));
                AddText(body, "CliTrustOriginalPath", DesktopText.Get("CliTrustOriginalPath", recovery.WorkspacePath));
            }
            AddText(body, "CliTrustTargetPath", DesktopText.Get("CliTrustTargetPath", recovery.TargetPath));
            AddText(body, "CliTrustSaveHelp", DesktopText.Get(recheck ? "CliTrustSaveHelp" : "CliTrustWizardSaveHelp"));
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = App.Appearance.RequestedTheme,
                Title = DesktopText.Get("CliTrustTitle"),
                PrimaryButtonText = DesktopText.Get(recheck
                    ? (recovery.UsesLinkTarget ? "CliTrustUseTarget" : "CliTrustAdd")
                    : (recovery.UsesLinkTarget ? "CliTrustUseTargetOnly" : "CliTrustAddOnly")),
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
            AutomationProperties.SetAutomationId(dialog, "CliTrustDialog");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && await viewModel.TryApplyCliTrustRecoveryAsync(recovery) && recheck)
                await viewModel.CheckCliCommand.ExecuteAsync(null);
        }
        finally { _isOpen = false; }
    }

    private static void AddText(StackPanel panel, string id, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(block, id);
        panel.Children.Add(block);
    }
}
