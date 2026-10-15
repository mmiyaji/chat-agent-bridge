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

    private async Task<ModelExecutionSelection?> EditSessionModelAsync(SessionHistoryRow session, BridgeSettings globalSettings)
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
            Header = "推論の強さ（CLIの候補）",
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
        AutomationProperties.SetName(effortCandidates, "セッションの推論レベル候補");
        effortCandidates.SelectionChanged += (_, _) =>
        {
            if (!updatingEffortSelection && effortCandidates.SelectedItem is ChatAgentBridge_Desktop.ViewModels.ReasoningEffortChoice chosen)
            {
                selectedEffort = chosen;
                currentEffort = chosen.Id;
                unsupportedEffort = false;
                dirtyEffortSelection = true;
                effortStatus.Text = chosen.Id is null ? "エージェント設定に従います。別モデルを指定した場合はCLIの設定に任せます。" : $"選択した推論レベル: {chosen.SelectionLabel}";
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
                    ? "CLIの既定値を使用します。モデル候補を取得すると対応状況を確認できます。"
                    : $"保存済みの推論レベル: {currentEffort}（モデル候補を取得するまで対応状況は未確認です）";
                return;
            }
            unsupportedEffort = currentEffort is not null && matching is null;
            if (matching is not null) selectedEffort = matching;
            effortStatus.Text = unsupportedEffort
                ? $"保存済みの推論レベル「{currentEffort}」はこのモデルの候補にありません。「エージェント設定に従う」か、対応候補を選択してください。"
                : currentEffort is null ? "エージェント設定に従います。別モデルを指定した場合はCLIの設定に任せます。" : $"保存済みの推論レベル: {matching!.SelectionLabel}";
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
                            Text = $"会話: {session.Conversation}\nこの会話で次回から使うモデルと推論レベルを指定できます。モデル欄が空ならエージェント設定のモデルを使います。推論レベルが空なら、モデルがエージェント設定と同じ場合は推論レベルも継承し、異なる場合はCLIの既定値を使います。",
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
                        new TextBlock { Text = "推論レベルの候補は選択モデルについてCLIが報告した値です。「エージェント設定に従う」では、同じモデルの既定設定を引き継ぎます。", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "候補はCLIの報告値です。契約上の利用可否は実行時に確認されます。", TextWrapping = TextWrapping.Wrap }
                    }
                }
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.IsPrimaryButtonEnabled = !unsupportedEffort;
        AutomationProperties.SetAutomationId(dialog, "SessionModelDialog");
        AutomationProperties.SetName(dialog, "次回の実行モデルを設定");
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
}
