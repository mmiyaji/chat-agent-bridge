using System.Text.Json;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

public sealed record AgentRunProgress(string Stream, string Text);

/// <summary>Converts provider JSONL events into bounded, user-facing progress text.</summary>
internal sealed class AgentRunProgressParser(string provider)
{
    internal const int MaxLineCharacters = 64 * 1024;
    internal const int MaxTextCharacters = 4 * 1024;
    private readonly HashSet<int> _claudePartialBlockIndices = [];

    public IReadOnlyList<AgentRunProgress> ParseLine(string line)
    {
        if (line.Length == 0 || line.Length > MaxLineCharacters || line[0] != '{') return [];
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            return ParseRoot(document.RootElement);
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Parses an already-parsed event so callers can share one JsonDocument per line.</summary>
    public IReadOnlyList<AgentRunProgress> ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        return provider == AgentProfile.Codex ? ParseCodex(root) : ParseClaude(root);
    }

    private static IReadOnlyList<AgentRunProgress> ParseCodex(JsonElement root)
    {
        var type = String(root, "type");
        var item = Property(root, "item");
        var itemType = String(item, "type");
        string? message = type switch
        {
            "item.started" or "item.updated" when itemType == "command_execution" =>
                CoreText.Get("ProgressRunningCommand", String(item, "command") ?? CoreText.Get("ProgressGenericCommand")),
            "item.completed" when itemType == "agent_message" => String(item, "text"),
            "item.completed" when itemType == "command_execution" =>
                String(item, "aggregated_output") is { } commandOutput
                    ? CoreText.Get("ProgressCommandResult", commandOutput) : CoreText.Get("ProgressCommandFinished"),
            "turn.started" => CoreText.Get("ProgressWorking"),
            "turn.completed" => CoreText.Get("ProgressFinished"),
            "turn.failed" => CoreText.Get("ProgressTurnFailed"),
            "error" => String(root, "message"),
            _ => null
        };
        return Split("activity", message is null ? null : message + Environment.NewLine);
    }

    private IReadOnlyList<AgentRunProgress> ParseClaude(JsonElement root)
    {
        var type = String(root, "type");
        if (type == "stream_event") return ParseClaudeStreamEvent(Property(root, "event"));
        if (type == "assistant")
        {
            var content = Property(Property(root, "message"), "content");
            if (content.ValueKind != JsonValueKind.Array) return [];
            var result = new List<AgentRunProgress>();
            var index = 0;
            foreach (var block in content.EnumerateArray())
            {
                var blockType = String(block, "type");
                if (_claudePartialBlockIndices.Contains(index)) { }
                else if (blockType == "text") Add(result, String(block, "text"));
                else if (blockType == "tool_use")
                {
                    var name = String(block, "name");
                    var input = Property(block, "input");
                    var command = String(input, "command");
                    var usingTool = name is null ? CoreText.Get("ProgressUsingGenericTool") : CoreText.Get("ProgressUsingTool", name);
                    Add(result, Prefix(usingTool, command));
                }
                index++;
            }
            _claudePartialBlockIndices.Clear();
            return result;
        }
        if (type == "user")
        {
            // Tool results are useful progress, while arbitrary user payloads are not.
            var content = Property(Property(root, "message"), "content");
            if (content.ValueKind != JsonValueKind.Array) return [];
            var result = new List<AgentRunProgress>();
            foreach (var block in content.EnumerateArray())
                if (String(block, "type") == "tool_result") Add(result, CoreText.Get("ProgressToolResult", ToolResultText(block)));
            return result;
        }
        if (type == "result") return Split("activity", (Boolean(root, "is_error") ? CoreText.Get("ProgressClaudeError") : CoreText.Get("ProgressFinished")) + Environment.NewLine);
        if (type == "system" && String(root, "subtype") == "init") return Split("activity", CoreText.Get("ProgressClaudeSessionReady") + Environment.NewLine);
        return [];
    }

    private IReadOnlyList<AgentRunProgress> ParseClaudeStreamEvent(JsonElement value)
    {
        var eventType = String(value, "type");
        if (eventType == "message_start")
        {
            _claudePartialBlockIndices.Clear();
            return [];
        }
        if (eventType == "content_block_start")
        {
            if (Property(value, "content_block") is { ValueKind: JsonValueKind.Object } block && String(block, "type") == "tool_use")
            {
                if (Property(value, "index") is { ValueKind: JsonValueKind.Number } toolIndexValue && toolIndexValue.TryGetInt32(out var toolIndex) && toolIndex is >= 0 and < 2048)
                    _claudePartialBlockIndices.Add(toolIndex);
                var toolName = String(block, "name");
                var toolLabel = toolName is null ? CoreText.Get("ProgressUsingGenericTool") : CoreText.Get("ProgressUsingTool", toolName);
                return Split("activity", toolLabel + Environment.NewLine);
            }
            return [];
        }
        if (eventType != "content_block_delta") return [];
        var delta = Property(value, "delta");
        if (String(delta, "type") is not ("text_delta" or "text")) return [];
        var text = String(delta, "text");
        if (string.IsNullOrEmpty(text)) return [];
        if (Property(value, "index") is { ValueKind: JsonValueKind.Number } indexValue && indexValue.TryGetInt32(out var index) && index is >= 0 and < 2048)
            _claudePartialBlockIndices.Add(index);
        return Split("stdout", text);
    }

    private static void Add(List<AgentRunProgress> target, string? text)
    {
        target.AddRange(Split("activity", text is null ? null : text + Environment.NewLine));
    }

    private static string? Prefix(string prefix, string? detail)
    {
        return string.IsNullOrEmpty(detail) ? prefix : $"{prefix}: {detail}";
    }

    private static string ToolResultText(JsonElement block)
    {
        var content = Property(block, "content");
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var result = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (String(part, "type") == "text" && String(part, "text") is { } text) result.Append(text);
        return result.ToString();
    }

    internal static IReadOnlyList<AgentRunProgress> Split(string stream, string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var result = new List<AgentRunProgress>((text.Length + MaxTextCharacters - 1) / MaxTextCharacters);
        for (var offset = 0; offset < text.Length; offset += MaxTextCharacters)
            result.Add(new(stream, text.Substring(offset, Math.Min(MaxTextCharacters, text.Length - offset))));
        return result;
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;

    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    private static bool Boolean(JsonElement value, string name)
        => Property(value, name).ValueKind == JsonValueKind.True;
}
