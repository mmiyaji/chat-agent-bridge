using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop;

/// <summary>Explains the live follow-up mode and, on consent, applies the settings it depends on to the draft.</summary>
internal static class FollowUpSteerDialog
{
    /// <returns>True when the steer mode was enabled; false when the user declined or the provider is unsupported.</returns>
    public static async Task<bool> ShowAsync(MainPageViewModel viewModel, XamlRoot xamlRoot)
    {
        var requirements = viewModel.GetFollowUpSteerRequirements();
        var body = new StackPanel { Spacing = 12 };
        if (!requirements.ProviderSupported)
        {
            AddText(body, "FollowUpSteerUnsupported",
                DesktopText.Get("FollowUpSteerUnsupported", viewModel.AgentProviderDisplayName));
        }
        else
        {
            AddText(body, "FollowUpSteerExplanation", DesktopText.Get("FollowUpSteerExplanation"));
            AddText(body, "FollowUpSteerTokenNote", DesktopText.Get("FollowUpSteerTokenNote"));
            if (requirements.HasChanges)
            {
                AddText(body, "FollowUpSteerChangesHeader", DesktopText.Get("FollowUpSteerChangesHeader"));
                if (requirements.RequiresAppServer)
                    AddText(body, "FollowUpSteerChangeAppServer", "• " + DesktopText.Get("FollowUpSteerChangeAppServer"));
                if (requirements.RequiresApprovalChange)
                    AddText(body, "FollowUpSteerChangeApproval", "• " + DesktopText.Get("FollowUpSteerChangeApproval",
                        viewModel.CurrentChatApprovalLabel, MainPageViewModel.SteerApprovalLabel));
            }
            else AddText(body, "FollowUpSteerNoChanges", DesktopText.Get("FollowUpSteerNoChanges"));
        }
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = App.Appearance.RequestedTheme,
            Title = DesktopText.Get("FollowUpSteerTitle"),
            PrimaryButtonText = requirements.ProviderSupported
                ? DesktopText.Get(requirements.HasChanges ? "FollowUpSteerApply" : "FollowUpSteerEnable") : "",
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
        AutomationProperties.SetAutomationId(dialog, "FollowUpSteerDialog");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !requirements.ProviderSupported) return false;
        viewModel.ApplyFollowUpSteer(requirements);
        return true;
    }

    private static void AddText(StackPanel panel, string id, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(block, id);
        panel.Children.Add(block);
    }
}
