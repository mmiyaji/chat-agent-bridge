using ChatAgentBridge_Desktop.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.ModelSelection;
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
        var binding = session.Binding;
        var isAppServer = string.Equals(binding.AgentArguments, "app-server", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(binding.AgentArguments, "claude-stream-json-v1", StringComparison.Ordinal);
        var isCodex = isAppServer || string.Equals(binding.AgentArguments, "exec --json -", StringComparison.Ordinal);
        var provider = isClaude ? AgentProfile.Claude : isCodex ? AgentProfile.Codex : "custom";
        var catalog = new ModelCatalogViewModel(() => new BridgeSettings
        {
            WorkspaceRoot = binding.WorkspaceRoot,
            AgentExecutable = binding.AgentExecutable,
            AgentArguments = isAppServer ? "app-server" : isClaude ? "" : binding.AgentArguments,
            AgentMode = isAppServer ? "app-server" : "exec",
            AgentProvider = provider,
            AgentModel = model.Text
        }, option => model.Text = option.Id);
        if (provider == "custom") catalog.Invalidate("このセッションのCLI形式では候補を取得できません。モデル名を直接入力できます。");
        var fetch = new Button { Content = "CLIモデル候補を取得", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = provider != "custom" };
        AutomationProperties.SetAutomationId(fetch, "SessionFetchModels");
        fetch.Click += (_, _) => catalog.FetchModelsCommand.Execute(null);
        var cancel = new Button { Content = "取得をキャンセル", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(cancel, "SessionCancelFetchModels");
        cancel.Click += (_, _) => catalog.CancelFetchCommand.Execute(null);
        var candidates = new ComboBox
        {
            Header = "CLIが提示したモデル候補",
            ItemsSource = catalog.Models,
            DisplayMemberPath = "SelectionLabel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = provider != "custom"
        };
        AutomationProperties.SetAutomationId(candidates, "SessionModelCandidates");
        AutomationProperties.SetName(candidates, "セッションのモデル候補");
        candidates.SelectionChanged += (_, _) =>
        {
            if (candidates.SelectedItem is ModelOption selected) model.Text = selected.Id;
        };
        var catalogStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = catalog.Status };
        AutomationProperties.SetAutomationId(catalogStatus, "SessionModelCatalogStatus");
        var progress = new ProgressRing { IsActive = catalog.IsLoading, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        catalog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(catalog.Status)) catalogStatus.Text = catalog.Status;
            if (args.PropertyName == nameof(catalog.IsLoading))
            {
                fetch.IsEnabled = !catalog.IsLoading && provider != "custom";
                cancel.IsEnabled = catalog.IsLoading;
                progress.IsActive = catalog.IsLoading;
            }
        };
        cancel.IsEnabled = false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "次回の実行モデル",
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
                            Text = $"会話: {session.Conversation}\nこの会話で、次の実行から使うモデル名を入力してください。変更するまで継続して使います。空欄にするとエージェント設定の既定モデルを使います。",
                            TextWrapping = TextWrapping.Wrap
                        },
                        model,
                        fetch,
                        cancel,
                        progress,
                        candidates,
                        catalogStatus,
                        new TextBlock { Text = "候補はCLIの報告値です。契約上の利用可否は実行時に確認されます。", TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        AutomationProperties.SetAutomationId(dialog, "SessionModelDialog");
        AutomationProperties.SetName(dialog, "次回の実行モデルを設定");
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? model.Text.Trim() : null;
        }
        finally
        {
            catalog.Dispose();
        }
    }
}
