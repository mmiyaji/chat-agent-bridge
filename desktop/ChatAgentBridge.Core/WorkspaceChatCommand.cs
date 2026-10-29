namespace ChatAgentBridge.Core;

/// <summary>Parses a configuration command, never a shell command.</summary>
public static class WorkspaceChatCommand
{
    public static bool TryParse(string text, out string path)
    {
        path = "";
        var line = text.Split(['\r', '\n'], 2)[0].Trim();
        var boundary = line.IndexOfAny([' ', '\t']);
        var verb = boundary < 0 ? line : line[..boundary];
        if (!verb.Equals("folder", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("/folder", StringComparison.OrdinalIgnoreCase)) return false;
        path = boundary < 0 ? "" : line[(boundary + 1)..].Trim();
        if (path.StartsWith('"') && path.EndsWith('"') && path.Length >= 2) path = path[1..^1];
        return true;
    }

    /// <summary>Parses <c>worktree NAME</c>. The name is not validated here; <see cref="GitWorktreeService.IsValidName"/> does that.</summary>
    public static bool TryParseWorktree(string text, out string name)
    {
        name = "";
        var line = text.Split(['\r', '\n'], 2)[0].Trim();
        var boundary = line.IndexOfAny([' ', '\t']);
        var verb = boundary < 0 ? line : line[..boundary];
        if (!verb.Equals("worktree", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("/worktree", StringComparison.OrdinalIgnoreCase)) return false;
        name = boundary < 0 ? "" : line[(boundary + 1)..].Trim();
        if (name.StartsWith('"') && name.EndsWith('"') && name.Length >= 2) name = name[1..^1];
        return true;
    }

    /// <summary>True for any command that rebinds the conversation workspace (folder or worktree).</summary>
    public static bool IsWorkspaceCommand(string text) => TryParse(text, out _) || TryParseWorktree(text, out _);
}
