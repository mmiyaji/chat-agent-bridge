using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public sealed record AgentSwitchSelection(string Provider, bool IncludeSummary, AgentConnectionProfile? ConfirmedProfile = null);

public sealed class ConversationAgentService(SessionStore sessions, AgentConnectionStore connections,
    ConversationSummaryStore summaries)
{
    public async Task SwitchAsync(SessionBinding expected, BridgeSettings defaults, AgentSwitchSelection selection,
        Func<CancellationToken, Task<bool>> canSwitch, CancellationToken token = default)
    {
        if (expected.Key.UserId is null || expected.Key.Origin is null || expected.Key.Provider is not ("slack" or "mattermost" or "discord"))
            throw new ArgumentException("Agent switching requires an owner-scoped chat conversation.", nameof(expected));
        using var operation = sessions.TryBeginConversationOperation(expected.Key)
            ?? throw new InvalidOperationException("The conversation is busy.");
        if (!await canSwitch(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
        var profile = await connections.FindAsync(selection.Provider, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Register and check the target CLI connection on this PC first.");
        if (selection.ConfirmedProfile is not null && profile != selection.ConfirmedProfile)
            throw new InvalidOperationException("The confirmed CLI profile changed. Try again.");
        if (!ExecutionPolicy.IsWorkspaceTrusted(defaults, expected.WorkspaceRoot))
            throw new InvalidOperationException("The conversation workspace is not trusted.");
        var currentProvider = expected.NextAgentProvider ?? ProviderOf(expected);
        if (currentProvider == profile.Provider && expected.NextAgentProvider is null)
            throw new InvalidOperationException("This conversation already uses that agent.");
        if (ProviderOf(expected) is null)
            throw new InvalidOperationException("Switching requires a supported CLI conversation.");
        string? summary = null;
        if (selection.IncludeSummary)
        {
            var note = await summaries.FindAsync(expected.Key, token).ConfigureAwait(false);
            if (note is null || note.SessionId != expected.SessionId || note.SourceProvider != ProviderOf(expected))
                throw new InvalidOperationException("No matching conversation notes are available. Choose a fresh session.");
            summary = note.Text;
        }
        if (!await canSwitch(token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
        var latest = await connections.FindAsync(selection.Provider, token).ConfigureAwait(false);
        if (profile != latest) throw new InvalidOperationException("The registered CLI connection changed. Try again.");
        await sessions.SwitchAgentAsync(expected, profile.Provider, summary, token).ConfigureAwait(false);
    }

    public static string? ProviderOf(SessionBinding binding)
    {
        if (binding.AgentArguments is "app-server" or "exec --json -") return AgentProfile.Codex;
        if (binding.AgentArguments == "claude-stream-json-v1") return AgentProfile.Claude;
        foreach (var provider in new[] { AgentProfile.Gemini, AgentProfile.GitHubCopilot, AgentProfile.Antigravity })
        {
            var prefix = CliSessionMarker(provider) + ":";
            if (binding.AgentArguments.StartsWith(prefix, StringComparison.Ordinal) &&
                binding.AgentArguments.Length == prefix.Length + 64 &&
                binding.AgentArguments[prefix.Length..].All(Uri.IsHexDigit)) return provider;
        }
        return null;
    }

    internal static string CliSessionMarker(string provider) => provider switch
    {
        AgentProfile.Gemini => "gemini-stream-json-v1",
        AgentProfile.GitHubCopilot => "copilot-jsonl-v1",
        AgentProfile.Antigravity => "antigravity-stream-json-v1",
        _ => throw new ArgumentException("Unsupported structured CLI provider.", nameof(provider))
    };
}
