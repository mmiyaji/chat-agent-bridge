using ChatAgentBridge_Desktop.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.ModelSelection;
using ChatAgentBridge_Desktop.Localization;
using System.ComponentModel;

namespace ChatAgentBridge_Desktop.History;

public sealed partial class HistoryPage : Page
{
    private const double DefaultSessionListWidth = 380;
    private const double MinimumSessionListWidth = 280;
    private const double MinimumSessionDetailWidth = 320;
    private const double SessionSplitterWidth = 12;
    private bool _showDetailOnNarrow;
    private bool _isResizingSessionList;
    private double _preferredSessionListWidth = DefaultSessionListWidth;
    private ChatAgentBridge.Core.Sessions.SessionKey? _lastDetailKey;
    private bool _pricingUpdatesEnabled;
    public HistoryPageViewModel ViewModel { get; } = new();
    public event Action? AgentSettingsRequested;

    public HistoryPage()
    {
        InitializeComponent();
        SessionListPane.SizeChanged += SessionListPane_SizeChanged;
        SessionBrowser.SizeChanged += SessionBrowser_SizeChanged;
        SessionSplitter.AddHandler(UIElement.ManipulationStartedEvent,
            new ManipulationStartedEventHandler(SessionSplitter_ManipulationStarted), true);
        SessionSplitter.AddHandler(UIElement.ManipulationCompletedEvent,
            new ManipulationCompletedEventHandler(SessionSplitter_ManipulationCompleted), true);
        SessionSplitter.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SessionSplitter_KeyDown), true);
        SessionSplitter.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(SessionSplitter_KeyUp), true);
        var activity = new ActivityDashboard(ViewModel);
        activity.OpenDayRequested += () =>
        {
            ViewModel.ShowExecutionsForActivityDay();
            HistoryPivot.SelectedItem = ExecutionsTab;
        };
        ActivityHost.Content = activity;
        ViewModel.ConfirmDetachAsync = ConfirmDetachAsync;
        ViewModel.EditSessionModelAsync = EditSessionModelAsync;
        ViewModel.EditSessionPolicyAsync = EditSessionPolicyAsync;
        ViewModel.RenameSessionAsync = RenameSessionAsync;
        ViewModel.ChooseAgentSwitchAsync = ChooseAgentSwitchAsync;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SizeChanged += Page_SizeChanged;
        // MainPage hosts this Page directly and switches Visibility, so Frame
        // navigation events alone do not cover its actual lifetime.
        Loaded += (_, _) => SetPricingUpdatesEnabled(true);
        Unloaded += (_, _) =>
        {
            SetPricingUpdatesEnabled(false);
            SessionFiltersFlyout.Hide();
        };
        UpdateResponsiveLayout();
    }

    public static Visibility EmptyToVisibility(bool hasItems) =>
        hasItems ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void AgentSettingsRequested_Click(object sender, RoutedEventArgs e) => AgentSettingsRequested?.Invoke();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.SetChatNameUpdatesEnabled(true);
        await ViewModel.RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.SetChatNameUpdatesEnabled(false);
        base.OnNavigatedFrom(e);
    }

    private void SetPricingUpdatesEnabled(bool enabled)
    {
        if (_pricingUpdatesEnabled == enabled) return;
        _pricingUpdatesEnabled = enabled;
        if (enabled)
        {
            App.Pricing.Changed += Pricing_Changed;
            ViewModel.RefreshPricing();
        }
        else App.Pricing.Changed -= Pricing_Changed;
    }

    private void Pricing_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_pricingUpdatesEnabled) ViewModel.RefreshPricing();
        });
    }

    private void SavedSessionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SessionHistoryRow row) return;
        ViewModel.SelectedSession = row;
        _showDetailOnNarrow = true;
        UpdateResponsiveLayout();
        if (ViewModel.IsNarrow) DispatcherQueue.TryEnqueue(() => BackToSessionsButton.Focus(FocusState.Programmatic));
    }

    private void SavedSessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: SessionHistoryRow row }) ViewModel.SelectedSession = row;
    }

    private void SavedSessionsList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && ViewModel.SelectedSession is not null)
        {
            _showDetailOnNarrow = true;
            UpdateResponsiveLayout();
            if (ViewModel.IsNarrow) DispatcherQueue.TryEnqueue(() => BackToSessionsButton.Focus(FocusState.Programmatic));
            e.Handled = true;
        }
    }

    private void HistoryPivot_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateResponsiveLayout();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedSession))
        {
            if (ViewModel.SelectedSession is null) _showDetailOnNarrow = false;
            if (ViewModel.SelectedSession is { } selected && _lastDetailKey != selected.Binding.Key)
            {
                _lastDetailKey = selected.Binding.Key;
                DispatcherQueue.TryEnqueue(() => SessionDetailScroll.ChangeView(null, 0, null, true));
            }
        }
        if (e.PropertyName is nameof(ViewModel.SelectedSession) or nameof(ViewModel.IsNarrow))
            UpdateResponsiveLayout();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.IsNarrow = e.NewSize.Width < 800;
        UpdateResponsiveLayout();
    }

    private void SessionBrowser_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ViewModel.IsNarrow) ApplySessionColumnWidths();
    }

    private void SessionListPane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isResizingSessionList && !ViewModel.IsNarrow && e.NewSize.Width >= MinimumSessionListWidth)
            _preferredSessionListWidth = e.NewSize.Width;
    }

    private void SessionSplitter_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e) =>
        _isResizingSessionList = true;

    private void SessionSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        FinishSessionListResize();

    private void SessionSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
            _isResizingSessionList = true;
    }

    private void SessionSplitter_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
            FinishSessionListResize();
    }

    private void FinishSessionListResize()
    {
        if (!_isResizingSessionList) return;
        if (!ViewModel.IsNarrow && SessionListPane.ActualWidth >= MinimumSessionListWidth)
            _preferredSessionListWidth = SessionListPane.ActualWidth;
        _isResizingSessionList = false;
        ApplySessionColumnWidths();
    }

    private void ApplySessionColumnWidths()
    {
        if (SessionBrowser is null || ViewModel.IsNarrow || SessionBrowser.ActualWidth <= 0) return;
        var spacing = SessionBrowser.ColumnSpacing * 2;
        var contentWidth = SessionBrowser.ActualWidth - spacing - SessionSplitterWidth;
        var maxListWidth = Math.Max(MinimumSessionListWidth,
            contentWidth - MinimumSessionDetailWidth);
        var width = Math.Clamp(_preferredSessionListWidth, MinimumSessionListWidth, maxListWidth);
        // Star columns keep the grid within its parent's viewport while a window
        // resize is being arranged. A fixed list column can inflate the grid's
        // desired width before SizeChanged has a chance to clamp it.
        SessionListColumn.Width = new GridLength(width, GridUnitType.Star);
        SessionSplitterColumn.Width = new GridLength(SessionSplitterWidth, GridUnitType.Pixel);
        SessionDetailColumn.Width = new GridLength(Math.Max(MinimumSessionDetailWidth, contentWidth - width), GridUnitType.Star);
    }

    private void BackToSessions_Click(object sender, RoutedEventArgs e)
    {
        _showDetailOnNarrow = false;
        UpdateResponsiveLayout();
        SessionList.Focus(FocusState.Programmatic);
    }

    private void UpdateResponsiveLayout()
    {
        if (SessionListPane is null || SessionDetailPane is null) return;
        var narrow = ViewModel.IsNarrow;
        var showDetail = ViewModel.SelectedSession is not null && _showDetailOnNarrow;
        if (!ReferenceEquals(HistoryPivot.SelectedItem, ConversationsTab) || (narrow && showDetail))
            SessionFiltersFlyout.Hide();
        SessionListColumn.MinWidth = narrow ? 0 : MinimumSessionListWidth;
        SessionDetailColumn.MinWidth = narrow ? 0 : MinimumSessionDetailWidth;
        SessionListPane.Visibility = narrow && showDetail ? Visibility.Collapsed : Visibility.Visible;
        SessionDetailPane.Visibility = narrow && !showDetail ? Visibility.Collapsed : Visibility.Visible;
        SessionSplitter.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        if (narrow) SessionListColumn.Width = new GridLength(1, GridUnitType.Star);
        SessionSplitterColumn.Width = new GridLength(narrow ? 0 : SessionSplitterWidth, GridUnitType.Pixel);
        if (narrow) SessionDetailColumn.Width = new GridLength(0, GridUnitType.Pixel);
        Grid.SetColumn(SessionDetailPane, narrow ? 0 : 2);
        Grid.SetColumnSpan(SessionDetailPane, narrow ? 3 : 1);
        Grid.SetColumnSpan(SessionListPane, narrow ? 3 : 1);
        if (!narrow) ApplySessionColumnWidths();
        var compactHeader = ActualWidth < 600;
        HistoryTitle.Style = (Style)Application.Current.Resources[compactHeader ? "SubtitleTextBlockStyle" : "TitleTextBlockStyle"];
        HistoryDescription.Visibility = compactHeader ? Visibility.Collapsed : Visibility.Visible;
        HistoryAgentSettingsButton.Visibility = compactHeader ? Visibility.Collapsed : Visibility.Visible;
        HistoryStatusText.MaxLines = compactHeader ? 1 : 0;
        SessionFilters.Visibility = narrow && showDetail ? Visibility.Collapsed : Visibility.Visible;
        if (BackToSessionsButton is not null)
            BackToSessionsButton.Visibility = narrow && showDetail ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<string?> RenameSessionAsync(SessionHistoryRow session)
    {
        var name = new TextBox
        {
            Header = DesktopText.Get("HistoryRenameNameLabel"),
            Text = session.Binding.DisplayName ?? string.Empty,
            PlaceholderText = session.WorkspaceName,
            MaxLength = 80,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(name, "SessionDisplayName");
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("HistoryRenameSession"),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = session.ListIdentity, TextWrapping = TextWrapping.Wrap },
                    name,
                    new TextBlock { Text = DesktopText.Get("HistoryRenameHelp"), TextWrapping = TextWrapping.Wrap }
                }
            },
            PrimaryButtonText = DesktopText.Get("Save"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AutomationProperties.SetAutomationId(dialog, "RenameSessionDialog");
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !name.Text.Any(char.IsControl);
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? name.Text : null;
    }

    private async Task<bool> ConfirmDetachAsync(SessionHistoryRow session)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("HistoryStartFreshTitle"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = DesktopText.Get("HistoryProvider", session.Provider), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryChannelId", session.ChannelId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryThreadId", session.ThreadId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistorySessionId", session.SessionId), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = DesktopText.Get("HistoryStartFreshWarning"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }
                }
            },
            PrimaryButtonText = DesktopText.Get("HistoryStartFresh"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "DetachSessionConfirmation");
        AutomationProperties.SetName(dialog, DesktopText.Get("HistoryStartFreshTitle"));
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<ModelExecutionSelection?> EditSessionModelAsync(SessionHistoryRow session, BridgeSettings globalSettings)
    {
        var model = new TextBox
        {
            Text = session.ModelOverride ?? string.Empty,
            MaxLength = 256,
            Header = DesktopText.Get("NextExecutionModel"),
            PlaceholderText = DesktopText.Get("EnterModelName")
        };
        AutomationProperties.SetAutomationId(model, "SessionModelOverride");
        AutomationProperties.SetName(model, DesktopText.Get("NextExecutionModel"));
        var binding = session.Binding;
        var isAppServer = string.Equals(binding.AgentArguments, "app-server", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(binding.AgentArguments, "claude-stream-json-v1", StringComparison.Ordinal);
        var provider = ConversationAgentService.ProviderOf(binding) ?? AgentProfile.Custom;
        var supportsCatalog = provider is AgentProfile.Codex or AgentProfile.Claude;
        var isSessionCli = AgentProfile.IsBasicCliProvider(provider);
        var currentModel = string.IsNullOrWhiteSpace(binding.Model) ? globalSettings.AgentModel : binding.Model;
        var currentEffort = !supportsCatalog ? null : binding.ReasoningEffort ??
            (binding.Model is null || string.Equals(binding.Model, globalSettings.AgentModel, StringComparison.Ordinal)
                ? globalSettings.AgentReasoningEffort : null);
        var efforts = new System.Collections.ObjectModel.ObservableCollection<ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice>
        {
            ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice.InheritDefault
        };
        ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice? selectedEffort = currentEffort is null
            ? ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice.InheritDefault : null;
        var effortStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = supportsCatalog ? Visibility.Visible : Visibility.Collapsed };
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
            IsEnabled = supportsCatalog,
            Visibility = supportsCatalog ? Visibility.Visible : Visibility.Collapsed
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
        if (!supportsCatalog) catalog.Invalidate(DesktopText.Get(isSessionCli ? "ManualModelCandidatesUnavailable" : "HistoryCustomCliUnsupported"));
        var fetch = new Button { Content = DesktopText.Get("FetchCliModels"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = supportsCatalog };
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
            IsEnabled = supportsCatalog
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
                fetch.IsEnabled = !catalog.IsLoading && supportsCatalog;
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
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 12, 0),
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
                        new TextBlock { Text = DesktopText.Get("ReasoningEffortOptionsInfo"), TextWrapping = TextWrapping.Wrap, Visibility = supportsCatalog ? Visibility.Visible : Visibility.Collapsed },
                        new TextBlock { Text = DesktopText.Get("CliReportedOptionsInfo"), TextWrapping = TextWrapping.Wrap, Visibility = supportsCatalog ? Visibility.Visible : Visibility.Collapsed }
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
                supportsCatalog ? (dirtyEffortSelection ? selectedEffort?.Id : binding.ReasoningEffort) : null);
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
        var cliManagedPermissions = AgentProfile.IsBasicCliProvider(ConversationAgentService.ProviderOf(binding) ?? AgentProfile.Custom);
        foreach (var item in permission.Items.OfType<ComboBoxItem>())
        {
            if (cliManagedPermissions) item.IsEnabled = item.Tag as string is "<global>" or "inherit";
            else if (string.Equals(item.Tag as string, "ask", StringComparison.Ordinal))
                item.IsEnabled = string.Equals(binding.AgentArguments, "app-server", StringComparison.OrdinalIgnoreCase);
        }
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
