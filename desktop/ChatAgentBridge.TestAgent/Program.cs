using System.Text.Json;
using System.Text;

// This offline fixture exchanges UTF-8 text over redirected standard streams with the bridge.
// Pin both encodings explicitly so the machine's console code page cannot corrupt prompts or replies.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

// Offline E2E fixture only. No model calls, command execution or networking. The explicitly
// marked attachment fixture may create one of four synthetic files inside its isolated workspace.
if (args is ["--version"])
{
    var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
    var versionError = exeName switch { "cab-probe-runtime-error" => "runtime", "cab-probe-version-error" => "other", _ => null };
    if (versionError == "runtime")
    {
        await Console.Error.WriteLineAsync("You must install or update .NET to run this application.");
        Environment.ExitCode = 72;
        return;
    }
    if (versionError == "other")
    {
        await Console.Error.WriteLineAsync("fixture secret stderr must not be included in probe errors");
        Environment.ExitCode = 73;
        return;
    }
    var fixtureVersion = Environment.GetEnvironmentVariable("CAB_TEST_AGENT_VERSION");
    var version = fixtureVersion ?? (exeName.Equals("qwen", StringComparison.OrdinalIgnoreCase)
        ? "Qwen Code 0.15 (offline fixture)"
        : exeName.Equals("kimi", StringComparison.OrdinalIgnoreCase)
        ? "Kimi Code CLI 2.4.1 (offline fixture)"
        : exeName.Equals("kimi-legacy", StringComparison.OrdinalIgnoreCase)
        ? "Kimi CLI 1.51 (offline legacy fixture)"
        : exeName.Equals("kiro-cli", StringComparison.OrdinalIgnoreCase)
        ? "Kiro CLI 2.33 (offline fixture)"
        : exeName.Equals("codex", StringComparison.OrdinalIgnoreCase)
        ? "codex-cli 1.2.3 (offline fixture)"
        : exeName.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? "Claude Code 1.2.3 (offline fixture)"
            : exeName.Equals("agy", StringComparison.OrdinalIgnoreCase)
                ? "1.2.3"
                : "ChatAgentBridge.TestAgent 1.2.3 (offline fixture)");
    await Console.Out.WriteLineAsync(version);
    return;
}

if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("kimi", StringComparison.OrdinalIgnoreCase) && args is ["--help"])
{
    await Console.Out.WriteLineAsync("Usage: kimi [options]\n--prompt <prompt> --output-format stream-json\nDocumentation: https://moonshotai.github.io/kimi-code/");
    return;
}

if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("kimi-legacy", StringComparison.OrdinalIgnoreCase) && args is ["--help"])
{
    await Console.Out.WriteLineAsync("Usage: kimi --print --output-format stream-json https://moonshotai.github.io/kimi-cli/");
    return;
}

// Antigravity (agy) fixtures. Selected only when this fixture is launched as agy.exe; the real CLI
// lists models as "id<TAB>label" on stdout and writes its progress banner to stderr.
var isAntigravityFixture = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("agy", StringComparison.OrdinalIgnoreCase);
if (isAntigravityFixture && args is ["--help"])
{
    // The real agy prints its usage to stderr and exits 0.
    await Console.Error.WriteLineAsync("Usage of agy.exe:");
    return;
}
if (isAntigravityFixture && args is ["models"])
{
    await Console.Error.WriteLineAsync("Fetching available models...");
    await Console.Out.WriteLineAsync("fixture-agy-fast\tFixture Agy Fast");
    await Console.Out.WriteLineAsync("fixture-agy-deep\tFixture Agy Deep (Thinking)");
    return;
}

