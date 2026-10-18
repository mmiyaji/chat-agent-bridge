using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly CliProbeService _cliProbe = new();
    private CancellationTokenSource? _cliCheckCancellation;
    private string? _checkedCliSignature;
    private long _cliCheckGeneration;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCliCommand)), NotifyCanExecuteChangedFor(nameof(CancelCliCheckCommand))]
    public partial bool IsCheckingCli { get; set; }
    [ObservableProperty] public partial bool CliCheckSucceeded { get; set; }
    [ObservableProperty] public partial string CliVersion { get; set; } = "未確認";
    [ObservableProperty] public partial string CliExecutablePath { get; set; } = "";
    [ObservableProperty] public partial string CliCheckSummary { get; set; } = "CLIをチェックすると、起動・バージョン・モデル候補を確認できます。";

    partial void OnIsCheckingCliChanged(bool value)
    {
        StartOnboardingCommand.NotifyCanExecuteChanged();
        PreviousOnboardingCommand.NotifyCanExecuteChanged();
        NextOnboardingCommand.NotifyCanExecuteChanged();
        DeferOnboardingCommand.NotifyCanExecuteChanged();
    }
    private string CurrentCliSignature() => $"{AgentProvider}\n{AgentExecutable}\n{WorkspaceRoot}\n{UseAppServer}\n{TrustedWorkspaceRoots}";
    private bool HasCurrentCliCheck() => CliCheckSucceeded && _checkedCliSignature == CurrentCliSignature() &&
        (string.IsNullOrWhiteSpace(AgentModel) || ModelCatalog.Models.Any(model => string.Equals(model.Id, AgentModel.Trim(), StringComparison.Ordinal)));

    private void WizardSettingChanged(string? property)
    {
        if (property is nameof(AgentProvider) or nameof(AgentExecutable) or nameof(WorkspaceRoot) or nameof(UseAppServer) or nameof(TrustedWorkspaceRoots))
        {
            if (_checkedCliSignature is not null || IsCheckingCli)
            {
                CancelCliCheck();
                CliCheckSucceeded = false;
                _checkedCliSignature = null;
                CliVersion = "未確認";
                CliExecutablePath = "";
                CliCheckSummary = "CLIまたは作業フォルダーの設定が変わりました。もう一度チェックしてください。";
            }
        }
        if (property is nameof(AgentProvider) or nameof(AgentModel) or nameof(AgentReasoningEffort) or nameof(WorkspaceRoot) or nameof(ChatApprovalMode) or nameof(ChatResultMode) or nameof(AgentPermissionMode) or nameof(AutoConnectProvider))
            OnPropertyChanged(nameof(WizardSummary));
    }
    [RelayCommand(CanExecute = nameof(CanCheckCli))]
    private async Task CheckCliAsync()
    {
        if (!CanCheckCli()) return;
        var cancellation = new CancellationTokenSource();
        _cliCheckCancellation = cancellation;
        var generation = ++_cliCheckGeneration;
        var signature = CurrentCliSignature();
        CliCheckSucceeded = false;
        _checkedCliSignature = null;
        CliVersion = "確認中…";
        CliExecutablePath = "";
        CliCheckSummary = "CLIの起動とバージョンを確認しています…";
        ModelCatalog.Invalidate("CLIのチェックと合わせてモデル候補を取得します。");
        IsCheckingCli = true;
        try
        {
            var settings = CreateSettingsSnapshot();
            var result = await _cliProbe.CheckAsync(settings, cancellation.Token);
            if (generation != _cliCheckGeneration || signature != CurrentCliSignature()) return;
            CliVersion = result.Version;
            CliExecutablePath = result.Executable;
            if (!result.ProviderRecognized) { CliCheckSummary = "バージョン応答はありましたが、このCLIのモデル候補を確認できません。CodexまたはClaude Codeを選んでください。"; return; }
            CliCheckSummary = "CLIを起動できました。続いてモデル候補を確認しています…";
            await ModelCatalog.FetchModelsCommand.ExecuteAsync(null);
            if (generation != _cliCheckGeneration || cancellation.IsCancellationRequested || signature != CurrentCliSignature()) return;
            if (ModelCatalog.Models.Count == 0) { CliCheckSummary = "バージョンは確認できましたが、モデル候補を取得できませんでした。CLIへのログインとネットワークを確認し、再チェックしてください。"; return; }
            ModelCatalog.SelectedModel = ModelCatalog.Models.FirstOrDefault(model => string.Equals(model.Id, AgentModel?.Trim(), StringComparison.Ordinal));
            CliCheckSucceeded = true;
            _checkedCliSignature = signature;
            CliCheckSummary = $"CLIの起動・種類・バージョンと、{ModelCatalog.Models.Count}件のモデル候補を確認しました。AIの作業実行や利用権限の確認は行っていません。";
        }
        catch (OperationCanceledException) { if (generation == _cliCheckGeneration) CliCheckSummary = "CLIのチェックをキャンセルしました。再実行できます。"; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (generation == _cliCheckGeneration) { CliVersion = "確認できませんでした"; CliCheckSummary = ex is TimeoutException ? "CLIの応答が時間内にありませんでした。CLIの場所とログイン状態を確認し、再実行してください。" : "CLIを確認できませんでした。選択したAIと実行ファイルが合っているか、CLIがインストール済みか確認してください。"; }
        }
        finally
        {
            if (generation == _cliCheckGeneration) { IsCheckingCli = false; _cliCheckCancellation = null; }
            cancellation.Dispose();
        }
    }
    private bool CanCheckCli() => !_closing && IsSettingsLoaded && !IsLoading && !IsRunning && !IsCheckingCli && !IsOnboardingBusy;
    [RelayCommand(CanExecute = nameof(IsCheckingCli))]
    private void CancelCliCheck()
    {
        _cliCheckGeneration++;
        _cliCheckCancellation?.Cancel();
        _cliCheckCancellation = null;
        ModelCatalog.CancelLookup();
        IsCheckingCli = false;
        CliCheckSucceeded = false;
        _checkedCliSignature = null;
        CliCheckSummary = "CLIのチェックをキャンセルしました。再実行できます。";
    }
}
