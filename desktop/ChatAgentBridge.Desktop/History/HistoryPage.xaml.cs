using ChatAgentBridge_Desktop.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
            XamlRoot = XamlRoot,
            Title = "このセッションを切り離しますか？",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"提供元: {session.Provider}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"チャンネル ID: {session.ChannelId}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"スレッド ID: {session.ThreadId}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"セッション ID: {session.SessionId}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "この会話と保存済みセッションの関連付けを削除します。実行履歴は残ります。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }
                }
            },
            PrimaryButtonText = "切り離す",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "DetachSessionConfirmation");
        AutomationProperties.SetName(dialog, "セッション切り離しの確認");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> EditSessionModelAsync(SessionHistoryRow session)
    {
        var model = new TextBox
        {
            Text = session.ModelOverride ?? string.Empty,
            MaxLength = 256,
            PlaceholderText = "利用できるモデル名を入力"
        };
        AutomationProperties.SetAutomationId(model, "SessionModelOverride");
        AutomationProperties.SetName(model, "次回の実行モデル");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "次回の実行モデル",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"会話: {session.Conversation}\nこの会話で、次の実行から使うモデル名を入力してください。変更するまで継続して使います。空欄にするとエージェント設定の既定モデルを使います。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    model
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "SessionModelDialog");
        AutomationProperties.SetName(dialog, "次回の実行モデルを設定");
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? model.Text.Trim() : null;
    }
}
