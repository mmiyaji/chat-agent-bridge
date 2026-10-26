using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using ChatAgentBridge_Desktop.ModelSelection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private CliVerificationStore ProviderCliVerificationStore(BridgeSettings settings) =>
        CliVerificationStore.ForProvider(_settingsStore.DirectoryPath, AgentProfile.ResolveProvider(settings));

    private Task<CachedCliVerification?> LoadCliVerificationAsync(BridgeSettings settings) =>
        Task.Run(async () =>
            await ProviderCliVerificationStore(settings).LoadAsync(settings).ConfigureAwait(false) ??
            await new CliVerificationStore(_settingsStore.DirectoryPath).LoadAsync(settings).ConfigureAwait(false));
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
        if (SupportsModelCatalog) ModelCatalog.RestoreVerifiedModels(cached.Models, AgentModel, cached.ModelsFetchedAtUtc);
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
                new CachedCliVerification(probe, ModelCatalog.Models.ToArray(), checkedAt, _checkedConnectionSignature ?? "", ModelCatalog.FetchedAtUtc), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == _cliCheckGeneration)
                CliCheckSummary += " " + ViewModelText.Get("CliVerificationSaveFailed");
        }
    }

    private async Task SaveFetchedModelsAsync(BridgeSettings settings, IReadOnlyList<ModelOption> models,
        DateTimeOffset fetchedAtUtc, CancellationToken cancellationToken)
    {
        // A model-only lookup may refresh a diagnostic cache but cannot manufacture a CLI check.
        if (_closing || IsCheckingCli || !HasCurrentCliCheck() || _cliCheckedAt is not { } checkedAt ||
            _checkedConnectionSignature is not { } connectionSignature) return;
        var generation = _cliCheckGeneration;
        var signature = CurrentCliSignature();
        try
        {
            if (!CliVerificationStore.HasSameContext(settings, CreateSettingsSnapshot())) return;
            var store = ProviderCliVerificationStore(settings);
            if (await store.UpdateModelsAsync(settings, checkedAt, connectionSignature, models, fetchedAtUtc, cancellationToken)) return;
            if (_closing || IsCheckingCli || generation != _cliCheckGeneration || signature != CurrentCliSignature()) return;
            // Legacy diagnostic caches can be refreshed in place without inventing a new verification.
            await new CliVerificationStore(_settingsStore.DirectoryPath)
                .UpdateModelsAsync(settings, checkedAt, connectionSignature, models, fetchedAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Keep the retrieved candidates and distinguish a cache-write failure from discovery failure.
            throw new ModelCatalogPersistenceException(ex);
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
