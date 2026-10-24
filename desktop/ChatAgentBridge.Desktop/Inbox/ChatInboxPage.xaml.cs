using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using ChatAgentBridge_Desktop.Localization;
using System.Collections.Specialized;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxPage : Page
{
    public ChatInboxViewModel ViewModel { get; } = new();
    public event Action? OpenRequested;
    public event Action? ConnectionSettingsRequested;
    private bool _windowClosedSubscribed;
    private bool _openAppCallbackConfigured;
    private bool _searchSuggestionsAllowed;
    private string _lastUserSearchText = "";

    private void OpenConnectionSettings_Click(object sender, RoutedEventArgs e) => ConnectionSettingsRequested?.Invoke();

    public ChatInboxPage()
    {
        InitializeComponent();
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.ConfirmReplyAsync = ConfirmReplyAsync;
        ViewModel.ConfirmAttachmentsAsync = ConfirmAttachmentsAsync;
        var serverDialogs = new AppServerUi.AppServerDialogs(() => XamlRoot);
        ViewModel.ServerApprovalHandler = serverDialogs.ApproveAsync;
        ViewModel.ServerInputHandler = serverDialogs.InputAsync;
        ViewModel.RequestReceived += NotifyRequest;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.Selected))
                DispatcherQueue.TryEnqueue(() =>
                {
                    InboxApprovalHelpTip.IsOpen = false;
                    InboxDetailScroll.ChangeView(null, 0, null, true);
                });
            else if (args.PropertyName == nameof(ViewModel.SelectedConversation))
                DispatcherQueue.TryEnqueue(() => InboxApprovalHelpTip.IsOpen = false);
        };
        ViewModel.ConnectionEnded += () => App.PendingRequestNotifications.ClearAll();
        App.PendingRequestNotifications.StatusChanged += () =>
            ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        App.PendingRequestNotifications.ActionCompleted += () => NotificationActionNotice.IsOpen = false;
        ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        ViewModel.SearchSuggestions.CollectionChanged += SearchSuggestions_CollectionChanged;
        ViewModel.SearchApplied += () => DispatcherQueue.TryEnqueue(RefreshSuggestionPopup);
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _searchSuggestionsAllowed = false;
        InboxSearchBox.IsSuggestionListOpen = false;
        InboxApprovalHelpTip.IsOpen = false;
    }

    private void SearchSuggestions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshSuggestionPopup();

    private void RefreshSuggestionPopup()
    {
        var search = InboxSearchBox;
        search.IsSuggestionListOpen = _searchSuggestionsAllowed && HasSearchFocus() &&
            search.Text.Trim().Length > 0 && ViewModel.SearchSuggestions.Count > 0;
    }

    private bool HasSearchFocus()
    {
        if (XamlRoot is null) return false;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, InboxSearchBox)) return true;
            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_windowClosedSubscribed)
        {
            App.Window.Closed += WindowClosed;
            _windowClosedSubscribed = true;
        }
        if (!_openAppCallbackConfigured)
        {
            App.PendingRequestNotifications.SetOpenAppCallback(() =>
            {
                ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
                NotificationActionNotice.IsOpen = true;
                OpenRequested?.Invoke();
                App.Tray.ShowWindow();
            });
            _openAppCallbackConfigured = true;
        }

        await ViewModel.EnsureConversationsRestoredAsync();
    }

    private Task OpenInboxAsync(InboxItem item)
    {
        if (ViewModel.Requests.Contains(item))
        {
            ViewModel.RevealRequest(item);
        }
        OpenRequested?.Invoke();
        App.Tray.ShowWindow();
        return Task.CompletedTask;
    }

    public static Microsoft.UI.Xaml.Visibility ConversationPaneVisibility(bool wide, bool detailOpen) =>
        wide || !detailOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public static Microsoft.UI.Xaml.Visibility DetailPaneVisibility(bool wide, bool detailOpen) =>
        wide || detailOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public static Microsoft.UI.Xaml.Visibility BackButtonVisibility(bool wide) =>
        wide ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public static Microsoft.UI.Xaml.Visibility VisibleWhen(bool value) => value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public static Microsoft.UI.Xaml.Visibility HiddenWhen(bool value) => value ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private void Page_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        ConnectionActions.Orientation = e.NewSize.Width >= 680
            ? Orientation.Horizontal : Orientation.Vertical;
        ViewModel.IsWideLayout = e.NewSize.Width >= 840;
        if (ViewModel.IsWideLayout)
        {
            BodyGrid.ColumnDefinitions[0].Width = new Microsoft.UI.Xaml.GridLength(2, Microsoft.UI.Xaml.GridUnitType.Star);
            BodyGrid.ColumnDefinitions[1].Width = new Microsoft.UI.Xaml.GridLength(3, Microsoft.UI.Xaml.GridUnitType.Star);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(DetailPane, 1);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(DetailPane, 1);
        }
        else
        {
            BodyGrid.ColumnDefinitions[0].Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star);
            BodyGrid.ColumnDefinitions[1].Width = new Microsoft.UI.Xaml.GridLength(0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(DetailPane, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(DetailPane, 2);
        }
    }

    private void Conversation_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InboxConversation conversation) OpenConversation(conversation);
    }

    private void Conversation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: InboxConversation conversation })
        {
            ViewModel.SelectedConversation = conversation;
        }
    }

    private void Conversation_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && ViewModel.SelectedConversation is { } conversation)
        {
            OpenConversation(conversation);
            e.Handled = true;
        }
    }

    private void InboxSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchSuggestionsAllowed = true;
            _lastUserSearchText = sender.Text;
            ViewModel.SearchText = sender.Text;
        }
        else if (sender.Text != _lastUserSearchText) _searchSuggestionsAllowed = false;
        sender.IsSuggestionListOpen = false;
    }

    private void InboxSearch_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        _searchSuggestionsAllowed = false;
        InboxSearchBox.IsSuggestionListOpen = false;
    }

    private void InboxSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        _searchSuggestionsAllowed = false;
        sender.IsSuggestionListOpen = false;
        if (e.ChosenSuggestion is InboxConversation conversation)
        {
            // A request can change state or be removed while its suggestion is open.
            if (ViewModel.VisibleConversations.Contains(conversation)) OpenConversation(conversation);
            return;
        }

        // Keep arbitrary text as a normal list filter; never choose a result implicitly.
        ViewModel.SearchText = e.QueryText;
        if (!ViewModel.IsWideLayout) ViewModel.IsDetailOpen = false;
    }

    private void ApprovalHelp_Click(object sender, RoutedEventArgs e)
    {
        InboxApprovalHelpTip.CloseButtonContent = DesktopText.Get("Close");
        InboxApprovalHelpTip.IsOpen = true;
    }

    private void ApprovalHelp_Close(TeachingTip sender, object args) => sender.IsOpen = false;

    public static Visibility AvatarVisibility(string? path) => string.IsNullOrEmpty(path) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ConnectCommandsVisibility(bool wide, bool connected) =>
        wide || !connected ? Visibility.Visible : Visibility.Collapsed;

    private void ConversationPersonPicture_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InboxConversation conversation })
            ViewModel.TrackAvatarLoaded(conversation, sender);
    }

    private void ConversationPersonPicture_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            ViewModel.TrackAvatarUnloaded(element);
    }

    private void ConversationPersonPicture_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (!sender.IsLoaded) return;
        if (e.NewValue is InboxConversation conversation)
            ViewModel.TrackAvatarLoaded(conversation, sender);
        else
            ViewModel.TrackAvatarUnloaded(sender);
    }

    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? AvatarImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
        {
            DecodePixelWidth = 64,
            DecodePixelHeight = 64,
            UriSource = uri
        };
    }

    private void OpenConversation(InboxConversation conversation)
    {
        ViewModel.OpenConversation(conversation);
        if (!ViewModel.IsWideLayout) DispatcherQueue.TryEnqueue(() => InboxBackButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
    }

    private void BackToConversations_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.BackToConversations();
        ConversationList.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void NotifyRequest(InboxItem item)
    {
        var notifications = App.PendingRequestNotifications;
        if (!notifications.TryNotifyPendingRequest(Notifications.PendingNotificationKind.ChatRequest,
            () => ViewModel.ApproveNotificationAsync(item),
            () => ViewModel.RejectNotificationAsync(item),
            () => OpenInboxAsync(item), out var token))
        {
            ViewModel.NotificationStatus = notifications.Status;
            return;
        }
        System.ComponentModel.PropertyChangedEventHandler? resolved = null;
        resolved = (_, args) =>
        {
            if (args.PropertyName == nameof(InboxItem.State) && item.State != "承認待ち")
            {
                notifications.Resolve(token);
                item.PropertyChanged -= resolved;
            }
        };
        item.PropertyChanged += resolved;
    }

    private async Task<bool> ConfirmAsync(InboxItem item, CancellationToken cancellationToken)
    {
        var changesWorkspace = ChatAgentBridge.Core.WorkspaceChatCommand.TryParse(item.Prompt, out _);
        var changesAgent = ChatAgentBridge.Core.AgentChatCommand.TryParse(item.Prompt, out _);
        var executionSettings = item.EffectiveSettings ?? item.Settings;
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get(changesAgent ? "AgentSwitchTitle" : changesWorkspace ? "InboxConfirmWorkspaceTitle" : "InboxConfirmRunTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = changesAgent ? item.SessionLabel : DesktopText.Get("InboxRunConfirmBody", item.Source, item.Request.ThreadId, item.SessionLabel, item.Workspace,
                        executionSettings.AgentExecutable, executionSettings.AgentMode == "app-server" ? "app-server" : executionSettings.AgentArguments, item.PromptDisplay),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                MaxHeight = 400
            },
            PrimaryButtonText = DesktopText.Get(changesAgent || changesWorkspace ? "Change" : "Run"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ChatRunConfirmation");
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            try { dialog.Hide(); }
            catch (InvalidOperationException) { }
        }));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmReplyAsync(InboxItem item, string text)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("ConfirmSendResultTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 400,
                Content = new TextBlock
                {
                    Text = DesktopText.Get("ConfirmSendResultBody", item.Source, item.Request.ThreadId, text),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            PrimaryButtonText = DesktopText.Get("Send"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ReplyConfirmation");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<IReadOnlyList<ChatAgentBridge.Core.FileAttachmentCandidate>?> ConfirmAttachmentsAsync(
        InboxItem item,
        IReadOnlyList<ChatAgentBridge.Core.FileAttachmentCandidate> candidates,
        bool isFirstScopeConsent,
        CancellationToken cancellationToken)
    {
        var settings = item.EffectiveSettings ?? item.Settings;
        ChatAgentBridge.Core.FileAttachmentPolicy.Validate(settings);
        var checks = new List<(ChatAgentBridge.Core.FileAttachmentCandidate Candidate, CheckBox Box)>();
        var filesPanel = new StackPanel { Spacing = 4 };
        foreach (var candidate in candidates)
        {
            var fileSizeLabel = DesktopText.Get("AttachmentFileSize", candidate.Length, candidate.Length / (1024d * 1024d));
            var box = new CheckBox
            {
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = candidate.RelativePath, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                        new TextBlock { Text = fileSizeLabel, Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"] }
                    }
                },
                IsThreeState = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, $"AttachmentFile{checks.Count + 1}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, DesktopText.Get("AttachmentFileAccessibleName", candidate.RelativePath, fileSizeLabel));
            checks.Add((candidate, box));
            filesPanel.Children.Add(box);
        }

        var selectionError = new TextBlock
        {
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(selectionError, "AttachmentSelectionStatus");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(selectionError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var selectedDestination = new TextBlock
        {
            Text = DesktopText.Get("AttachmentDestination", item.Source, item.Request.ThreadId, item.Workspace),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var intro = new TextBlock { Text = DesktopText.Get("AttachmentConfirmIntro"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        var scopeWarning = new TextBlock { Text = DesktopText.Get("AttachmentFirstConsentWarning", settings.FileAttachmentMaxCount, settings.FileAttachmentMaxSizeMb, settings.FileAttachmentMaxTotalSizeMb), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(intro);
        body.Children.Add(new TextBlock { Text = DesktopText.Get("AttachmentPrivacyHint"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        body.Children.Add(selectedDestination);
        if (isFirstScopeConsent) body.Children.Add(scopeWarning);
        body.Children.Add(new TextBlock { Text = DesktopText.Get("AttachmentSelectionLimits", settings.FileAttachmentMaxCount, settings.FileAttachmentMaxSizeMb, settings.FileAttachmentMaxTotalSizeMb), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = filesPanel, MaxHeight = 280, VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto });
        body.Children.Add(selectionError);

        ContentDialog? dialog = null;
        void UpdateSelection()
        {
            var selected = checks.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Candidate).ToArray();
            var totalBytes = selected.Sum(candidate => candidate.Length);
            var maximumFileBytes = (long)settings.FileAttachmentMaxSizeMb * 1024 * 1024;
            var maximumTotalBytes = (long)settings.FileAttachmentMaxTotalSizeMb * 1024 * 1024;
            selectionError.Text = selected.Length == 0 ? DesktopText.Get("AttachmentSelectAtLeastOne")
                : selected.Length > settings.FileAttachmentMaxCount ? DesktopText.Get("AttachmentSelectionCountExceeded", settings.FileAttachmentMaxCount)
                : selected.Any(candidate => candidate.Length > maximumFileBytes) ? DesktopText.Get("AttachmentSelectionFileTooLarge", settings.FileAttachmentMaxSizeMb)
                : totalBytes > maximumTotalBytes ? DesktopText.Get("AttachmentSelectionTotalTooLarge", settings.FileAttachmentMaxTotalSizeMb)
                : string.Empty;
            if (dialog is not null) dialog.IsPrimaryButtonEnabled = selected.Length > 0 && selectionError.Text.Length == 0;
        }
        foreach (var (_, box) in checks)
        {
            box.Checked += (_, _) => UpdateSelection();
            box.Unchecked += (_, _) => UpdateSelection();
        }

        dialog = new ContentDialog
        {
            RequestedTheme = App.Appearance.RequestedTheme,
            XamlRoot = XamlRoot,
            Title = DesktopText.Get("AttachmentConfirmTitle"),
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            PrimaryButtonText = DesktopText.Get("AttachmentSendSelected"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "AttachmentConfirmation");
        UpdateSelection();
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellation = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            try { dialog.Hide(); }
            catch (InvalidOperationException) { }
        }));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary
            ? checks.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Candidate).ToArray()
            : null;
    }

    private void WindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => ViewModel.Close();
}
