namespace ChatAgentBridge.Core;

/// <summary>Only registered provider names and explicit handoff modes are accepted; never executable paths.</summary>
public sealed record AgentChatCommand(string? Provider, bool? IncludeSummary, bool IsValid)
{
    public static bool TryParse(string text, out AgentChatCommand command)
    {
        command = new(null, null, false);
        var value = text.Trim();
        if (!value.StartsWith("!cab", StringComparison.OrdinalIgnoreCase) || value.Length <= 4 || !char.IsWhiteSpace(value[4]))
            return false;
        value = value[4..].TrimStart();
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("agent", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == 1) { command = new(null, null, true); return true; }
        var provider = parts[1].ToLowerInvariant();
        if (provider is not (AgentProfile.Codex or AgentProfile.Claude))
        {
            // !cab also starts ordinary requests: "!cab agent のログを調べて".
            // Reserve unknown targets only when the text has explicit switch syntax
            // or attempts to supply an executable; prose should reach the agent.
            return (parts.Length == 3 && parts[2].ToLowerInvariant() is "summary" or "fresh") ||
                provider.IndexOfAny(['/', '\\', ':']) >= 0 ||
                provider.EndsWith(".exe", StringComparison.Ordinal) || provider.EndsWith(".cmd", StringComparison.Ordinal) ||
                provider.EndsWith(".ps1", StringComparison.Ordinal);
        }
        if (parts.Length > 3) return true;
        if (parts.Length == 2) { command = new(provider, null, true); return true; }
        var mode = parts[2].ToLowerInvariant();
        command = new(provider, mode == "summary", mode is "summary" or "fresh");
        return true;
    }
}
