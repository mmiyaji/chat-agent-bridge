using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public sealed record CliTrustRecovery(string WorkspacePath, string TargetPath)
{
    public bool UsesLinkTarget => !string.Equals(WorkspacePath, TargetPath, StringComparison.OrdinalIgnoreCase);
}

public partial class MainPageViewModel
{
    private CliTrustRecovery? _cliTrustRecovery;
    private string? _cliTrustRecoverySignature;
    public bool HasCliTrustRecovery => _cliTrustRecovery is not null;

    private void SetCliTrustRecovery(CliWorkspaceTrustException error, string signature)
    {
        _cliTrustRecovery = new(error.WorkspacePath, error.SuggestedWorkspacePath ?? error.WorkspacePath);
        _cliTrustRecoverySignature = signature;
        OnPropertyChanged(nameof(HasCliTrustRecovery));
    }

    private void ClearCliTrustRecovery()
    {
        _cliTrustRecovery = null;
        _cliTrustRecoverySignature = null;
        OnPropertyChanged(nameof(HasCliTrustRecovery));
    }

    public CliTrustRecovery? GetCliTrustRecovery() =>
        CanCheckCli() && _cliTrustRecoverySignature == CurrentCliSignature() ? _cliTrustRecovery : null;

    // Called only after the user has reviewed the exact path and explicitly agreed.
    public bool TryApplyCliTrustRecovery(CliTrustRecovery recovery)
    {
        if (!ReferenceEquals(GetCliTrustRecovery(), recovery)) return false;
        var path = recovery.TargetPath;
        // Revalidate at consent time. Never accept a root that became a link while the dialog was open.
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path) ||
            !ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = path }, path))
            return FailCliTrustRecovery("TrustedFolderRecoveryChanged");
        try { ExecutionPolicy.NormalizeTrustedRoots(TrustedWorkspaceRoots); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return FailCliTrustRecovery("TrustedFolderRootsInvalid"); }
        if (recovery.UsesLinkTarget) WorkspaceRoot = path;
        if (TryAddTrustedFolder(path) ||
            ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = TrustedWorkspaceRoots }, path))
        {
            ClearCliTrustRecovery();
            CliCheckFailed = false;
            CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
            return true;
        }
        return FailCliTrustRecovery("TrustedFolderRecoveryChanged");
    }

    private bool FailCliTrustRecovery(string key)
    {
        ClearCliTrustRecovery();
        SetTrustedFolderStatus(key);
        CliCheckSummary = TrustedFolderStatus;
        SetStatus(ViewModelText.Get("CheckWorkspace"), TrustedFolderStatus, "Error");
        return false;
    }
}
