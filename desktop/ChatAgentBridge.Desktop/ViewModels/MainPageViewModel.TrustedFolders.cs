using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    partial void OnTrustedWorkspaceRootsChanged(string value)
    {
        if (!IsPickingTrustedFolder) TrustedFolderStatus = string.Empty;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartTrustedFolderPick))]
    public partial bool IsPickingTrustedFolder { get; set; }

    [ObservableProperty]
    public partial string TrustedFolderStatus { get; set; } = string.Empty;

    public bool CanStartTrustedFolderPick => !IsPickingTrustedFolder;

    public async Task<bool> TryAddTrustedFolderAsync(string? folderPath)
    {
        var candidatePath = folderPath;
        var workspaceRoot = WorkspaceRoot;
        var trustedRoots = TrustedWorkspaceRoots;
        var result = await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath) || !Directory.Exists(candidatePath))
                return (Success: false, Status: "TrustedFolderInvalid", Roots: (string?)null);
            try
            {
                var existing = ExecutionPolicy.NormalizeTrustedRoots(trustedRoots);
                var candidate = ExecutionPolicy.NormalizeTrustedRoots(candidatePath);
                var roots = existing.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
                var hadExplicitRoots = roots.Count > 0;
                var workspaceExists = Path.IsPathFullyQualified(workspaceRoot) && Directory.Exists(workspaceRoot);

                // An empty list implicitly trusts the current workspace subtree. Preserve that
                // scope when adding an unrelated folder, unless the chosen folder already covers it.
                if (!hadExplicitRoots && workspaceExists &&
                    !ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = candidate }, workspaceRoot))
                    roots.Add(ExecutionPolicy.NormalizeTrustedRoots(workspaceRoot));

                if (hadExplicitRoots && roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return (Success: false, Status: "TrustedFolderAlreadyListed", Roots: (string?)null);

                roots.Add(candidate);
                var normalizedRoots = ExecutionPolicy.NormalizeTrustedRoots(string.Join(Environment.NewLine, roots));
                return (Success: true, Status: "TrustedFolderAdded", Roots: normalizedRoots);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return (Success: false, Status: "TrustedFolderRootsInvalid", Roots: (string?)null);
            }
        });

        if (!string.Equals(workspaceRoot, WorkspaceRoot, StringComparison.Ordinal) ||
            !string.Equals(trustedRoots, TrustedWorkspaceRoots, StringComparison.Ordinal)) return false;
        if (result.Success) TrustedWorkspaceRoots = result.Roots!;
        SetTrustedFolderStatus(result.Status);
        return result.Success;
    }

    public void SetTrustedFolderStatus(string resourceKey) => TrustedFolderStatus = ViewModelText.Get(resourceKey);
}
