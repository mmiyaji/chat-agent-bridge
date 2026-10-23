using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CliVerificationStore _cliVerificationStore = new();
    [ObservableProperty] public partial bool IsCliCheckRestored { get; set; }

    private async Task RestoreCliVerificationAsync()
    {
        var generation = _cliCheckGeneration;
        var signature = CurrentCliSignature();
        var cached = await _cliVerificationStore.LoadAsync(CreateSettingsSnapshot());
        if (_closing || IsCheckingCli || generation != _cliCheckGeneration || signature != CurrentCliSignature()) return;
        if (cached is null)
        {
            if (CliCheckSucceeded)
            {
                CancelCliCheck();
                ModelCatalog.Invalidate();
                CliVersion = ViewModelText.Get("NotChecked");
                CliExecutablePath = "";
                CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
            }
            return;
        }

        if (SupportsReasoning) ModelCatalog.RestoreVerifiedModels(cached.Models, AgentModel);
        else InvalidateModelCatalog();
        PopulateGlobalEfforts();
        CliVersion = cached.Probe.Version;
        CliExecutablePath = cached.Probe.Executable;
        _checkedCliSignature = signature;
        _checkedConnectionSignature = cached.ConnectionSignature;
        _cliCheckedAt = cached.CheckedAtUtc;
        IsCliCheckRestored = true;
        CliCheckFailed = false;
        CliCheckSummary = ViewModelText.Get("CliVerificationRestored", cached.CheckedAtUtc.ToLocalTime().ToString("g"));
        CliCheckSucceeded = true;
    }

    private async Task SaveCliVerificationAsync(BridgeSettings settings, CliProbeResult probe, long generation, CancellationToken cancellationToken)
    {
        if (_cliCheckedAt is not { } checkedAt) return;
        try
        {
            await _cliVerificationStore.SaveAsync(settings,
                new CachedCliVerification(probe, ModelCatalog.Models.ToArray(), checkedAt, _checkedConnectionSignature ?? ""), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == _cliCheckGeneration)
                CliCheckSummary += " " + ViewModelText.Get("CliVerificationSaveFailed");
        }
    }

    private async Task ClearSavedCliVerificationAsync()
    {
        try { await _cliVerificationStore.ClearAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Verification is optional diagnostic state. A cache failure must
            // not prevent an explicit check or change execution permissions.
        }
    }
}
