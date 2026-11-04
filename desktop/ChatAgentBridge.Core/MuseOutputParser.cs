using System.Text.Json;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

/// <summary>Parses only the small, non-sensitive subset of Muse exec JSONL needed by the bridge.</summary>
public sealed class MuseOutputParser
{
    public const int MaxLineCharacters = 64 * 1024;
    public const int MaxOutputCharacters = 1_000_000;
    private const int MaxEvents = 100_000;

    private readonly string _expectedSessionId;
    private string? _sessionId;
    private string? _finalMessage;
    private string? _model;
    private bool _terminalSeen;
    private bool _failed;
    private bool _invalid;
    private bool _approvalPending;
    private bool _usageSeen;
    private bool _sessionMismatch;
    private bool _sessionExistenceConfirmed;
    private int _events;
    private long? _input;
    private long? _cached;
    private long? _output;

    public MuseOutputParser(string expectedSessionId)
    {
        if (!Guid.TryParseExact(expectedSessionId, "D", out var id)) throw new ArgumentException("Muse session id must be a UUID.", nameof(expectedSessionId));
        _expectedSessionId = id.ToString("D");
    }

    public bool ApprovalPending => _approvalPending;
    public bool SessionMismatch => _sessionMismatch && _sessionId is null;
    public bool IsInvalid => _invalid;
    public void RejectInput() => _invalid = true;

    /// <summary>Returns false for malformed or oversized input; unknown well-formed events are ignored.</summary>
    public bool AddLine(string line)
    {
        if (_invalid) return false;
        if (line.Length > MaxLineCharacters || ++_events > MaxEvents) return Invalidate();
        if (string.IsNullOrWhiteSpace(line)) return true;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalidate();

            var stream = Property(root, "stream");
            var streamId = String(stream, "id");
            var streamKind = String(stream, "kind");
            if (streamKind == "session" && streamId is not null)
            {
                if (!string.Equals(streamId, _expectedSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_sessionId is null) _sessionMismatch = true;
                    return true;
                }
                _sessionId ??= _expectedSessionId;
                _sessionMismatch = false;
            }
            else if (streamKind != "exec.usage" || !string.Equals(streamId, _expectedSessionId, StringComparison.OrdinalIgnoreCase)) return true;

            var payloadType = String(root, "payload_type");
            if (payloadType is null) return Invalidate();
            var payload = Property(root, "payload");
            if (payload.ValueKind != JsonValueKind.Object) return Invalidate();

            var isRootSession = streamKind == "session" && string.Equals(streamId, _expectedSessionId, StringComparison.OrdinalIgnoreCase);
            if (isRootSession && payloadType == "session.started" && String(payload, "kind") == "session_started")
                _sessionExistenceConfirmed = true;
            if (isRootSession && payloadType == "session.run.linked" && String(payload, "kind") == "session_run_linked" &&
                IsRunStream(Property(payload, "run_stream")))
                _sessionExistenceConfirmed = true;
            if (isRootSession && payloadType == "approval_wait.effect.started" && String(payload, "kind") == "approval_wait")
                _approvalPending = true;
            if (isRootSession && payloadType == "run.terminal.completed")
            {
                if (_terminalSeen) return Invalidate();
                _terminalSeen = true;
                if (String(payload, "kind") != "run_terminal" || String(payload, "terminal") != "completed") _failed = true;
                _finalMessage = Bounded(String(payload, "text"));
            }
            else if (isRootSession && payloadType.StartsWith("run.terminal.", StringComparison.Ordinal))
            {
                if (_terminalSeen) return Invalidate();
                _terminalSeen = true;
                _failed = true;
            }

            if (streamKind == "exec.usage" && string.Equals(streamId, _expectedSessionId, StringComparison.OrdinalIgnoreCase) && payloadType == "run.usage")
                ReadUsage(payload);
            if (isRootSession || streamKind == "exec.usage") ValidateTopLevelCounters(payload);
            return !_invalid;
        }
        catch (JsonException) { return Invalidate(); }
        catch (OverflowException) { return Invalidate(); }
    }

    public CliSessionOutput Complete(bool processSucceeded)
    {
        var hasResult = processSucceeded && !_invalid && !_failed && !_approvalPending && _terminalSeen && _sessionId is not null;
        AgentUsage? usage = null;
        if (_usageSeen)
            usage = new AgentUsage
            {
                InputTokens = _input,
                CachedInputTokens = _cached,
                OutputTokens = _output,
                InputIncludesCache = true,
                Scope = "turn",
                Source = "muse.exec-jsonl",
                Model = _model
            };
        return new CliSessionOutput(AgentProfile.Muse, _sessionId, hasResult ? _finalMessage : null,
            hasResult, !hasResult, usage)
        {
            SessionExistenceConfirmed = _sessionExistenceConfirmed,
            FailureGuidance = _approvalPending ? CoreText.Get("MuseApprovalUnavailable")
                : _invalid ? CoreText.Get("MuseProtocolInvalid")
                : processSucceeded && !_terminalSeen && !SessionMismatch ? CoreText.Get("MuseResultMissing") : null
        };
    }

    private void ReadUsage(JsonElement payload)
    {
        _usageSeen = true;
        var models = Property(payload, "models");
        if (models.ValueKind != JsonValueKind.Array) { _input = _cached = _output = null; return; }
        if (models.GetArrayLength() == 0) { _input = _cached = _output = null; _model = null; return; }
        long input = 0, cached = 0, output = 0;
        var hasInput = true; var hasCached = true; var hasOutput = true;
        string? model = null;
        var hasModel = false;
        var mixedModels = false;
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { Invalidate(); return; }
            var itemModel = String(item, "model");
            if (!hasModel) { model = itemModel; hasModel = true; }
            else if (!string.Equals(itemModel, model, StringComparison.Ordinal)) mixedModels = true;
            AddCounter(item, "input_tokens", ref input, ref hasInput);
            AddCounter(item, "cached_tokens", ref cached, ref hasCached);
            AddCounter(item, "output_tokens", ref output, ref hasOutput);
        }
        _input = hasInput ? input : null;
        _cached = hasCached ? cached : null;
        _output = hasOutput ? output : null;
        _model = mixedModels ? null : model;
    }

    private void ValidateTopLevelCounters(JsonElement payload)
    {
        foreach (var name in new[] { "calls", "completed_calls", "failed_calls", "cancelled_calls" })
        {
            var counter = Property(payload, name);
            if (counter.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            if (counter.ValueKind != JsonValueKind.Number || !counter.TryGetInt64(out var value) || value < 0) { Invalidate(); return; }
        }
    }

    private void AddCounter(JsonElement obj, string name, ref long total, ref bool complete)
    {
        var property = Property(obj, name);
        if (property.ValueKind == JsonValueKind.Undefined || property.ValueKind == JsonValueKind.Null) { complete = false; return; }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value) || value < 0) { Invalidate(); return; }
        total = checked(total + value);
    }

    private static string? Bounded(string? text) => text is null ? null : text[..Math.Min(text.Length, MaxOutputCharacters)];
    private static bool IsRunStream(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || String(value, "kind") != "run") return false;
        return Guid.TryParseExact(String(value, "id"), "D", out _);
    }
    private bool Invalidate() { _invalid = true; return false; }
    private static JsonElement Property(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;
    private static string? String(JsonElement obj, string name) => Property(obj, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
