using System.Text.Json;

namespace ChatAgentBridge.Core.Sessions;

public sealed record CodexOutput(string? SessionId, string? FinalMessage, bool TurnFailed, bool HasEvents = false);

public static class CodexOutputParser
{
    private static readonly HashSet<string> RecognizedEvents = new(StringComparer.Ordinal)
    {
        "thread.started", "turn.started", "turn.completed", "turn.failed",
        "item.started", "item.updated", "item.completed", "error"
    };

    public static CodexOutput Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? sessionId = null;
        string? finalMessage = null;
        var turnFailed = false;
        var hasEvents = false;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                    continue;
                var type = typeElement.GetString();
                if (type is not null && RecognizedEvents.Contains(type)) hasEvents = true;
                if (type == "thread.started" && root.TryGetProperty("thread_id", out var threadId) &&
                    threadId.ValueKind == JsonValueKind.String && Guid.TryParse(threadId.GetString(), out var id))
                    sessionId = id.ToString("D");
                else if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var itemType) &&
                    itemType.ValueKind == JsonValueKind.String && itemType.GetString() == "agent_message" &&
                    item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    finalMessage = text.GetString();
                else if (type == "turn.failed") turnFailed = true;
            }
            catch (JsonException) { }
        }
        return new CodexOutput(sessionId, finalMessage, turnFailed, hasEvents);
    }
}
