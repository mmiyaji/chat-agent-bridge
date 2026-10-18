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

    public bool TryAddTrustedFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Path.IsPathFullyQualified(folderPath) || !Directory.Exists(folderPath))
        {
            SetTrustedFolderStatus("TrustedFolderInvalid");
            return false;
        }

        try
        {
            var existing = ExecutionPolicy.NormalizeTrustedRoots(TrustedWorkspaceRoots);
            var candidate = ExecutionPolicy.NormalizeTrustedRoots(folderPath);
            var roots = existing.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
            var hadExplicitRoots = roots.Count > 0;

            // An empty list implicitly trusts the current workspace subtree. Preserve that
            // scope when adding an unrelated folder, unless the chosen folder already covers it.
            if (!hadExplicitRoots && Path.IsPathFullyQualified(WorkspaceRoot) && Directory.Exists(WorkspaceRoot) &&
                !ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = candidate }, WorkspaceRoot))
                roots.Add(ExecutionPolicy.NormalizeTrustedRoots(WorkspaceRoot));

            if (hadExplicitRoots && roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                SetTrustedFolderStatus("TrustedFolderAlreadyListed");
                return false;
            }

            roots.Add(candidate);
            TrustedWorkspaceRoots = ExecutionPolicy.NormalizeTrustedRoots(string.Join(Environment.NewLine, roots));
            SetTrustedFolderStatus("TrustedFolderAdded");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetTrustedFolderStatus("TrustedFolderRootsInvalid");
            return false;
        }
    }

    public void SetTrustedFolderStatus(string resourceKey) => TrustedFolderStatus = ViewModelText.Get(resourceKey);
}
