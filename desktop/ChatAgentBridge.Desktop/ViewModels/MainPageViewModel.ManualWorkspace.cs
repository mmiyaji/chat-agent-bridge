using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasManualWorkspaceError)), NotifyPropertyChangedFor(nameof(HasManualTrustRecovery))]
    public partial string ManualWorkspaceError { get; set; } = "";

    public bool HasManualWorkspaceError => !string.IsNullOrEmpty(ManualWorkspaceError);
    public bool HasManualTrustRecovery => HasManualWorkspaceError && !HasUnsavedSettings && GetCliTrustRecovery() is not null;

    private (BridgeSettings Settings, string Signature)? _pendingManualWorkspaceValidation;
    private Task? _manualWorkspaceValidationDrainTask;

    private void QueueManualWorkspaceValidation()
    {
        if (_closing || _isAgentProfileEditor || _settings is null) return;
        _pendingManualWorkspaceValidation = (_settings, CurrentCliSignature());
        if (_manualWorkspaceValidationDrainTask is null)
            _manualWorkspaceValidationDrainTask = DrainManualWorkspaceValidationsAsync();
    }

    private async Task DrainManualWorkspaceValidationsAsync()
    {
        try
        {
            // Let the current synchronous settings update finish before probing its snapshot.
            await Task.Yield();
            while (!_closing && _pendingManualWorkspaceValidation is { } pending)
            {
                _pendingManualWorkspaceValidation = null;
                try
                {
                    await ValidateManualWorkspaceAsync(pending.Settings, pending.Signature);
                }
                catch
                {
                    // Proactive status refresh must not surface an unobserved background exception.
                }
            }
        }
        catch
        {
            // Keep this coalesced refresh isolated from the UI event that queued it.
        }
        finally
        {
            _manualWorkspaceValidationDrainTask = null;
            if (!_closing && _pendingManualWorkspaceValidation is not null)
                _manualWorkspaceValidationDrainTask = DrainManualWorkspaceValidationsAsync();
        }
    }

    private async Task<bool> ValidateManualWorkspaceAsync(BridgeSettings settings, string signature)
    {
        Exception? validationError = null;
        try
        {
            await Task.Run(() => CliProbeService.ValidateWorkspace(settings));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            validationError = error;
        }

        // A probe may finish after the user changed the CLI/workspace context or the app began closing.
        if (_closing || !string.Equals(signature, CurrentCliSignature(), StringComparison.Ordinal)) return false;
        if (validationError is null)
        {
            ManualWorkspaceError = "";
            return true;
        }
        if (validationError is CliWorkspaceTrustException trust)
            SetCliTrustRecovery(trust, signature);
        else
            ClearCliTrustRecovery();
        ManualWorkspaceError = validationError.Message;
        return false;
    }
}
