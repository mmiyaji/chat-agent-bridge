using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasManualWorkspaceError)), NotifyPropertyChangedFor(nameof(HasManualTrustRecovery))]
    public partial string ManualWorkspaceError { get; set; } = "";

    public bool HasManualWorkspaceError => !string.IsNullOrEmpty(ManualWorkspaceError);
    public bool HasManualTrustRecovery => HasManualWorkspaceError && !HasUnsavedSettings && GetCliTrustRecovery() is not null;

    private bool ValidateManualWorkspace(BridgeSettings settings)
    {
        try
        {
            CliProbeService.ValidateWorkspace(settings);
            ManualWorkspaceError = "";
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (error is CliWorkspaceTrustException trust)
                SetCliTrustRecovery(trust, CurrentCliSignature());
            else
                ClearCliTrustRecovery();
            ManualWorkspaceError = error.Message;
            return false;
        }
    }
}