// Synthetic structured CLI fixtures. Selected only by the bridge's provider output format;
// these never read credentials, execute tools, or contact a provider.
var structuredFixtureName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").ToLowerInvariant();
if (structuredFixtureName.StartsWith("kimi", StringComparison.Ordinal) && args is ["acp"])
{
    string? sessionId = null;
    var model = "fixture-default";
    var mode = "yolo"; // Deliberately non-default: the bridge must set the configured mode on every session.
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var request = JsonDocument.Parse(line);
        var root = request.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString();
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        object result = new { };
        switch (method)
        {
            case "initialize":
                result = new { protocolVersion = structuredFixtureName.Contains("noprotocol", StringComparison.Ordinal) ? (int?)null : 1,
                    agentInfo = new { name = structuredFixtureName.Contains("badidentity", StringComparison.Ordinal) ? "Other Agent" : "Kimi Code CLI", version = "2.4.1 fixture" },
                    agentCapabilities = new { loadSession = true, sessionCapabilities = new { resume = new { } } } };
                break;
            case "session/new":
                sessionId = "session_" + Guid.NewGuid().ToString("D");
                result = new { sessionId, modes = FixtureModes(structuredFixtureName) };
                break;
            case "session/load":
                sessionId = parameters.GetProperty("sessionId").GetString();
                await WriteAsync(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "load replay must not escape" } } } });
                result = new { modes = FixtureModes(structuredFixtureName) };
                break;
            case "session/set_mode":
            case "session/set_model":
                if (method == "session/set_model") model = parameters.GetProperty("modelId").GetString() ?? model;
                else mode = parameters.GetProperty("modeId").GetString() ?? mode;
                result = new { };
                break;
            case "session/prompt":
                var promptText = parameters.GetProperty("prompt")[0].GetProperty("text").GetString() ?? "";
                if (promptText == "fixture-auth-required")
                {
                    await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "Auth required: 403 subscription; PRIVATE_DIAGNOSTIC_SENTINEL" } });
                    continue;
                }
                var fixtureId = sessionId ?? throw new InvalidOperationException("ACP session was not initialized.");
                var answer = $"kimi-acp; model={model}; mode={mode}; prompt={promptText}";
                if (promptText.Contains("__CAB_SCENARIO_UNKNOWN_NOTIFICATION__", StringComparison.Ordinal))
                    await WriteAsync(new { jsonrpc = "2.0", method = "future/harmless_notification", @params = new { ignored = true } });
                if (promptText.Contains("__CAB_SCENARIO_WRONG_UPDATE__", StringComparison.Ordinal))
                    await WriteAsync(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = "session_" + Guid.NewGuid().ToString("D"), update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "wrong session leak" } } } });
                if (promptText.Contains("__CAB_SCENARIO_WRONG_PERMISSION__", StringComparison.Ordinal))
                {
                    await WriteAsync(new { jsonrpc = "2.0", id, method = "session/request_permission", @params = new { sessionId = "session_" + Guid.NewGuid().ToString("D"),
                        toolCall = new { toolCallId = "tool-foreign", title = "Foreign session request", rawInput = new { command = "fixture" } },
                        options = new[] { new { optionId = "approve_once", name = "Approve once", kind = "allow_once" } } } });
                }
                if (promptText.Contains("__CAB_SCENARIO_PERMISSION__", StringComparison.Ordinal) || promptText == "fixture-permission")
                {
                    var permissionId = promptText.Contains("__CAB_SCENARIO_ID_COLLISION__", StringComparison.Ordinal) ? id : 9001;
                    await WriteAsync(new { jsonrpc = "2.0", id = permissionId, method = "session/request_permission", @params = new { sessionId = fixtureId,
                        toolCall = new { toolCallId = "tool-1", title = "Run fixture tool", rawInput = new { command = "fixture" } },
                        options = new[] { new { optionId = "approve_once", name = "Approve once", kind = "allow_once" }, new { optionId = "reject", name = "Reject", kind = "reject_once" } } } });
                    var permissionLine = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("Missing permission response.");
                    using var permission = JsonDocument.Parse(permissionLine);
                    if (permission.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString() != "selected")
                    {
                        await WriteAsync(new { jsonrpc = "2.0", id, result = new { stopReason = "cancelled" } });
                        break;
                    }
                }
                if (promptText.Contains("__CAB_SCENARIO_MISSING_STOP__", StringComparison.Ordinal))
                {
                    await WriteAsync(new { jsonrpc = "2.0", id, result = new { } });
                    continue;
                }
                if (promptText.Contains("__CAB_SCENARIO_OVERSIZED_TEXT__", StringComparison.Ordinal))
                {
                    await WriteAsync(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = fixtureId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = new string('x', 1_000_100) } } } });
                    await WriteAsync(new { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } });
                    continue;
                }
                if (promptText.Contains("__CAB_SCENARIO_LONG_STDERR__", StringComparison.Ordinal))
                    await Console.Error.WriteLineAsync(new string('e', 1_000_100));
                if (promptText.Contains("__CAB_SCENARIO_CANCEL__", StringComparison.Ordinal))
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                await WriteAsync(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = fixtureId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = answer } } } });
                await WriteAsync(new { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } });
                continue;
            default:
                await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "unknown fixture method" } });
                continue;
        }
        await WriteAsync(new { jsonrpc = "2.0", id, result });
    }
    return;
}
if (structuredFixtureName.StartsWith("kimi", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync("Kimi ACP fixture accepts only the acp subcommand; prompt data belongs on stdio.");
    Environment.ExitCode = 64;
    return;
}
if (structuredFixtureName is "qwen" or "kimi")
{
    var selector = Array.IndexOf(args, structuredFixtureName == "kimi" ? "--session" : "--resume");
    var fixtureId = selector >= 0 ? args[selector + 1] : Guid.NewGuid().ToString("D");
    var modelArg = Array.IndexOf(args, "--model");
    var selectedModel = modelArg >= 0 ? args[modelArg + 1] : "fixture-default";
    var promptValue = args.FirstOrDefault(value => value.StartsWith("--prompt=", StringComparison.Ordinal));
    var inputText = structuredFixtureName == "kimi" && promptValue is not null ? promptValue[9..] : await Console.In.ReadToEndAsync();
    var fixtureText = $"{structuredFixtureName}-e2e; session={fixtureId}; model={selectedModel}; prompt={inputText}";
    if (structuredFixtureName == "qwen")
    {
        await WriteAsync(new { type = "system", subtype = "session_start", session_id = fixtureId, model = selectedModel });
        await WriteAsync(new { type = "assistant", session_id = fixtureId, message = new { content = new[] { new { type = "text", text = fixtureText } } } });
        await WriteAsync(new { type = "result", subtype = "success", session_id = fixtureId, is_error = false, result = fixtureText });
    }
    else
    {
        await WriteAsync(new { role = "assistant", content = new object[] { new { type = "think", think = "private-reasoning" }, new { type = "text", text = fixtureText } } });
        await WriteAsync(new { role = "meta", type = "session.resume_hint", session_id = fixtureId.StartsWith("session_", StringComparison.Ordinal) ? fixtureId : "session_" + fixtureId });
        if (inputText == "fixture-fail") Environment.ExitCode = 1;
    }
    return;
}
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("kiro-cli", StringComparison.OrdinalIgnoreCase))
{
    if (args is ["chat", "--list-models", "--format", "json"])
    {
        await WriteAsync(new { default_model = "fixture-kiro-fast", models = new[] { new { id = "fixture-kiro-fast", displayName = "Fixture Kiro Fast" }, new { id = "fixture-kiro-deep", displayName = "Fixture Kiro Deep" } } });
        return;
    }
    var resume = Array.IndexOf(args, "--resume-id");
    var session = resume >= 0 ? args[resume + 1] : Guid.NewGuid().ToString("D");
    var modelIndex = Array.IndexOf(args, "--model");
    var model = modelIndex >= 0 ? args[modelIndex + 1] : "fixture-kiro-fast";
    var kiroFixturePrompt = await Console.In.ReadToEndAsync();
    var reply = $"kiro-e2e: {(resume >= 0 ? "resumed" : "new")}; model={model}; prompt={kiroFixturePrompt}";
    await WriteAsync(new { type = "runStarted", data = new { sessionId = session } });
    await WriteAsync(new { type = "sessionUpdate", data = new { sessionId = session, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = reply } } } });
    await WriteAsync(new { type = "runFinished", data = new { finalText = reply, stopReason = "end_turn" } });
    await WriteAsync(new { type = "metadata", data = new { meteringUsage = new[] { new { value = 0.1, unit = "credit" } } } });
    return;
}
var structuredFormatIndex = Array.IndexOf(args, "--output-format");
if (!args.Contains("--print") && structuredFormatIndex >= 0 && structuredFormatIndex + 1 < args.Length &&
    args[structuredFormatIndex + 1] is "stream-json" or "json")
{
    var isGemini = args[structuredFormatIndex + 1] == "stream-json" && !isAntigravityFixture;
    var isAntigravity = args[structuredFormatIndex + 1] == "stream-json" && isAntigravityFixture;
    var fixtureIndex = Array.IndexOf(args, "--cab-cli-fixture");
    var fixtureMode = fixtureIndex >= 0 && fixtureIndex + 1 < args.Length ? args[fixtureIndex + 1] : "success";
    var resumeIndex = Array.IndexOf(args, isAntigravity ? "--conversation" : "--resume");
    var fixtureSessionId = resumeIndex >= 0 && resumeIndex + 1 < args.Length ? args[resumeIndex + 1] : Guid.NewGuid().ToString("D");
    var fixturePrompt = await Console.In.ReadToEndAsync();
    CaptureLaunchContract(fixturePrompt, args);
    var fixtureModelIndex = Array.IndexOf(args, "--model");
    var fixtureModel = fixtureModelIndex >= 0 && fixtureModelIndex + 1 < args.Length ? args[fixtureModelIndex + 1] : "fixture-default";
    var fixtureReply = $"{(isAntigravity ? "antigravity" : isGemini ? "gemini" : "copilot")}-e2e: {(resumeIndex >= 0 ? "resumed" : "new")}; model={fixtureModel}; prompt={fixturePrompt}";
    if (fixtureMode == "malformed") await Console.Out.WriteLineAsync("{malformed fixture");
    if (fixtureMode == "oversized") await Console.Out.WriteLineAsync(new string('x', 64 * 1024 + 1));
    if (isAntigravity)
    {
        if (fixtureMode != "missing-id") await WriteAsync(new { @event = "init", conversation_id = fixtureSessionId, init = new { cwd = Environment.CurrentDirectory, permission_mode = "request-review" } });
        if (fixtureMode == "conflicting-id") await WriteAsync(new { @event = "init", conversation_id = Guid.NewGuid().ToString("D") });
        await WriteAsync(new { @event = "step_update", step_update = new { conversation_id = fixtureSessionId, step_index = 0, state = "DONE", step_type = "user_input", text_delta = "hidden-user-payload" } });
        await WriteAsync(new { @event = "step_update", step_update = new { conversation_id = fixtureSessionId, step_index = 1, state = "ACTIVE", step_type = "tool", tool_name = "synthetic",
            tool_info = new { name = "synthetic", parameters = new { command = "hidden-tool-arguments" } } } });
        await WriteAsync(new { @event = "step_update", step_update = new { conversation_id = fixtureSessionId, step_index = 1, state = "DONE", step_type = "tool", tool_name = "synthetic" } });
        await WriteAsync(new { @event = "step_update", step_update = new { conversation_id = fixtureSessionId, step_index = 2, state = "ACTIVE", step_type = "agent_response", text_delta = fixtureReply[..12] } });
        var antigravityUsage = new { input_tokens = 100, output_tokens = 5, thinking_tokens = 2, cache_read_tokens = 75, total_tokens = 105 };
        await WriteAsync(new { @event = "step_update", step_update = new { conversation_id = fixtureSessionId, step_index = 2, state = "DONE", step_type = "agent_response", text_delta = fixtureReply[12..],
            usage = antigravityUsage } });
        // Like the real CLI, the result totals the whole conversation, so a resumed run reports more than its own call.
        var turns = resumeIndex >= 0 ? 2 : 1;
        if (fixtureMode != "missing-result") await WriteAsync(new { @event = "result", result = new { conversation_id = fixtureSessionId,
            status = fixtureMode == "failure" ? "ERROR" : "SUCCESS", response = fixtureReply, duration_seconds = 0.1, num_turns = turns,
            usage = new { input_tokens = 100 * turns, output_tokens = 5 * turns, thinking_tokens = 2 * turns, cache_read_tokens = 75 * turns, total_tokens = 105 * turns } } });
    }
    else if (isGemini)
    {
        if (fixtureMode != "missing-id") await WriteAsync(new { type = "init", session_id = fixtureSessionId, model = fixtureModel });
        if (fixtureMode == "conflicting-id") await WriteAsync(new { type = "init", session_id = Guid.NewGuid().ToString("D"), model = fixtureModel });
        await WriteAsync(new { type = "message", role = "user", content = "hidden-user-payload" });
        await WriteAsync(new { type = "tool_use", tool_name = "synthetic", tool_id = "fixture", parameters = new { command = "hidden-tool-arguments" } });
        await WriteAsync(new { type = "tool_result", tool_id = "fixture", status = "success", output = "hidden-tool-output" });
        if (fixtureMode == "large-stream")
            for (var i = 0; i < 40; i++) await WriteAsync(new { type = "message", role = "user", content = new string('x', 32000) });
        await WriteAsync(new { type = "message", role = "assistant", content = fixtureReply[..12], delta = true });
        await WriteAsync(new { type = "message", role = "assistant", content = fixtureReply[12..], delta = true });
        if (fixtureMode == "failure") await WriteAsync(new { type = "error", severity = "error", message = "hidden-error-payload" });
        if (fixtureMode != "missing-result") await WriteAsync(new { type = "result", status = fixtureMode == "failure" ? "error" : "success",
            stats = new { input_tokens = 100, output_tokens = 5, cached = 75, input = 25, total_tokens = 105 } });
    }
    else
    {
        if (fixtureMode != "missing-id") await WriteAsync(new { type = "session.start", data = new { sessionId = fixtureSessionId, selectedModel = fixtureModel } });
        await WriteAsync(new { type = "user.message", data = new { content = "hidden-user-payload" } });
        await WriteAsync(new { type = "assistant.reasoning", data = new { content = "hidden-thought-payload" } });
        await WriteAsync(new { type = "tool.execution_start", data = new { arguments = new { command = "hidden-tool-arguments" } } });
        await WriteAsync(new { type = "tool.execution_complete", data = new { result = new { content = "hidden-tool-output" } } });
        if (fixtureMode == "large-stream")
            for (var i = 0; i < 40; i++) await WriteAsync(new { type = "user.message", data = new { content = new string('x', 32000) } });
        await WriteAsync(new { type = "assistant.message_delta", data = new { messageId = "fixture-message", deltaContent = fixtureReply[..12] } });
        await WriteAsync(new { type = "assistant.message_delta", data = new { messageId = "fixture-message", deltaContent = fixtureReply[12..] } });
        await WriteAsync(new { type = "assistant.message", data = new { messageId = "fixture-message", content = fixtureReply } });
        if (fixtureMode == "failure") await WriteAsync(new { type = "session.error", data = new { errorType = "fatal", message = "hidden-error-payload" } });
        if (fixtureMode != "missing-result") await WriteAsync(new { type = "result", sessionId = fixtureMode == "missing-id" ? null : fixtureMode == "conflicting-id" ? Guid.NewGuid().ToString("D") : fixtureSessionId,
            exitCode = fixtureMode == "failure" ? 1 : 0, usage = new { premiumRequests = 1, totalApiDurationMs = 10 } });
    }
    return;
}

