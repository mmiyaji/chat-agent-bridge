using System.Text.Json;

namespace ChatAgentBridge.Core.Sessions;

public sealed record CodexOutput(string? SessionId, string? FinalMessage, bool TurnFailed, bool HasEvents = false);

public static class CodexOutputParser
{
    internal const int MaxLineCharacters = 2 * 1024 * 1024;
    private static readonly HashSet<string> RecognizedEvents = new(StringComparer.Ordinal)
    {
        "thread.started", "turn.started", "turn.completed", "turn.failed",
        "item.started", "item.updated", "item.completed", "error"
    };

    public static CodexOutput Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var accumulator = new Accumulator();
        foreach (var line in output.Split('\n'))
            accumulator.AddLine(line);
        return accumulator.Snapshot();
    }

    internal sealed class Accumulator
    {
        private string? _sessionId;
        private string? _finalMessage;
        private bool _turnFailed;
        private bool _hasEvents;

        internal void AddLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLineCharacters || trimmed[0] != '{') return;
            try
            {
                using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                    return;
                var type = typeElement.GetString();
                if (type is not null && RecognizedEvents.Contains(type)) _hasEvents = true;
                if (type == "thread.started" && root.TryGetProperty("thread_id", out var threadId) &&
                    threadId.ValueKind == JsonValueKind.String && Guid.TryParse(threadId.GetString(), out var id))
                    _sessionId = id.ToString("D");
                else if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var itemType) &&
                    itemType.ValueKind == JsonValueKind.String && itemType.GetString() == "agent_message" &&
                    item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    _finalMessage = text.GetString();
                else if (type == "turn.failed") _turnFailed = true;
            }
            catch (JsonException) { }
        }

        internal CodexOutput Snapshot() => new(_sessionId, _finalMessage, _turnFailed, _hasEvents);
    }
}
