using Microsoft.UI.Xaml.Controls;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxPage : Page
{
    public ChatInboxViewModel ViewModel { get; } = new();

    public ChatInboxPage()
    {
        InitializeComponent();
        ViewModel.ConfirmAsync = ConfirmAsync;
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
                    Text = $"{item.Source}\n作業フォルダー: {item.Workspace}\nエージェント: {item.Settings.AgentExecutable} {item.Settings.AgentArguments}\n\n{item.Prompt}",
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

    private void WindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => ViewModel.Close();
}
