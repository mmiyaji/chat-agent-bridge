using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

public sealed record CliSessionOutput(string Provider, string? SessionId, string? FinalMessage,
    bool HasResult, bool TurnFailed, AgentUsage? Usage = null);

/// <summary>Parses Gemini stream-json and Copilot CLI JSONL without exposing raw event payloads.</summary>
public static class CliSessionOutputParser
{
    internal const int MaxLineCharacters = 64 * 1024;
    internal const int MaxMessageCharacters = 1_000_000;

    public static CliSessionOutput Parse(string output, string provider)
    {
        ArgumentNullException.ThrowIfNull(output);
        var parser = new Accumulator(provider);
        // Read through the entire stream, including terminal metadata beyond the capture limit.
        var start = 0;
        for (var index = 0; index <= output.Length; index++)
        {
            if (index != output.Length && output[index] != '\n') continue;
            if (index - start > MaxLineCharacters) parser.Invalidate();
            else parser.AddLine(output.Substring(start, index - start));
            start = index + 1;
        }
        return parser.Snapshot();
    }

    internal sealed class Accumulator
    {
        private readonly string _provider;
        private readonly StringBuilder _message = new();
        private string? _sessionId;
        private string? _model;
        private string? _deltaMessageId;
        private bool _hasResult;
        private bool _failed;
        private bool _invalid;
        private bool _truncated;
        private AgentUsage? _usage;

        internal Accumulator(string provider)
        {
            if (!AgentProfile.IsBasicCliProvider(provider)) throw new ArgumentException("Unsupported structured CLI provider.", nameof(provider));
            _provider = provider;
        }

        internal bool Truncated => _truncated;
        internal void Invalidate() => _invalid = true;

        internal IReadOnlyList<AgentRunProgress> AddLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return [];
            if (line.Length > MaxLineCharacters) { Invalidate(); return []; }
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || String(root, "type") is null) { Invalidate(); return []; }
                return _provider == AgentProfile.Gemini ? Gemini(root) : Copilot(root);
            }
            catch (JsonException) { Invalidate(); return []; }
        }

        internal CliSessionOutput Snapshot()
        {
            var invalid = _invalid || _sessionId is null;
            return new(_provider, invalid ? null : _sessionId, _message.Length == 0 ? null : _message.ToString(),
                _hasResult, _failed || invalid || !_hasResult, _usage);
        }

        private IReadOnlyList<AgentRunProgress> Gemini(JsonElement root)
        {
            var type = String(root, "type");
            if (_hasResult) { Invalidate(); return []; }
            switch (type)
            {
                case "init":
                    Bind(String(root, "session_id"));
                    _model = String(root, "model");
                    return Activity("ProgressWorking");
                case "message" when String(root, "role") == "assistant":
                    return Message(String(root, "content"), Property(root, "delta").ValueKind == JsonValueKind.True);
                case "tool_use":
                    return Activity("ProgressUsingGenericTool");
                case "tool_result":
                    return Activity("ProgressCommandFinished");
                case "error":
                    if (String(root, "severity") != "warning") _failed = true;
                    return Activity("ProgressTurnFailed");
                case "result":
                    var status = String(root, "status");
                    _hasResult = status is "success" or "error";
                    _failed |= status != "success";
                    var stats = Property(root, "stats");
                    var input = Counter(stats, "input_tokens");
                    var output = Counter(stats, "output_tokens");
                    var cached = Counter(stats, "cached");
                    if (input is not null || output is not null || cached is not null)
                        _usage = new AgentUsage { InputTokens = input, OutputTokens = output, CachedInputTokens = cached,
                            Scope = "turn", Source = "gemini.stream-json", Model = GeminiUsageModel(stats) };
                    return Activity(_failed ? "ProgressTurnFailed" : "ProgressFinished");
            }
            return [];
        }

        private IReadOnlyList<AgentRunProgress> Copilot(JsonElement root)
        {
            var type = String(root, "type");
            var data = Property(root, "data");
            // Runtime subagent events carry this routing property; never mix their text or ids into the main reply.
            if (String(root, "parentToolCallId") is not null || String(data, "parentToolCallId") is not null) return [];
            if (type == "result")
            {
                if (_hasResult) { Invalidate(); return []; }
                Bind(String(root, "sessionId"));
                var exitCode = Property(root, "exitCode");
                _hasResult = exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out _);
                _failed |= !_hasResult || exitCode.GetInt32() != 0;
                // The CLI's result.usage contains premium requests and durations, not token counters.
                return Activity(_failed ? "ProgressTurnFailed" : "ProgressFinished");
            }
            if (_hasResult) { Invalidate(); return []; }
            switch (type)
            {
                case "session.start":
                case "session.resume":
                    // Resume events can omit sessionId; terminal result still must report it.
                    if (Property(data, "sessionId").ValueKind != JsonValueKind.Undefined) Bind(String(data, "sessionId"));
                    _model = String(data, "selectedModel") ?? _model;
                    return Activity("ProgressWorking");
                case "assistant.message_delta":
                    var messageId = String(data, "messageId");
                    if (messageId is null) { Invalidate(); return []; }
                    if (_deltaMessageId != messageId) { _message.Clear(); _deltaMessageId = messageId; }
                    return Message(String(data, "deltaContent"), append: true);
                case "assistant.message":
                    var text = String(data, "content");
                    var hadDeltas = _deltaMessageId is not null && _deltaMessageId == String(data, "messageId");
                    _message.Clear();
                    Append(text);
                    _deltaMessageId = null;
                    return hadDeltas ? [] : AgentRunProgressParser.Split("stdout", text);
                case "tool.execution_start":
                    return Activity("ProgressUsingGenericTool");
                case "tool.execution_complete":
                    return Activity("ProgressCommandFinished");
                case "session.error":
                    // The runtime can recover from individual model-call failures before the terminal outcome.
                    if (String(data, "errorType") != "model_call") _failed = true;
                    return Activity("ProgressTurnFailed");
                case "abort":
                    _failed = true;
                    return Activity("ProgressTurnFailed");
            }
            return [];
        }

        private string? GeminiUsageModel(JsonElement stats)
        {
            var models = Property(stats, "models");
            if (models.ValueKind != JsonValueKind.Object) return _model;
            var names = models.EnumerateObject().Select(model => model.Name).ToArray();
            // Aggregate counters from several models cannot be priced as the initial model.
            return names.Length switch { 0 => _model, 1 => names[0], _ => null };
        }

        private IReadOnlyList<AgentRunProgress> Message(string? text, bool append)
        {
            if (!append) _message.Clear();
            Append(text);
            return AgentRunProgressParser.Split("stdout", text);
        }

        private void Append(string? text)
        {
            if (text is null) return;
            var length = Math.Min(text.Length, MaxMessageCharacters - _message.Length);
            if (length > 0) _message.Append(text, 0, length);
            if (length < text.Length) _truncated = true;
        }

        private void Bind(string? sessionId)
        {
            if (!Guid.TryParseExact(sessionId, "D", out var id)) { Invalidate(); return; }
            var normalized = id.ToString("D");
            if (_sessionId is not null && !string.Equals(_sessionId, normalized, StringComparison.Ordinal)) Invalidate();
            else _sessionId = normalized;
        }

        private static IReadOnlyList<AgentRunProgress> Activity(string key)
            => AgentRunProgressParser.Split("activity", CoreText.Get(key) + Environment.NewLine);
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
    private static long? Counter(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.Number } found && found.TryGetInt64(out var count) && count >= 0 ? count : null;
}
