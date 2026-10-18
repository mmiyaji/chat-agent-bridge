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
    private long _cliCheckGeneration;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCliCommand)), NotifyCanExecuteChangedFor(nameof(CancelCliCheckCommand))]
    public partial bool IsCheckingCli { get; set; }
    [ObservableProperty] public partial bool CliCheckSucceeded { get; set; }
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

    private void WizardSettingChanged(string? property)
    {
        if (property is nameof(AgentProvider) or nameof(AgentExecutable) or nameof(AgentArguments) or nameof(WorkspaceRoot) or nameof(UseAppServer) or nameof(TrustedWorkspaceRoots))
        {
            if (_checkedCliSignature is not null || IsCheckingCli)
            {
                CancelCliCheck();
                CliCheckSucceeded = false;
                _checkedCliSignature = null;
                CliVersion = ViewModelText.Get("NotChecked");
                CliExecutablePath = "";
                CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
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
        CliVersion = ViewModelText.Get("Checking");
        CliExecutablePath = "";
        CliCheckSummary = ViewModelText.Get("CheckingCliVersion");
        ModelCatalog.Invalidate(ViewModelText.Get("FetchingModelsWithCliCheck"));
        IsCheckingCli = true;
        try
        {
            var settings = CreateSettingsSnapshot();
            var result = await _cliProbe.CheckAsync(settings, cancellation.Token);
            if (generation != _cliCheckGeneration || signature != CurrentCliSignature()) return;
            CliVersion = result.Version;
            CliExecutablePath = result.Executable;
            if (!IsCustomProvider && !result.ProviderRecognized) { CliCheckSummary = ViewModelText.Get("UnrecognizedCliProvider"); return; }
            if (IsBasicProvider)
            {
                ModelCatalog.Invalidate(ViewModelText.Get("BasicModelManagedByCli"));
                CliCheckSucceeded = true;
                _checkedCliSignature = signature;
                CliCheckSummary = ViewModelText.Get("BasicCliCheckSucceeded");
                return;
            }
            CliCheckSummary = ViewModelText.Get("CliStartedFetchingModels");
            await ModelCatalog.FetchModelsCommand.ExecuteAsync(null);
            if (generation != _cliCheckGeneration || cancellation.IsCancellationRequested || signature != CurrentCliSignature()) return;
            if (ModelCatalog.Models.Count == 0) { CliCheckSummary = ViewModelText.Get("CliModelsUnavailable"); return; }
            ModelCatalog.SelectedModel = ModelCatalog.Models.FirstOrDefault(model => string.Equals(model.Id, AgentModel?.Trim(), StringComparison.Ordinal));
            CliCheckSucceeded = true;
            _checkedCliSignature = signature;
            CliCheckSummary = ViewModelText.Get("CliCheckSucceeded", ModelCatalog.Models.Count);
        }
        catch (OperationCanceledException) { if (generation == _cliCheckGeneration) CliCheckSummary = ViewModelText.Get("CliCheckCancelled"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (generation == _cliCheckGeneration)
            {
                CliVersion = ViewModelText.Get("CliUnconfirmed");
                CliCheckSummary = ex is TimeoutException ? ViewModelText.Get("CliTimeout") : ViewModelText.Get("CliCheckFailedDetails", ex.Message);
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
        _cliCheckGeneration++;
        _cliCheckCancellation?.Cancel();
        _cliCheckCancellation = null;
        ModelCatalog.CancelLookup();
        IsCheckingCli = false;
        CliCheckSucceeded = false;
        _checkedCliSignature = null;
        CliCheckSummary = ViewModelText.Get("CliCheckCancelled");
    }
}
