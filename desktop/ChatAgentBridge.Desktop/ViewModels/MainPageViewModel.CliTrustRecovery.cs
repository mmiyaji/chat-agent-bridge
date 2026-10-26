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
        OnPropertyChanged(nameof(HasManualTrustRecovery));
    }

    private void ClearCliTrustRecovery()
    {
        _cliTrustRecovery = null;
        _cliTrustRecoverySignature = null;
        OnPropertyChanged(nameof(HasCliTrustRecovery));
        OnPropertyChanged(nameof(HasManualTrustRecovery));
    }

    public CliTrustRecovery? GetCliTrustRecovery() =>
        CanCheckCli() && _cliTrustRecoverySignature == CurrentCliSignature() ? _cliTrustRecovery : null;

    // Called only after the user has reviewed the exact path and explicitly agreed.
    public async Task<bool> TryApplyCliTrustRecoveryAsync(CliTrustRecovery recovery)
    {
        var signature = CurrentCliSignature();
        if (!ReferenceEquals(GetCliTrustRecovery(), recovery) || _cliTrustRecoverySignature != signature) return false;
        var path = recovery.TargetPath;
        var workspaceRoot = WorkspaceRoot;
        var trustedRoots = TrustedWorkspaceRoots;
        var intendedWorkspaceRoot = recovery.UsesLinkTarget ? path : workspaceRoot;

        // Revalidate consent, path existence, link safety, and the resulting trust scope
        // together off the UI thread. No state is applied until the captured context is checked again.
        var result = await Task.Run(() =>
        {
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path) ||
                !ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = path }, path))
                return (Valid: false, Roots: (string?)null, Error: "TrustedFolderRecoveryChanged");
            try
            {
                var roots = ExecutionPolicy.NormalizeTrustedRoots(trustedRoots)
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
                var candidate = ExecutionPolicy.NormalizeTrustedRoots(path);
                var hadExplicitRoots = roots.Count > 0;
                if (!hadExplicitRoots && Path.IsPathFullyQualified(intendedWorkspaceRoot) && Directory.Exists(intendedWorkspaceRoot) &&
                    !ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = candidate }, intendedWorkspaceRoot))
                    roots.Add(ExecutionPolicy.NormalizeTrustedRoots(intendedWorkspaceRoot));
                if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase)) roots.Add(candidate);
                var updatedRoots = ExecutionPolicy.NormalizeTrustedRoots(string.Join(Environment.NewLine, roots));
                return ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = updatedRoots }, path)
                    ? (Valid: true, Roots: (string?)updatedRoots, Error: (string?)null)
                    : (Valid: false, Roots: (string?)null, Error: (string?)"TrustedFolderRecoveryChanged");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return (Valid: false, Roots: (string?)null, Error: (string?)"TrustedFolderRootsInvalid");
            }
        });

        if (!ReferenceEquals(GetCliTrustRecovery(), recovery) || _cliTrustRecoverySignature != signature ||
            !string.Equals(signature, CurrentCliSignature(), StringComparison.Ordinal) ||
            !string.Equals(workspaceRoot, WorkspaceRoot, StringComparison.Ordinal) ||
            !string.Equals(trustedRoots, TrustedWorkspaceRoots, StringComparison.Ordinal)) return false;
        if (!result.Valid) return FailCliTrustRecovery(result.Error ?? "TrustedFolderRecoveryChanged");

        if (recovery.UsesLinkTarget) WorkspaceRoot = path;
        TrustedWorkspaceRoots = result.Roots!;
        ClearCliTrustRecovery();
        CliCheckFailed = false;
        CliCheckSummary = ViewModelText.Get("CliSettingsChanged");
        return true;
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
