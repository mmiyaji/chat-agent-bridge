using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

public sealed record CliSessionOutput(string Provider, string? SessionId, string? FinalMessage,
    bool HasResult, bool TurnFailed, AgentUsage? Usage = null, IReadOnlyList<string>? DeniedActions = null);

/// <summary>Parses Gemini and Antigravity stream-json and Copilot CLI JSONL without exposing raw event payloads.</summary>
public static class CliSessionOutputParser
{
    internal const int MaxLineCharacters = 64 * 1024;
    internal const int MaxMessageCharacters = 1_000_000;
    private const int MaxDeniedActions = 8;
    private const int MaxTrackedSteps = 4096;

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
        private readonly StringBuilder _stepText = new();
        private readonly Dictionary<int, string> _stepTexts = [];
        private readonly HashSet<int> _announcedSteps = [];
        private readonly Dictionary<int, (long? Input, long? Output, long? Cached)> _stepUsage = [];
        private readonly List<string> _deniedActions = [];
        private bool _streamedText;
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
                // Antigravity names its event discriminator "event"; the other CLIs use "type".
                if (root.ValueKind != JsonValueKind.Object || String(root, _provider == AgentProfile.Antigravity ? "event" : "type") is null)
                { Invalidate(); return []; }
                return _provider switch
                {
                    AgentProfile.Gemini => Gemini(root),
                    AgentProfile.Antigravity => Antigravity(root),
                    _ => Copilot(root)
                };
            }
            catch (JsonException) { Invalidate(); return []; }
        }

        internal CliSessionOutput Snapshot()
        {
            var invalid = _invalid || _sessionId is null;
            var message = _message.Length == 0 ? null : _message.ToString();
            if (_deniedActions.Count > 0)
            {
                // The CLI reports success after auto-denying tools it could not prompt for; tell the user why nothing changed.
                var note = CoreText.Get("CliAntigravityActionsDenied", string.Join(", ", _deniedActions));
                message = message is null ? note : message.TrimEnd() + "\n\n" + note;
            }
            return new(_provider, invalid ? null : _sessionId, message,
                _hasResult, _failed || invalid || !_hasResult, _usage, _deniedActions.Count == 0 ? null : _deniedActions.ToArray());
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

        private IReadOnlyList<AgentRunProgress> Antigravity(JsonElement root)
        {
            if (_hasResult) { Invalidate(); return []; }
            switch (String(root, "event"))
            {
                case "init":
                    Bind(String(root, "conversation_id"));
                    return Activity("ProgressWorking");
                case "step_update":
                    return AntigravityStep(Property(root, "step_update"));
                case "result":
                    var result = Property(root, "result");
                    // The conversation id is also on init, so a result that omits it keeps the earlier binding.
                    if (String(result, "conversation_id") is { } conversationId) Bind(conversationId);
                    var status = String(result, "status");
                    _hasResult = status is not null;
                    _failed |= !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                    // result.usage totals the whole conversation, so a resumed turn is priced from this run's model calls.
                    // It is only a fallback for the first turn, where both totals are the same.
                    var (input, output, cached) = _stepUsage.Count > 0
                        ? (Sum(_stepUsage.Values.Select(usage => usage.Input)), Sum(_stepUsage.Values.Select(usage => usage.Output)),
                            Sum(_stepUsage.Values.Select(usage => usage.Cached)))
                        : Counter(result, "num_turns") == 1 ? AntigravityUsage(Property(result, "usage")) : default;
                    if (input is not null || output is not null || cached is not null)
                        _usage = new AgentUsage { InputTokens = input, OutputTokens = output, CachedInputTokens = cached,
                            Scope = "turn", Source = "antigravity.stream-json" };
                    var denied = Property(result, "denied_actions");
                    if (denied.ValueKind == JsonValueKind.Array)
                        foreach (var action in denied.EnumerateArray())
                        {
                            var name = SafeLabel(String(action, "display_name") ?? String(action, "action"));
                            if (name is not null && _deniedActions.Count < MaxDeniedActions && !_deniedActions.Contains(name))
                                _deniedActions.Add(name);
                        }
                    // The final response replaces streamed text; the streamed text is the fallback when it is empty.
                    var response = String(result, "response");
                    _message.Clear();
                    Append(string.IsNullOrWhiteSpace(response) ? _stepText.ToString() : response);
                    var progress = new List<AgentRunProgress>();
                    // A reply that never streamed is shown once, at the end.
                    if (!_streamedText && _message.Length > 0) progress.AddRange(AgentRunProgressParser.Split("stdout", _message.ToString()));
                    progress.AddRange(Activity(_failed ? "ProgressTurnFailed" : "ProgressFinished"));
                    return progress;
            }
            return [];
        }

        private IReadOnlyList<AgentRunProgress> AntigravityStep(JsonElement step)
        {
            if (step.ValueKind != JsonValueKind.Object) return [];
            if (String(step, "conversation_id") is { } conversationId) Bind(conversationId);
            var index = Property(step, "step_index") is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var parsed) ? parsed : -1;
            var state = String(step, "state");
            switch (String(step, "step_type"))
            {
                case "agent_response":
                    // Each response step is one model call; a later update for the same step replaces its counters.
                    var usage = AntigravityUsage(Property(step, "usage"));
                    if (index >= 0 && usage != default && (_stepUsage.ContainsKey(index) || _stepUsage.Count < MaxTrackedSteps))
                        _stepUsage[index] = usage;
                    var delta = String(step, "text_delta");
                    if (string.IsNullOrEmpty(delta)) return [];
                    var known = _stepTexts.TryGetValue(index, out var previous);
                    // A terminal update can repeat the whole step text instead of only the new part.
                    if (state == "DONE" && known && string.Equals(previous, delta, StringComparison.Ordinal)) return [];
                    if (known || _stepTexts.Count < MaxTrackedSteps) _stepTexts[index] = known ? previous + delta : delta;
                    if (!known && _stepText.Length > 0 && _stepText.Length < MaxMessageCharacters) _stepText.Append('\n');
                    _stepText.Append(delta, 0, Math.Min(delta.Length, Math.Max(0, MaxMessageCharacters - _stepText.Length)));
                    _streamedText = true;
                    return AgentRunProgressParser.Split("stdout", delta);
                case "tool":
                    if (state == "ACTIVE")
                    {
                        // The same tool step is updated several times; announce it once.
                        if (index >= 0 && (_announcedSteps.Count >= MaxTrackedSteps || !_announcedSteps.Add(index))) return [];
                        var toolName = SafeLabel(String(step, "tool_name") ?? String(Property(step, "tool_info"), "name"));
                        return AgentRunProgressParser.Split("activity",
                            (toolName is null ? CoreText.Get("ProgressUsingGenericTool") : CoreText.Get("ProgressUsingTool", toolName)) + Environment.NewLine);
                    }
                    return state is "DONE" or "ERROR" ? Activity("ProgressCommandFinished") : [];
            }
            return [];
        }

        // output_tokens already includes thinking_tokens (total = input + output), and input includes cache reads.
        private static (long? Input, long? Output, long? Cached) AntigravityUsage(JsonElement usage)
            => (Counter(usage, "input_tokens"), Counter(usage, "output_tokens"), Counter(usage, "cache_read_tokens"));

        private static long? Sum(IEnumerable<long?> counts)
        {
            long? total = null;
            try { foreach (var count in counts) if (count is { } value) total = checked((total ?? 0) + value); }
            catch (OverflowException) { return null; }
            return total;
        }

        /// <summary>Returns a short printable label for a tool or action name, or null when there is none.</summary>
        private static string? SafeLabel(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var clean = new string(name.Where(character => !char.IsControl(character)).Take(64).ToArray()).Trim();
            return clean.Length == 0 ? null : clean;
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
