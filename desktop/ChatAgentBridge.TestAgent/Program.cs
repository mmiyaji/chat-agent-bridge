using System.Text.Json;

// Offline E2E fixture only. No model calls, command execution, file changes or networking.
if (args.Contains("--print"))
{
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
