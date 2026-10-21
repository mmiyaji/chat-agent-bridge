using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private readonly CliProbeService _cliProbe = new();
    private CancellationTokenSource? _cliCheckCancellation;
    private string? _checkedCliSignature;
    private string? _checkedConnectionSignature;
    private DateTimeOffset? _cliCheckedAt;
    private long _cliCheckGeneration;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCliCommand)), NotifyCanExecuteChangedFor(nameof(CancelCliCheckCommand))]
    public partial bool IsCheckingCli { get; set; }
    [ObservableProperty] public partial bool CliCheckSucceeded { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowCliCheckSummary))] public partial bool CliCheckFailed { get; set; }
    public bool ShowCliCheckSummary => !CliCheckFailed;
    [ObservableProperty] public partial string CliVersion { get; set; } = ViewModelText.Get("NotChecked");
    [ObservableProperty] public partial string CliExecutablePath { get; set; } = "";
    [ObservableProperty] public partial string CliCheckSummary { get; set; } = ViewModelText.Get("CliCheckIntro");

    partial void OnIsCheckingCliChanged(bool value)
    {
        StartOnboardingCommand.NotifyCanExecuteChanged();
        PreviousOnboardingCommand.NotifyCanExecuteChanged();
        NextOnboardingCommand.NotifyCanExecuteChanged();
        DeferOnboardingCommand.NotifyCanExecuteChanged();
    }
    private string CurrentCliSignature() => $"{AgentProvider}\n{AgentExecutable}\n{AgentArguments}\n{WorkspaceRoot}\n{UseAppServer}\n{TrustedWorkspaceRoots}";
    private bool HasCurrentCliCheck() => CliCheckSucceeded && _checkedCliSignature == CurrentCliSignature() &&
        (string.IsNullOrWhiteSpace(AgentModel) || ModelCatalog.Models.Any(model => string.Equals(model.Id, AgentModel.Trim(), StringComparison.Ordinal)));

    private string CurrentConnectionSignature() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", AgentProvider, AgentExecutable, AgentArguments,
        UseAppServer, AgentModel?.Trim() ?? "", AgentReasoningEffort ?? "", AgentPermissionMode, AllowAgentNetwork,
        CliVersion, CliExecutablePath))));

    private bool CanRegisterAgentConnection() => HasCurrentCliCheck() &&
        AgentProvider is (AgentProfile.Codex or AgentProfile.Claude) &&
        _checkedConnectionSignature == CurrentConnectionSignature() && !IsLoading && !IsRunning;

    private void WizardSettingChanged(string? property)
    {
        if (property is nameof(AgentProvider) or nameof(AgentExecutable) or nameof(AgentArguments) or nameof(WorkspaceRoot) or nameof(UseAppServer) or nameof(TrustedWorkspaceRoots))
        {
            if (_checkedCliSignature is not null || IsCheckingCli || CliCheckFailed)
            {
                CancelCliCheck();
                CliCheckSucceeded = false;
                IsCliCheckRestored = false;
                _checkedCliSignature = null;
                _checkedConnectionSignature = null;
                _cliCheckedAt = null;
                CliVersion = ViewModelText.Get("NotChecked");
                CliExecutablePath = "";
                CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
            }
        }
        if (property is nameof(AgentProvider) or nameof(AgentModel) or nameof(AgentReasoningEffort) or nameof(WorkspaceRoot) or nameof(ChatApprovalMode) or nameof(ChatResultMode) or nameof(AgentPermissionMode) or nameof(AutoConnectProvider))
            OnPropertyChanged(nameof(WizardSummary));
    }

    /// <summary>Invalidates process-dependent CLI results after refreshing PATH and user environment.</summary>
    public void OnProcessEnvironmentReloaded()
    {
        ClearCliTrustRecovery();
        CliCheckSucceeded = false;
        IsCliCheckRestored = false;
        _ = ClearSavedCliVerificationAsync();
        CliCheckFailed = false;
        _checkedCliSignature = null;
        _checkedConnectionSignature = null;
        _cliCheckedAt = null;
        CliVersion = ViewModelText.Get("NotChecked");
        CliExecutablePath = "";
        CliCheckSummary = ViewModelText.Get("EnvironmentReloadedCliHelp");
        InvalidateModelCatalog();
    }
    [RelayCommand(CanExecute = nameof(CanCheckCli))]
    private async Task CheckCliAsync()
    {
        if (!CanCheckCli()) return;
        var cancellation = new CancellationTokenSource();
        _cliCheckCancellation = cancellation;
        var generation = ++_cliCheckGeneration;
        var signature = CurrentCliSignature();
        ClearCliTrustRecovery();
        CliCheckSucceeded = false;
        IsCliCheckRestored = false;
        CliCheckFailed = false;
        _checkedCliSignature = null;
        _checkedConnectionSignature = null;
        _cliCheckedAt = null;
        CliVersion = ViewModelText.Get("Checking");
        CliExecutablePath = "";
        CliCheckSummary = ViewModelText.Get("CheckingCliVersion");
        ModelCatalog.Invalidate(ViewModelText.Get("FetchingModelsWithCliCheck"));
        IsCheckingCli = true;
        try
        {
            var settings = CreateSettingsSnapshot();
            // A failed or cancelled explicit recheck must not resurrect an old
            // success on the next launch.
            await ClearSavedCliVerificationAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await _cliProbe.CheckAsync(settings, cancellation.Token);
            if (generation != _cliCheckGeneration || signature != CurrentCliSignature()) return;
            CliVersion = result.Version;
            CliExecutablePath = result.Executable;
            if (!IsCustomProvider && !result.ProviderRecognized) { CliCheckFailed = true; CliCheckSummary = ViewModelText.Get("UnrecognizedCliProvider"); return; }
            if (IsBasicProvider)
            {
                ModelCatalog.Invalidate(ViewModelText.Get("BasicModelManagedByCli"));
                _checkedCliSignature = signature;
                _checkedConnectionSignature = CurrentConnectionSignature();
                _cliCheckedAt = DateTimeOffset.UtcNow;
                CliCheckSucceeded = true;
                CliCheckSummary = ViewModelText.Get("BasicCliCheckSucceeded");
                await SaveCliVerificationAsync(settings, result, generation, cancellation.Token);
                return;
            }
            CliCheckSummary = ViewModelText.Get("CliStartedFetchingModels");
            await ModelCatalog.FetchModelsCommand.ExecuteAsync(null);
            if (generation != _cliCheckGeneration || cancellation.IsCancellationRequested || signature != CurrentCliSignature()) return;
            if (ModelCatalog.Models.Count == 0)
            {
                CliCheckFailed = true;
                CliCheckSummary = ViewModelText.Get("CliModelsUnavailableDetails", ModelCatalog.Status);
                return;
            }
            ModelCatalog.SelectedModel = ModelCatalog.Models.FirstOrDefault(model => string.Equals(model.Id, AgentModel?.Trim(), StringComparison.Ordinal));
            _checkedCliSignature = signature;
            _checkedConnectionSignature = CurrentConnectionSignature();
            _cliCheckedAt = DateTimeOffset.UtcNow;
            CliCheckSucceeded = true;
            CliCheckSummary = ViewModelText.Get("CliCheckSucceeded", ModelCatalog.Models.Count);
            await SaveCliVerificationAsync(settings, result, generation, cancellation.Token);
        }
        catch (OperationCanceledException) { if (generation == _cliCheckGeneration) CliCheckSummary = ViewModelText.Get("CliCheckCancelled"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (generation == _cliCheckGeneration)
            {
                CliVersion = ViewModelText.Get("CliUnconfirmed");
                CliCheckFailed = true;
                CliCheckSummary = ex is TimeoutException ? ViewModelText.Get("CliTimeout") : ViewModelText.Get("CliCheckFailedDetails", ex.Message);
                if (ex is CliWorkspaceTrustException trust && signature == CurrentCliSignature())
                    SetCliTrustRecovery(trust, signature);
            }
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
        ClearCliTrustRecovery();
        _cliCheckGeneration++;
        _cliCheckCancellation?.Cancel();
        _cliCheckCancellation = null;
        ModelCatalog.CancelLookup();
        IsCheckingCli = false;
        CliCheckSucceeded = false;
        IsCliCheckRestored = false;
        CliCheckFailed = false;
        _checkedCliSignature = null;
        _checkedConnectionSignature = null;
        _cliCheckedAt = null;
        CliCheckSummary = ViewModelText.Get("CliCheckCancelled");
    }
}
