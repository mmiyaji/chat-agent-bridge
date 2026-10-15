using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxPage : Page
{
    public ChatInboxViewModel ViewModel { get; } = new();

    public ChatInboxPage()
    {
        InitializeComponent();
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.ConfirmReplyAsync = ConfirmReplyAsync;
        Loaded += (_, _) => App.Window.Closed += WindowClosed;
    }

    private async Task<bool> ConfirmAsync(InboxItem item)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ローカルエージェントで実行しますか？",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = $"{item.Source}\nスレッド: {item.Request.ThreadId}\n{item.SessionLabel}\n作業フォルダー: {item.Workspace}\nエージェント: {item.Settings.AgentExecutable} {item.Settings.AgentArguments}\n\n{item.Prompt}",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                MaxHeight = 400
            },
            PrimaryButtonText = "実行",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmReplyAsync(InboxItem item, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "結果本文をチャットに送信しますか？",
            Content = new ScrollViewer
            {
                MaxHeight = 400,
                Content = new TextBlock
                {
                    Text = $"送信先: {item.Source}\nスレッド: {item.Request.ThreadId}\n\n{text}",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            PrimaryButtonText = "送信",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ReplyConfirmation");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void WindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => ViewModel.Close();
}
