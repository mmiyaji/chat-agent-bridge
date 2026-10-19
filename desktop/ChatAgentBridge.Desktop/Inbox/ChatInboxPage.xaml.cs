using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxPage : Page
{
    public ChatInboxViewModel ViewModel { get; } = new();
    public event Action? OpenRequested;

    public ChatInboxPage()
    {
        InitializeComponent();
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.ConfirmReplyAsync = ConfirmReplyAsync;
        var serverDialogs = new AppServerUi.AppServerDialogs(() => XamlRoot);
        ViewModel.ServerApprovalHandler = serverDialogs.ApproveAsync;
        ViewModel.ServerInputHandler = serverDialogs.InputAsync;
        ViewModel.RequestReceived += NotifyRequest;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.Selected))
                DispatcherQueue.TryEnqueue(() => InboxDetailScroll.ChangeView(null, 0, null, true));
        };
        ViewModel.ConnectionEnded += () => App.PendingRequestNotifications.ClearAll();
        App.PendingRequestNotifications.StatusChanged += () =>
            ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        Loaded += (_, _) => App.Window.Closed += WindowClosed;
    }

    private Task OpenInboxAsync(InboxItem item)
    {
        if (ViewModel.Requests.Contains(item))
        {
            ViewModel.RevealRequest(item);
        }
        OpenRequested?.Invoke();
        App.Window.Activate();
        if (App.Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
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
        if (sender is ListView { SelectedItem: InboxConversation conversation }) ViewModel.SelectedConversation = conversation;
    }

    private void Conversation_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && ViewModel.SelectedConversation is { } conversation)
        {
            OpenConversation(conversation);
            e.Handled = true;
        }
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
                        executionSettings.AgentExecutable, executionSettings.AgentMode == "app-server" ? "app-server" : executionSettings.AgentArguments, item.Prompt),
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

    private void WindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => ViewModel.Close();
}