if (Environment.GetEnvironmentVariable("CAB_CLAUDE_STEERING_FIXTURE") == "1" &&
    args.Contains("--print") && args.Contains("--input-format") && Array.IndexOf(args, "--input-format") + 1 < args.Length &&
    args[Array.IndexOf(args, "--input-format") + 1] == "stream-json")
{
    await ClaudeFollowUpFixture.RunAsync(args);
    return;
}

if (args.Contains("--input-format") && Array.IndexOf(args, "--input-format") + 1 < args.Length
    && args[Array.IndexOf(args, "--input-format") + 1] == "stream-json")
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var requestDocument = JsonDocument.Parse(line);
        var requestRoot = requestDocument.RootElement;
        if (requestRoot.GetProperty("type").GetString() != "control_request"
            || requestRoot.GetProperty("request").GetProperty("subtype").GetString() != "initialize") continue;
        var requestId = requestRoot.GetProperty("request_id").GetString();
        if (Environment.GetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_OVERSIZED") == "1")
        {
            await Console.Out.WriteLineAsync(new string('x', 256 * 1024 + 1));
            await Console.Out.FlushAsync();
        }
        await WriteAsync(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = Environment.GetEnvironmentVariable("CAB_TEST_AGENT_CLAUDE_MISMATCH") == "1" ? "wrong-request-id" : requestId,
                response = new
                {
                    models = new object[]
                    {
                        "malformed-row",
                        new { value = "fixture-claude-small", displayName = "Fixture Claude Small", description = "Quick fixture model", supportsEffort = true, supportedEffortLevels = new[] { "low", "medium" } },
                        new { value = "fixture-claude-large", displayName = "Fixture Claude Large", description = "Long context fixture model", supportsEffort = true, supportedEffortLevels = new[] { "high", "max" } }
                    }
                }
            }
        });
        break;
    }
    return;
}
if (args.Contains("--print"))
{
    if (Environment.GetEnvironmentVariable("CAB_CLAUDE_STEERING_FIXTURE") == "1")
    {
        await ClaudeFollowUpFixture.RunOneShotAsync(args);
        return;
    }

    // Deliberately gated live-output UI fixture. It can run only from an explicitly marked,
    // isolated workspace, emits sanitized provider-shaped progress and stderr before result,
    // and waits for the E2E harness to release completion.
    if (Environment.GetEnvironmentVariable("CAB_LIVE_OUTPUT_FIXTURE") == "1")
    {
        var liveWorkspace = Path.GetFullPath(Environment.CurrentDirectory);
        var liveRoot = Directory.GetParent(liveWorkspace)?.FullName;
        var liveMarker = liveRoot is null ? null : Path.Combine(liveRoot, ".cab-live-output-fixture");
        var releaseGate = liveRoot is null ? null : Path.Combine(liveRoot, ".cab-live-output-release");
        if (liveRoot is null || liveMarker is null || releaseGate is null ||
            !string.Equals(Path.GetFileName(liveWorkspace), "workspace", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(liveWorkspace) || !File.Exists(liveMarker) ||
            !string.Equals(File.ReadAllText(liveMarker).Trim(), "Isolated offline live-output UI test", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Live-output fixture requires its marked isolated workspace.");
            Environment.ExitCode = 2;
            return;
        }

        var fixturePidFile = Environment.GetEnvironmentVariable("CAB_LIVE_OUTPUT_FIXTURE_PID_FILE");
        if (!string.IsNullOrWhiteSpace(fixturePidFile))
            await File.AppendAllTextAsync(fixturePidFile, Environment.ProcessId + Environment.NewLine);

        var livePrompt = await Console.In.ReadToEndAsync();
        var liveRunMatch = System.Text.RegularExpressions.Regex.Match(livePrompt, @"\bLIVE_RUN_(ONE|TWO|CHAT_A|CHAT_B)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!liveRunMatch.Success)
        {
            Console.Error.WriteLine("Live-output fixture requires a whitelisted LIVE_RUN marker in the prompt.");
            Environment.ExitCode = 2;
            return;
        }
        var liveRunMarker = liveRunMatch.Value;
        var liveDelay = int.TryParse(Environment.GetEnvironmentVariable("CAB_LIVE_OUTPUT_FIXTURE_DELAY_MS"), out var configuredDelay)
            ? Math.Clamp(configuredDelay, 0, 30000) : 700;
        await Task.Delay(liveDelay);
        await WriteAsync(new { type = "system", subtype = "init", session_id = "a352fd0c-204e-4d06-8c83-8de46651b3ed", model = "fixture-claude" });
        await WriteAsync(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = $"{liveRunMarker} LIVE_FIXTURE assistant output arrived before the final result." } } } });
        await WriteAsync(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", name = "Read", input = new { command = "LIVE_FIXTURE synthetic tool activity" } } } } });
        await Console.Error.WriteLineAsync("LIVE_FIXTURE stderr arrived before the final result.");
        await Console.Error.FlushAsync();
        await Task.Delay(1200);
        await WriteAsync(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = "LIVE_FIXTURE late output while follow is paused." } } } });

        var waitUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        while (!File.Exists(releaseGate) && DateTimeOffset.UtcNow < waitUntil)
            await Task.Delay(50);
        if (!File.Exists(releaseGate))
        {
            Console.Error.WriteLine("Live-output fixture completion gate timed out.");
            Environment.ExitCode = 3;
            return;
        }
        await WriteAsync(new
        {
            type = "result", subtype = "success", session_id = "a352fd0c-204e-4d06-8c83-8de46651b3ed",
            result = "LIVE_FIXTURE_FINAL fixed final response",
            usage = new { input_tokens = 10, output_tokens = 5 },
            modelUsage = new Dictionary<string, object> { ["fixture-claude"] = new { } }
        });
        return;
    }

    var attachmentWorkspace = Environment.GetEnvironmentVariable("CAB_FILE_ATTACHMENTS_WORKSPACE");
    if (!string.IsNullOrWhiteSpace(attachmentWorkspace))
    {
        var workspace = Path.GetFullPath(attachmentWorkspace);
        var dataDirectory = Directory.GetParent(workspace)?.FullName;
        var marker = dataDirectory is null ? null : Path.Combine(dataDirectory, ".cab-file-attachments-ui");
        var transcript = Environment.GetEnvironmentVariable("CAB_FILE_ATTACHMENTS_TRANSCRIPT");
        if (dataDirectory is null || !string.Equals(Path.GetFileName(workspace), "workspace", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(workspace) || marker is null || !File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker).Trim(), "Isolated offline attachment UI test", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(transcript) ||
            !string.Equals(Path.GetFullPath(transcript), Path.Combine(dataDirectory, "agent-calls.jsonl"), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Attachment fixture environment did not point to its marked isolated test directory.");
            Environment.ExitCode = 2;
            return;
        }

        var attachmentPrompt = await Console.In.ReadToEndAsync();
        var attachmentFileMatch = System.Text.RegularExpressions.Regex.Match(attachmentPrompt, @"CREATE ATTACHMENT (first|second|third|fourth)\.txt", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var attachmentResult = "No fixture file requested.";
        if (attachmentFileMatch.Success)
        {
            var fileName = attachmentFileMatch.Groups[1].Value.ToLowerInvariant() + ".txt";
            var filePath = Path.Combine(workspace, fileName);
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(filePath)), workspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Attachment fixture filename escaped the isolated workspace.");
                Environment.ExitCode = 2;
                return;
            }
            await File.WriteAllTextAsync(filePath, $"Offline fixture file {fileName}{Environment.NewLine}");
            attachmentResult = $"Created {fileName}";
        }

        var attachmentResumeIndex = Array.IndexOf(args, "--resume");
        var attachmentSession = attachmentResumeIndex >= 0 && attachmentResumeIndex + 1 < args.Length
            ? args[attachmentResumeIndex + 1]
            : "6f76a963-bd6e-42dc-9c66-27ca2d8c77f3";
        await File.AppendAllTextAsync(transcript!, JsonSerializer.Serialize(new { args, prompt = attachmentPrompt, result = attachmentResult }) + Environment.NewLine);
        await WriteAsync(new { type = "system", subtype = "init", session_id = attachmentSession, model = "fixture-claude" });
        await WriteAsync(new { type = "assistant", message = new { content = new[] { new { type = "text", text = attachmentResult } } } });
        await WriteAsync(new { type = "result", subtype = "success", session_id = attachmentSession, result = attachmentResult,
            usage = new { input_tokens = 10, output_tokens = 5 }, modelUsage = new Dictionary<string, object> { ["fixture-claude"] = new { } } });
        return;
    }

    var resumeIndex = Array.IndexOf(args, "--resume");
    var session = resumeIndex >= 0 ? args[resumeIndex + 1] : Guid.NewGuid().ToString("D");
    var modelIndex = Array.IndexOf(args, "--model");
    var model = modelIndex >= 0 ? args[modelIndex + 1] : "fixture-claude";
    var text = await Console.In.ReadToEndAsync();
    CaptureLaunchContract(text, args);
    var fixtureCalls = resumeIndex >= 0 ? 2 : 1;
    await WriteAsync(new { type = "system", subtype = "init", session_id = session, model });
    await WriteAsync(new
    {
        type = "result", subtype = "success", session_id = session,
        result = $"claude-e2e: {(resumeIndex >= 0 ? "resumed" : "new")}; prompt={text}",
        usage = new { input_tokens = 10 * fixtureCalls, output_tokens = 5 * fixtureCalls, cache_read_input_tokens = 30 * fixtureCalls, cache_creation_input_tokens = 0 },
        modelUsage = new Dictionary<string, object> { [model] = new { } }
    });
    return;
}
if (args is ["exec", ..])
{
    var resume = Array.IndexOf(args, "resume");
    var id = resume >= 0 ? args.FirstOrDefault(argument => Guid.TryParse(argument, out _)) ?? Guid.NewGuid().ToString("D") : Guid.NewGuid().ToString("D");
    var modelIndex = Array.IndexOf(args, "--model");
    var model = modelIndex >= 0 ? args[modelIndex + 1] : "fixture-codex";
    var input = await Console.In.ReadToEndAsync();
    CaptureLaunchContract(input, args);
    await WriteAsync(new { type = "thread.started", thread_id = id });
    await WriteAsync(new { type = "item.completed", item = new { type = "agent_message", text = $"codex-e2e: {(resume >= 0 ? "resumed" : "new")}; prompt={input}" } });
    await WriteAsync(new { type = "turn.completed", model, usage = new { input_tokens = 50, output_tokens = 20, cached_input_tokens = 10 } });
    return;
}
if (args is not ["app-server", ..])
{
    Console.Write(await Console.In.ReadToEndAsync());
    return;
}

