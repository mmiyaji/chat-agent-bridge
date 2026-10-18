using ChatAgentBridge_Desktop.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.ModelSelection;
using ChatAgentBridge_Desktop.Localization;
using System.ComponentModel;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class HistoryPage : Page
{
    private bool _loaded;

    public HistoryPageViewModel ViewModel { get; } = new();
    public event Action? AgentSettingsRequested;

    public HistoryPage()
    {
        InitializeComponent();
        ViewModel.ConfirmDetachAsync = ConfirmDetachAsync;
        ViewModel.EditSessionModelAsync = EditSessionModelAsync;
        ViewModel.EditSessionPolicyAsync = EditSessionPolicyAsync;
        Loaded += HistoryPage_Loaded;
    }

    public static Visibility EmptyToVisibility(bool hasItems) =>
        hasItems ? Visibility.Collapsed : Visibility.Visible;

    private void AgentSettingsRequested_Click(object sender, RoutedEventArgs e) => AgentSettingsRequested?.Invoke();

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ViewModel.RefreshAsync();
    }

    private async Task<bool> ConfirmDetachAsync(SessionHistoryRow session)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("ConfirmDetachTitle"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = DesktopText.Get("HistoryProvider", session.Provider), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryChannelId", session.ChannelId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryThreadId", session.ThreadId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistorySessionId", session.SessionId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryDetachWarning"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }
                }
            },
            PrimaryButtonText = DesktopText.Get("Detach"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "DetachSessionConfirmation");
        AutomationProperties.SetName(dialog, DesktopText.Get("ConfirmDetachTitle"));
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<ModelExecutionSelection?> EditSessionModelAsync(SessionHistoryRow session, BridgeSettings globalSettings)
    {
        var model = new TextBox
        {
            Text = session.ModelOverride ?? string.Empty,
            MaxLength = 256,
            PlaceholderText = DesktopText.Get("EnterModelName")
        };
        AutomationProperties.SetAutomationId(model, "SessionModelOverride");
        AutomationProperties.SetName(model, DesktopText.Get("NextExecutionModel"));
        var binding = session.Binding;
        var isAppServer = string.Equals(binding.AgentArguments, "app-server", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(binding.AgentArguments, "claude-stream-json-v1", StringComparison.Ordinal);
        var isCodex = isAppServer || string.Equals(binding.AgentArguments, "exec --json -", StringComparison.Ordinal);
        var provider = isClaude ? AgentProfile.Claude : isCodex ? AgentProfile.Codex : "custom";
        var currentModel = string.IsNullOrWhiteSpace(binding.Model) ? globalSettings.AgentModel : binding.Model;
        var currentEffort = binding.ReasoningEffort ??
            (binding.Model is null || string.Equals(binding.Model, globalSettings.AgentModel, StringComparison.Ordinal)
                ? globalSettings.AgentReasoningEffort : null);
        var efforts = new System.Collections.ObjectModel.ObservableCollection<ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice>
        {
            ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice.InheritDefault
        };
        ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice? selectedEffort = currentEffort is null
            ? ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice.InheritDefault : null;
        var effortStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var unsupportedEffort = false;
        var selectedModelId = currentModel;
        var isApplyingSelection = false;
        var dirtyEffortSelection = false;
        var updatingEffortSelection = false;
        ContentDialog? dialog = null;
        var effortCandidates = new ComboBox
        {
            Header = DesktopText.Get("ReasoningEffortCliOptions"),
            ItemsSource = efforts,
            DisplayMemberPath = "SelectionLabel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = provider != "custom"
        };
        var catalog = new ModelCatalogViewModel(() => new BridgeSettings
        {
            WorkspaceRoot = binding.WorkspaceRoot,
            AgentExecutable = binding.AgentExecutable,
            AgentArguments = isAppServer ? "app-server" : isClaude ? "" : binding.AgentArguments,
            AgentMode = isAppServer ? "app-server" : "exec",
            AgentProvider = provider,
            AgentModel = string.IsNullOrWhiteSpace(model.Text) ? globalSettings.AgentModel : model.Text,
            AgentReasoningEffort = selectedEffort?.Id
        }, option =>
        {
            if (!string.Equals(selectedModelId, option.Id, StringComparison.Ordinal))
            {
                currentEffort = null;
                selectedEffort = null;
                dirtyEffortSelection = true;
            }
            selectedModelId = option.Id;
            isApplyingSelection = true;
            model.Text = option.Id;
            isApplyingSelection = false;
            PopulateEfforts(option);
        });
        if (provider == "custom") catalog.Invalidate(DesktopText.Get("HistoryCustomCliUnsupported"));
        var fetch = new Button { Content = DesktopText.Get("FetchCliModels"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = provider != "custom" };
        AutomationProperties.SetAutomationId(fetch, "SessionFetchModels");
        fetch.Click += (_, _) => catalog.FetchModelsCommand.Execute(null);
        var cancel = new Button { Content = DesktopText.Get("CancelFetch"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(cancel, "SessionCancelFetchModels");
        cancel.Click += (_, _) => catalog.CancelFetchCommand.Execute(null);
        var candidates = new ComboBox
        {
            Header = DesktopText.Get("CliModelSuggestions"),
            ItemsSource = catalog.Models,
            DisplayMemberPath = "SelectionLabel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = provider != "custom"
        };
        AutomationProperties.SetAutomationId(candidates, "SessionModelCandidates");
        AutomationProperties.SetName(candidates, DesktopText.Get("SessionModelSuggestions"));
        candidates.SelectionChanged += (_, _) =>
        {
            if (candidates.SelectedItem is ModelOption selected) catalog.SelectedModel = selected;
        };
        var catalogStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = catalog.Status };
        AutomationProperties.SetAutomationId(catalogStatus, "SessionModelCatalogStatus");
        var progress = new ProgressRing { IsActive = catalog.IsLoading, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        catalog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(catalog.Status)) catalogStatus.Text = catalog.Status;
            if ((args.PropertyName == nameof(catalog.Status) || args.PropertyName == nameof(catalog.IsLoading)) && !catalog.IsLoading)
                PopulateCurrentEfforts();
            if (args.PropertyName == nameof(catalog.IsLoading))
            {
                fetch.IsEnabled = !catalog.IsLoading && provider != "custom";
                cancel.IsEnabled = catalog.IsLoading;
                progress.IsActive = catalog.IsLoading;
            }
        };
        AutomationProperties.SetAutomationId(effortCandidates, "SessionReasoningEffortCandidates");
        AutomationProperties.SetName(effortCandidates, DesktopText.Get("SessionReasoningOptions"));
        effortCandidates.SelectionChanged += (_, _) =>
        {
            if (!updatingEffortSelection && effortCandidates.SelectedItem is ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice chosen)
            {
                selectedEffort = chosen;
                currentEffort = chosen.Id;
                unsupportedEffort = false;
                dirtyEffortSelection = true;
                effortStatus.Text = chosen.Id is null ? DesktopText.Get("FollowAgentSettings") : DesktopText.Get("SelectedReasoningEffort", chosen.SelectionLabel);
                if (dialog is not null) dialog.IsPrimaryButtonEnabled = true;
            }
        };
        AutomationProperties.SetAutomationId(effortStatus, "SessionReasoningEffortStatus");

        void PopulateEfforts(ModelOption? option)
        {
            efforts.Clear();
            efforts.Add(ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice.InheritDefault);
            if (option is not null)
                foreach (var effort in option.SupportedReasoningEfforts)
                    efforts.Add(new(effort.Id, effort.SelectionLabel));
            var matching = currentEffort is null
                ? efforts[0]
                : efforts.FirstOrDefault(choice => string.Equals(choice.Id, currentEffort, StringComparison.Ordinal));
            updatingEffortSelection = true;
            effortCandidates.SelectedItem = matching;
            updatingEffortSelection = false;
            if (option is null)
            {
                unsupportedEffort = false;
                effortStatus.Text = currentEffort is null
                    ? DesktopText.Get("UseCliDefaultGetSuggestionsToCheck")
                    : DesktopText.Get("SavedReasoningUnverified", currentEffort);
                return;
            }
            unsupportedEffort = currentEffort is not null && matching is null;
            if (matching is not null) selectedEffort = matching;
            effortStatus.Text = unsupportedEffort
                ? DesktopText.Get("SavedReasoningNotSupported", currentEffort)
                : currentEffort is null ? DesktopText.Get("FollowAgentSettings") : DesktopText.Get("SavedReasoning", matching!.SelectionLabel);
            if (dialog is not null) dialog.IsPrimaryButtonEnabled = !unsupportedEffort;
        }

        void PopulateCurrentEfforts()
        {
            var effectiveModel = string.IsNullOrWhiteSpace(model.Text) ? globalSettings.AgentModel : model.Text.Trim();
            var option = catalog.Models.FirstOrDefault(item => string.Equals(item.Id, effectiveModel, StringComparison.Ordinal));
            if (!string.Equals(selectedModelId, effectiveModel, StringComparison.Ordinal) && !isApplyingSelection)
            {
                selectedModelId = effectiveModel;
                if (dirtyEffortSelection) selectedEffort = null;
            }
            PopulateEfforts(option);
        }

        model.TextChanged += (_, _) =>
        {
            if (isApplyingSelection) return;
            var nextModel = string.IsNullOrWhiteSpace(model.Text) ? globalSettings.AgentModel : model.Text.Trim();
            if (!string.Equals(selectedModelId, nextModel, StringComparison.Ordinal))
            {
                selectedModelId = nextModel;
                currentEffort = null;
                selectedEffort = null;
                dirtyEffortSelection = true;
                PopulateCurrentEfforts();
            }
        };
        PopulateCurrentEfforts();
        cancel.IsEnabled = false;
        dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("NextExecutionModel"),
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 560,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = DesktopText.Get("SessionModelDialogIntro", session.Conversation),
                            TextWrapping = TextWrapping.Wrap
                        },
                        model,
                        fetch,
                        cancel,
                        progress,
                        candidates,
                        catalogStatus,
                        effortCandidates,
                        effortStatus,
                        new TextBlock { Text = DesktopText.Get("ReasoningEffortOptionsInfo"), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = DesktopText.Get("CliReportedOptionsInfo"), TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = DesktopText.Get("Save"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        dialog.IsPrimaryButtonEnabled = !unsupportedEffort;
        AutomationProperties.SetAutomationId(dialog, "SessionModelDialog");
        AutomationProperties.SetName(dialog, DesktopText.Get("SetNextExecutionModel"));
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return new ModelExecutionSelection(
                string.IsNullOrWhiteSpace(model.Text) ? null : model.Text.Trim(),
                dirtyEffortSelection ? selectedEffort?.Id : binding.ReasoningEffort);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    private async Task<SessionPolicySelection?> EditSessionPolicyAsync(SessionHistoryRow session, BridgeSettings globalSettings)
    {
        ComboBox CreateChoice(string automationId, string header, string? current, params (string? Value, string Label)[] choices)
        {
            var combo = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedValuePath = "Tag" };
            foreach (var (value, label) in choices)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = value ?? "<global>" });
            combo.SelectedValue = current ?? "<global>";
            AutomationProperties.SetAutomationId(combo, automationId);
            AutomationProperties.SetName(combo, header);
            return combo;
        }

        var binding = session.Binding;
        var approval = CreateChoice("SessionChatApprovalMode", DesktopText.Get("ConversationRequestApproval"), binding.ChatApprovalMode,
            (null, DesktopText.Get("UseGlobalSettings")), ("always", DesktopText.Get("AskEveryTime")), ("first", DesktopText.Get("AskFirstTime")), ("auto", DesktopText.Get("AutoApprove")));
        var result = CreateChoice("SessionChatResultMode", DesktopText.Get("ExecutionResultSharing"), binding.ChatResultMode,
            (null, DesktopText.Get("UseGlobalSettings")), ("manual", DesktopText.Get("ManualShare")), ("auto", DesktopText.Get("AutoShare")), ("none", DesktopText.Get("NoSharing")));
        var permission = CreateChoice("SessionAgentPermissionMode", DesktopText.Get("AiToolPermission"), binding.AgentPermissionMode,
            (null, DesktopText.Get("UseGlobalSettings")), ("inherit", DesktopText.Get("CliDefault")), ("read-only", DesktopText.Get("ReadAndPlanOnly")),
            ("workspace-write", DesktopText.Get("AutoAllowWorkspaceEdits")), ("full-access", DesktopText.Get("FullAccess")), ("ask", DesktopText.Get("AskPerAction")));
        foreach (var item in permission.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, "ask", StringComparison.Ordinal))
                item.IsEnabled = string.Equals(binding.AgentArguments, "app-server", StringComparison.OrdinalIgnoreCase);
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("ConversationExecutionPolicy"),
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 520,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = DesktopText.Get("ConversationPolicyIntro", session.Conversation, ApprovalLabel(globalSettings.ChatApprovalMode), ResultLabel(globalSettings.ChatResultMode), PermissionLabel(globalSettings.AgentPermissionMode)), TextWrapping = TextWrapping.Wrap },
                        approval,
                        result,
                        permission,
                        new TextBlock { Text = DesktopText.Get("ConversationPolicyWarning"), TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = DesktopText.Get("Save"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "SessionExecutionPolicyDialog");
        AutomationProperties.SetName(dialog, DesktopText.Get("SetConversationExecutionPolicy"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        static string? ReadChoice(ComboBox combo) => string.Equals(combo.SelectedValue as string, "<global>", StringComparison.Ordinal) ? null : combo.SelectedValue as string;
        return new SessionPolicySelection(ReadChoice(approval), ReadChoice(result), ReadChoice(permission));
    }

    private static string ApprovalLabel(string value) => value switch { "first" => DesktopText.Get("AskFirstTime"), "auto" => DesktopText.Get("AutoApprove"), _ => DesktopText.Get("AskEveryTime") };
    private static string ResultLabel(string value) => value switch { "auto" => DesktopText.Get("AutoShare"), "none" => DesktopText.Get("NoSharing"), _ => DesktopText.Get("ManualShare") };
    private static string PermissionLabel(string value) => value switch
    {
        "read-only" => DesktopText.Get("ReadAndPlanOnly"), "workspace-write" => DesktopText.Get("AutoAllowWorkspaceEdits"),
        "full-access" => DesktopText.Get("FullAccess"), "ask" => DesktopText.Get("AskPerAction"), _ => DesktopText.Get("CliDefault")
    };
}
