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
    var fixtureVersion = Environment.GetEnvironmentVariable("CAB_TEST_AGENT_VERSION");
    var version = fixtureVersion ?? (exeName.Equals("codex", StringComparison.OrdinalIgnoreCase)
        ? "codex-cli 1.2.3 (offline fixture)"
        : exeName.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? "Claude Code 1.2.3 (offline fixture)"
            : "ChatAgentBridge.TestAgent 1.2.3 (offline fixture)");
    await Console.Out.WriteLineAsync(version);
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

static async Task WriteAsync(object value)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
    await Console.Out.FlushAsync();
}
