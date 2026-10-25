using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CliVerificationStore ProviderCliVerificationStore(BridgeSettings settings) =>
        CliVerificationStore.ForProvider(_settingsStore.DirectoryPath, AgentProfile.ResolveProvider(settings));

    private async Task<CachedCliVerification?> LoadCliVerificationAsync(BridgeSettings settings) =>
        await ProviderCliVerificationStore(settings).LoadAsync(settings) ??
        await new CliVerificationStore(_settingsStore.DirectoryPath).LoadAsync(settings);
    [ObservableProperty] public partial bool IsCliCheckRestored { get; set; }

    private async Task RestoreCliVerificationAsync()
    {
        var generation = _cliCheckGeneration;
        var signature = CurrentCliSignature();
        var cached = await LoadCliVerificationAsync(CreateSettingsSnapshot());
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

        ApplyCliVerification(cached, signature, restored: true);
    }

    private void ApplyCliVerification(CachedCliVerification cached, string signature, bool restored)
    {
        if (SupportsModelCatalog) ModelCatalog.RestoreVerifiedModels(cached.Models, AgentModel);
        else InvalidateModelCatalog();
        PopulateGlobalEfforts();
        CliVersion = cached.Probe.Version;
        CliExecutablePath = cached.Probe.Executable;
        _checkedCliSignature = signature;
        _checkedConnectionSignature = cached.ConnectionSignature;
        _cliCheckedAt = cached.CheckedAtUtc;
        IsCliCheckRestored = restored;
        CliCheckFailed = false;
        CliCheckSummary = restored
            ? ViewModelText.Get("CliVerificationRestored", cached.CheckedAtUtc.ToLocalTime().ToString("g"))
            : ViewModelText.Get(IsBasicProvider || IsCustomProvider ? "BasicCliCheckSucceeded" : "CliCheckSucceeded", cached.Models.Count);
        CliCheckSucceeded = true;
    }

    private async Task SaveCliVerificationAsync(BridgeSettings settings, CliProbeResult probe, long generation, CancellationToken cancellationToken)
    {
        if (_cliCheckedAt is not { } checkedAt) return;
        try
        {
            await ProviderCliVerificationStore(settings).SaveAsync(settings,
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
        try
        {
            var settings = CreateSettingsSnapshot();
            await ProviderCliVerificationStore(settings).ClearAsync();
            // Remove the compatibility copy too, so an explicit failed recheck
            // cannot restore an old success. An inactive editor must preserve
            // the legacy cache for a different active provider.
            var legacy = new CliVerificationStore(_settingsStore.DirectoryPath);
            var active = _agentEditorSharedSettings ?? _settings ?? settings;
            if (AgentProfile.ResolveProvider(active) == AgentProfile.ResolveProvider(settings) ||
                await legacy.LoadAsync(settings) is not null)
                await legacy.ClearAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Verification is optional diagnostic state. A cache failure must
            // not prevent an explicit check or change execution permissions.
        }
    }
}
