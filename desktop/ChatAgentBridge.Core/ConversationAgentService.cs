using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public sealed record AgentSwitchSelection(string Provider, bool IncludeSummary, AgentConnectionProfile? ConfirmedProfile = null);

public sealed class ConversationAgentService(SessionStore sessions, AgentConnectionStore connections,
    ConversationSummaryStore summaries)
{
    public async Task SwitchAsync(SessionBinding expected, BridgeSettings defaults, AgentSwitchSelection selection,
        Func<CancellationToken, Task<bool>> canSwitch, CancellationToken token = default)
    {
        if (expected.Key.UserId is null || expected.Key.Origin is null || expected.Key.Provider is not ("slack" or "mattermost"))
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
        if (ProviderOf(expected) is not (AgentProfile.Codex or AgentProfile.Claude))
            throw new InvalidOperationException("Switching is supported for Codex and Claude Code conversations.");
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

    public static string? ProviderOf(SessionBinding binding) => binding.AgentArguments switch
    {
        "app-server" or "exec --json -" => AgentProfile.Codex,
        "claude-stream-json-v1" => AgentProfile.Claude,
        _ => null
    };
}
