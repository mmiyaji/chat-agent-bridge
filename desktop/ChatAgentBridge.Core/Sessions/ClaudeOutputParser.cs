using System.Text.Json;

namespace ChatAgentBridge.Core.Sessions;

public sealed record ClaudeOutput(string? SessionId, string? FinalMessage, bool TurnFailed, bool HasEvents = false)
{
    public AgentUsage? Usage { get; init; }
    public bool HasResult { get; init; }
}

public static class ClaudeOutputParser
{
    private const int MaxLineCharacters = 2 * 1024 * 1024;
    private static readonly HashSet<string> RecognizedEvents = new(StringComparer.Ordinal)
        { "system", "assistant", "user", "result", "error", "content_block_delta" };

    public static ClaudeOutput Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var accumulator = new Accumulator();
        foreach (var line in output.Split('\n')) accumulator.AddLine(line);
        return accumulator.Snapshot();
    }

    internal sealed class Accumulator
    {
        private string? _sessionId;
        private string? _finalMessage;
        private bool _turnFailed;
        private bool _hasEvents;
        private bool _hasResult;
        private AgentUsage? _usage;

        internal void AddLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLineCharacters || trimmed[0] != '{') return;
            try
            {
                using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type)) return;
                if (RecognizedEvents.Contains(type!)) _hasEvents = true;
                if (TryString(root, "session_id", out var session) && Guid.TryParse(session, out var id))
                    _sessionId = id.ToString("D");
                if (type == "error") _turnFailed = true;
                if (type != "result") return;
                if (!TryString(root, "subtype", out var subtype))
                {
                    _turnFailed = true;
                    return;
                }
                _hasResult = true;

                if (TryString(root, "result", out var result)) _finalMessage = result;
                if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    _turnFailed = true;
                if (subtype != "success") _turnFailed = true;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    _usage = new AgentUsage
                    {
                        InputTokens = ReadCounter(usage, "input_tokens"),
                        OutputTokens = ReadCounter(usage, "output_tokens"),
                        CachedInputTokens = ReadCounter(usage, "cache_read_input_tokens"),
                        CacheWriteInputTokens = ReadCounter(usage, "cache_creation_input_tokens"),
                        InputIncludesCache = false,
                        Scope = "turn",
                        Source = "claude.stream-json",
                        Model = ReadUsageModel(root)
                    };
                }
            }
            catch (JsonException) { }
        }

        internal ClaudeOutput Snapshot() => new(_sessionId, _finalMessage, _turnFailed, _hasEvents)
            { Usage = _usage, HasResult = _hasResult };

        private static bool TryString(JsonElement element, string property, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String) return false;
            value = item.GetString();
            return value is not null;
        }

        private static long? ReadCounter(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var counter) && counter >= 0 ? counter : null;

        private static string? ReadUsageModel(JsonElement root)
        {
            if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object)
            {
                using var properties = modelUsage.EnumerateObject();
                if (properties.MoveNext())
                {
                    var model = properties.Current.Name;
                    return properties.MoveNext() ? null : model;
                }
            }
            return TryString(root, "model", out var explicitModel) ? explicitModel : null;
        }
    }
}