if (Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_FIXTURE") == "1")
{
    await AppServerFollowUpFixture.RunAsync();
    return;
}

var threadId = Guid.NewGuid().ToString("D");
var turnId = "fixture-turn";
var inputPending = false;
var approvalPending = false;
var prompt = "";
var fixtureTurnCount = 0;
var fixtureThreadTurns = new Dictionary<string, int>();
while (await Console.In.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    if (root.TryGetProperty("method", out var method))
    {
        var name = method.GetString();
        var hasId = root.TryGetProperty("id", out var id);
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        switch (name)
        {
            case "initialize":
                await WriteAsync(new { id, result = new { userAgent = "offline-test-agent", platformFamily = "windows", platformOs = "windows" } });
                break;
            case "initialized": break;
            case "model/list":
                if (Environment.GetEnvironmentVariable("CAB_TEST_AGENT_EMPTY_MODEL_CATALOG") == "1")
                {
                    await WriteAsync(new { id, result = new { data = Array.Empty<object>(), nextCursor = (string?)null } });
                    break;
                }
                await WriteAsync(new
                {
                    id,
                    result = new
                    {
                        data = new[]
                        {
                            new { id = "fixture-codex-small", model = "fixture-codex-small", displayName = "Fixture Codex Small", description = "Quick fixture model", isDefault = true, hidden = false, supportedReasoningEfforts = new[] { new { reasoningEffort = "low", description = "Fast" }, new { reasoningEffort = "medium", description = "Balanced" } }, defaultReasoningEffort = "low" },
                            new { id = "fixture-codex-large", model = "fixture-codex-large", displayName = "Fixture Codex Large", description = "Long context fixture model", isDefault = false, hidden = false, supportedReasoningEfforts = new[] { new { reasoningEffort = "high", description = "Deep" }, new { reasoningEffort = "xhigh", description = "Maximum depth" } }, defaultReasoningEffort = "high" }
                        },
                        nextCursor = (string?)null
                    }
                });
                break;
            case "thread/start":
            case "thread/resume":
                if (name == "thread/resume") threadId = parameters.GetProperty("threadId").GetString()!;
                else threadId = Guid.NewGuid().ToString("D");
                await WriteAsync(new { id, result = new { thread = new { id = threadId }, cwd = parameters.GetProperty("cwd").GetString() } });
                break;
            case "turn/start":
                turnId = "fixture-turn-" + (++fixtureTurnCount);
                fixtureThreadTurns[threadId] = fixtureThreadTurns.GetValueOrDefault(threadId) + 1;
                prompt = parameters.GetProperty("input")[0].GetProperty("text").GetString() ?? "";
                await WriteAsync(new { id, result = new { turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>() } } });
                await WriteAsync(new { method = "turn/started", @params = new { threadId, turn = new { id = turnId, status = "inProgress" } } });
                if (Environment.GetEnvironmentVariable("CAB_LIVE_OUTPUT_FIXTURE") == "1")
                {
                    var liveWorkspace = Path.GetFullPath(Environment.CurrentDirectory);
                    var liveRoot = Directory.GetParent(liveWorkspace)?.FullName;
                    var liveMarker = liveRoot is null ? null : Path.Combine(liveRoot, ".cab-live-output-fixture");
                    var releaseGate = liveRoot is null ? null : Path.Combine(liveRoot, ".cab-live-output-release");
                    if (liveRoot is null || liveMarker is null || releaseGate is null ||
                        !string.Equals(Path.GetFileName(liveWorkspace), "workspace", StringComparison.OrdinalIgnoreCase) ||
                        !Directory.Exists(liveWorkspace) || !File.Exists(liveMarker) ||
                        !string.Equals(File.ReadAllText(liveMarker).Trim(), "Isolated offline live-output UI test", StringComparison.Ordinal))
                    {
                        await Console.Error.WriteLineAsync("Live-output app-server fixture requires its marked isolated workspace.");
                        Environment.ExitCode = 2;
                        return;
                    }
                    var liveRunMatch = System.Text.RegularExpressions.Regex.Match(prompt, @"\bLIVE_RUN_(ONE|TWO|CHAT_A|CHAT_B)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                    if (!liveRunMatch.Success)
                    {
                        await Console.Error.WriteLineAsync("Live-output app-server fixture requires a whitelisted LIVE_RUN marker in the prompt.");
                        Environment.ExitCode = 2;
                        return;
                    }
                    var liveRunMarker = liveRunMatch.Value;
                    await WriteAsync(new { method = "item/started", @params = new { threadId, turnId, item = new { id = "fixture-live-tool", type = "commandExecution", command = "LIVE_FIXTURE synthetic app-server tool activity" } } });
                    await WriteAsync(new { method = "item/completed", @params = new { threadId, turnId, item = new { id = "fixture-live-message", type = "agentMessage", text = $"{liveRunMarker} LIVE_FIXTURE app-server assistant output arrived before the final result." } } });
                    await Console.Error.WriteLineAsync("LIVE_FIXTURE app-server stderr arrived before the final result.");
                    await Console.Error.FlushAsync();
                    await Task.Delay(1200);
                    await WriteAsync(new { method = "item/completed", @params = new { threadId, turnId, item = new { id = "fixture-live-late-message", type = "agentMessage", text = "LIVE_FIXTURE late app-server output while follow is paused." } } });
                    var releaseDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
                    while (!File.Exists(releaseGate) && DateTimeOffset.UtcNow < releaseDeadline)
                        await Task.Delay(50);
                    if (!File.Exists(releaseGate))
                    {
                        await Console.Error.WriteLineAsync("Live-output app-server fixture completion gate timed out.");
                        Environment.ExitCode = 3;
                        return;
                    }
                    await CompleteAsync("completed", "LIVE_FIXTURE_FINAL fixed app-server response");
                    break;
                }
                approvalPending = true;
                await WriteAsync(new
                {
                    id = "fixture-approval", method = "item/commandExecution/requestApproval",
                    @params = new { threadId, turnId, itemId = "fixture-command", command = "offline fixture: echo only", cwd = parameters.GetProperty("cwd").GetString(), reason = "E2E test: no command will actually run" }
                });
                break;
            case "turn/interrupt":
                if (hasId) await WriteAsync(new { id, result = new { } });
                await CompleteAsync("interrupted", "fixture interrupted");
                break;
            default:
                if (hasId) await WriteAsync(new { id, error = new { code = -32601, message = "Unsupported fixture method" } });
                break;
        }
    }
    else if (root.TryGetProperty("id", out var responseId))
    {
        var responseKey = responseId.ValueKind == JsonValueKind.String ? responseId.GetString() : "";
        if (responseKey == "fixture-approval" && approvalPending)
        {
            approvalPending = false;
            var accepted = root.TryGetProperty("result", out var result) && result.TryGetProperty("decision", out var decision) && decision.GetString() == "accept";
            if (!accepted) { await CompleteAsync("failed", "fixture approval declined"); continue; }
            inputPending = true;
            await WriteAsync(new
            {
                id = "fixture-input", method = "item/tool/requestUserInput",
                @params = new
                {
                    threadId, turnId, itemId = "fixture-question",
                    questions = new[]
                    {
                        new
                        {
                            id = "choice", header = "Offline E2E", question = "Choose a fixture answer (no model request)", isOther = false, isSecret = false,
                            options = new[] { new { label = "alpha", description = "First fixture option" }, new { label = "beta", description = "Second fixture option" } }
                        }
                    }
                }
            });
        }
        else if (responseKey == "fixture-input" && inputPending)
        {
            inputPending = false;
            if (!root.TryGetProperty("result", out var result)) { await CompleteAsync("failed", "fixture input cancelled"); continue; }
            var answer = result.GetProperty("answers").GetProperty("choice").GetProperty("answers")[0].GetString();
            await CompleteAsync("completed", $"app-server-e2e: approved; answer={answer}; prompt={prompt}");
        }
    }
}

async Task CompleteAsync(string status, string text)
{
    var item = new { id = "fixture-response", type = "agentMessage", text };
    var threadTurns = fixtureThreadTurns.GetValueOrDefault(threadId);
    await WriteAsync(new
    {
        method = "thread/tokenUsage/updated",
        @params = new
        {
            threadId, turnId,
            tokenUsage = new
            {
                total = new { inputTokens = 100 * threadTurns, outputTokens = 5 * threadTurns, cachedInputTokens = 75 * threadTurns, reasoningOutputTokens = 0, totalTokens = 105 * threadTurns },
                last = new { inputTokens = 100, outputTokens = 5, cachedInputTokens = 75, reasoningOutputTokens = 0, totalTokens = 105 },
                modelContextWindow = 10000
            }
        }
    });
    await WriteAsync(new { method = "item/completed", @params = new { threadId, turnId, item } });
    await WriteAsync(new { method = "turn/completed", @params = new { threadId, turn = new { id = turnId, status, items = new[] { item } } } });
}

// Isolated runner contract probe: never reads credentials or writes outside its working folder.
static void CaptureLaunchContract(string prompt, string[] arguments)
{
    if (prompt != "fixture-launch-contract") return;
    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "launch-contract.json"), JsonSerializer.Serialize(new
    {
        workspace = Environment.CurrentDirectory,
        arguments,
        geminiTrust = Environment.GetEnvironmentVariable("GEMINI_CLI_TRUST_WORKSPACE")
    }));
}

static async Task WriteAsync(object value)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
    await Console.Out.FlushAsync();
}

static object FixtureModes(string fixtureName)
{
    var modes = fixtureName.Contains("no-yolo", StringComparison.Ordinal)
        ? new object[] { new { id = "default", name = "Default" } }
        : new object[] { new { id = "default", name = "Default" }, new { id = "yolo", name = "YOLO" } };
    return new { availableModes = modes };
}
