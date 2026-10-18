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
        ViewModel.ConnectionEnded += () => App.PendingRequestNotifications.ClearAll();
        App.PendingRequestNotifications.StatusChanged += () =>
            ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        ViewModel.NotificationStatus = App.PendingRequestNotifications.Status;
        Loaded += (_, _) => App.Window.Closed += WindowClosed;
    }

    private Task OpenInboxAsync(InboxItem item)
    {
        if (ViewModel.Requests.Contains(item)) ViewModel.Selected = item;
        OpenRequested?.Invoke();
        App.Window.Activate();
        if (App.Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
        return Task.CompletedTask;
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

    private async Task<bool> ConfirmAsync(InboxItem item)
    {
        var changesWorkspace = ChatAgentBridge.Core.WorkspaceChatCommand.TryParse(item.Prompt, out _);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = DesktopText.Get(changesWorkspace ? "InboxConfirmWorkspaceTitle" : "InboxConfirmRunTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = DesktopText.Get("InboxRunConfirmBody", item.Source, item.Request.ThreadId, item.SessionLabel, item.Workspace,
                        item.Settings.AgentExecutable, item.Settings.AgentMode == "app-server" ? "app-server" : item.Settings.AgentArguments, item.Prompt),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                MaxHeight = 400
            },
            PrimaryButtonText = DesktopText.Get(changesWorkspace ? "Change" : "Run"),
            CloseButtonText = DesktopText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ChatRunConfirmation");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmReplyAsync(InboxItem item, string text)
    {
        var dialog = new ContentDialog
        {
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
