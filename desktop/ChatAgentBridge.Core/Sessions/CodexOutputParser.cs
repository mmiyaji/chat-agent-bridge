using System.Text.Json;

namespace ChatAgentBridge.Core.Sessions;

public sealed record CodexOutput(string? SessionId, string? FinalMessage, bool TurnFailed, bool HasEvents = false)
{
    public AgentUsage? Usage { get; init; }
}

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
        private AgentUsage? _usage;

        internal void AddLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLineCharacters || trimmed[0] != '{') return;
            try
            {
                using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 64 });
                AddParsed(document.RootElement);
            }
            catch (JsonException) { }
        }

        /// <summary>Consumes a line already parsed by the caller (same semantics as <see cref="AddLine(string)"/>).</summary>
        internal void AddParsed(JsonElement root)
        {
            try
            {
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
                else if (type == "turn.completed" && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    _usage = new AgentUsage
                    {
                        InputTokens = ReadCounter(usage, "input_tokens"),
                        OutputTokens = ReadCounter(usage, "output_tokens"),
                        CachedInputTokens = ReadCounter(usage, "cached_input_tokens"),
                        InputIncludesCache = true,
                        Scope = "turn",
                        Source = "codex.exec",
                        Model = TryString(root, "model")
                    };
                }
            }
            catch (JsonException) { }
        }

        internal CodexOutput Snapshot() => new(_sessionId, _finalMessage, _turnFailed, _hasEvents) { Usage = _usage };

        private static long? ReadCounter(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var counter) && counter >= 0 ? counter : null;

        private static string? TryString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
