using ChatAgentBridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class HistoryPage
{
    private async Task<AgentSwitchSelection?> ChooseAgentSwitchAsync(SessionHistoryRow session,
        IReadOnlyList<AgentConnectionProfile> profiles, ConversationSummary? summary)
    {
        var current = session.Binding.NextAgentProvider ?? ConversationAgentService.ProviderOf(session.Binding);
        var choices = profiles.Where(profile => session.Binding.NextAgentProvider is not null || profile.Provider != current).ToArray();
        var picker = new ComboBox
        {
            Header = DesktopText.Get("AgentSwitchConnection"), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = choices.Select(profile => profile.Provider switch
            {
                AgentProfile.Codex => "Codex", AgentProfile.Claude => "Claude Code", AgentProfile.Gemini => "Gemini CLI",
                AgentProfile.GitHubCopilot => "GitHub Copilot CLI", _ => profile.Provider
            }).ToArray(),
            SelectedIndex = choices.Length > 0 ? Math.Max(0, Array.FindIndex(choices, profile => profile.Provider == current)) : -1
        };
        AutomationProperties.SetAutomationId(picker, "AgentSwitchConnection");
        var include = new CheckBox
        {
            Content = new TextBlock { Text = DesktopText.Get("AgentSummaryYes"), TextWrapping = TextWrapping.Wrap },
            IsChecked = false, IsEnabled = summary is not null
        };
        AutomationProperties.SetAutomationId(include, "AgentSwitchIncludeSummary");
        AutomationProperties.SetName(include, DesktopText.Get("AgentSummaryYes"));
        var preview = new TextBox
        {
            AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = summary?.Text ?? "",
            MinHeight = 120, MaxHeight = 180, Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(preview, "AgentSwitchSummaryPreview");
        AutomationProperties.SetName(preview, DesktopText.Get("AgentSummaryPreview"));
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateDetails() => details.Text = picker.SelectedIndex >= 0
            ? DesktopText.Get("AgentSwitchConfirm", choices[picker.SelectedIndex].Provider,
                choices[picker.SelectedIndex].Executable, choices[picker.SelectedIndex].Model ?? DesktopText.Get("CliDefault"),
                SessionHistoryRow.PermissionLabel(choices[picker.SelectedIndex].PermissionMode), session.Workspace, DesktopText.Get(include.IsChecked == true ? "AgentSummaryYes" : "AgentSummaryNo"))
            : DesktopText.Get("AgentSwitchRegisterFirst");
        include.Checked += (_, _) => { preview.Visibility = Visibility.Visible; UpdateDetails(); };
        include.Unchecked += (_, _) => { preview.Visibility = Visibility.Collapsed; UpdateDetails(); };
        picker.SelectionChanged += (_, _) => UpdateDetails();
        UpdateDetails();
        var connectionDetails = new Expander
        {
            Header = DesktopText.Get("AgentSwitchDetails"), Content = details,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(connectionDetails, "AgentSwitchConnectionDetails");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = App.Appearance.RequestedTheme,
            Title = DesktopText.Get("AgentSwitchTitle"), PrimaryButtonText = DesktopText.Get("Change"),
            CloseButtonText = DesktopText.Get("Cancel"), DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = choices.Length > 0,
            Content = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 440, Padding = new Thickness(0, 0, 12, 0),
                Content = new StackPanel
                {
                    Spacing = 12, Children =
                    {
                        picker,
                        new TextBlock { Text = DesktopText.Get("AgentSwitchIntro"), TextWrapping = TextWrapping.Wrap },
                        connectionDetails,
                        include,
                        new TextBlock { Text = DesktopText.Get(summary is null ? "AgentSummaryUnavailable" : "AgentSummaryHelp"), TextWrapping = TextWrapping.Wrap },
                        preview
                    }
                }
            }
        };
        AutomationProperties.SetAutomationId(dialog, "AgentSwitchDialog");
        AutomationProperties.SetAutomationId((ScrollViewer)dialog.Content, "AgentSwitchScroll");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedIndex < 0) return null;
        var chosen = choices[picker.SelectedIndex];
        return new(chosen.Provider, include.IsChecked == true, chosen);
    }
}
