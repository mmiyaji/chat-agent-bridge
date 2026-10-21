namespace ChatAgentBridge.Core;

/// <summary>Describes a CLI probe rejected because its workspace is outside the trusted folders.</summary>
public sealed class CliWorkspaceTrustException : InvalidOperationException
{
    public string WorkspacePath { get; }
    public string? SuggestedWorkspacePath { get; }

    public CliWorkspaceTrustException(string workspacePath, string? suggestedWorkspacePath, string message)
        : base(message)
    {
        WorkspacePath = workspacePath;
        SuggestedWorkspacePath = suggestedWorkspacePath;
    }
}
