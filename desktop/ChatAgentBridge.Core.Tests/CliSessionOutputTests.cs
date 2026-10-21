using System.Collections.Concurrent;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class CliSessionOutputTests
{
    private const string Id = "a352fd0c-204e-4d06-8c83-8de46651b3ed";
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Gemini_UsesOnlyAssistantTextAndReportedTokenCounters()
    {
        var output = string.Join('\n',
            Json(new { type = "init", session_id = Id, model = "gemini-fixture" }),
            Json(new { type = "message", role = "user", content = "hidden user" }),
            Json(new { type = "tool_use", parameters = new { prompt = "hidden arguments" } }),
            Json(new { type = "tool_result", output = "hidden result" }),
            Json(new { type = "message", role = "assistant", content = "日本語 ", delta = true }),
            Json(new { type = "message", role = "assistant", content = "😀", delta = true }),
            Json(new { type = "result", status = "success", stats = new { input_tokens = 100, output_tokens = 5, cached = 75 } }));
        var result = CliSessionOutputParser.Parse(output, AgentProfile.Gemini);
        Assert.Equal(Id, result.SessionId);
        Assert.True(result.HasResult);
        Assert.False(result.TurnFailed);
        Assert.Equal("日本語 😀", result.FinalMessage);
        Assert.Equal(100, result.Usage!.TotalInputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(75, result.Usage.CachedInputTokens);
        Assert.Equal(75d, result.Usage.CacheHitPercent);
        Assert.Equal("gemini-fixture", result.Usage.Model);
        Assert.Equal("turn", result.Usage.Scope);
        Assert.Null(result.Usage.CacheWriteInputTokens);
    }

    [Fact]
    public void Gemini_SnapshotsReplaceDeltasAndUnknownCountersRemainUnknown()
    {
        var output = string.Join('\n',
            Json(new { type = "init", session_id = Id, model = "gemini-fixture" }),
            Json(new { type = "message", role = "assistant", content = "old", delta = true }),
            Json(new { type = "message", role = "assistant", content = "final snapshot" }),
            Json(new { type = "result", status = "success", stats = new { output_tokens = 3 } }));
        var result = CliSessionOutputParser.Parse(output, AgentProfile.Gemini);
        Assert.Equal("final snapshot", result.FinalMessage);
        Assert.Null(result.Usage!.InputTokens);
        Assert.Null(result.Usage.CachedInputTokens);
        Assert.Null(result.Usage.CacheHitPercent);
    }

    [Theory]
    [InlineData(false, "actual-model")]
    [InlineData(true, null)]
    public void Gemini_AggregateUsageDoesNotMisattributeMultipleModels(bool multiple, string? expectedModel)
    {
        var models = new Dictionary<string, object> { ["actual-model"] = new { } };
        if (multiple) models["fallback-model"] = new { };
        var output = string.Join('\n',
            Json(new { type = "init", session_id = Id, model = "initial-alias" }),
            Json(new { type = "result", status = "success", stats = new { input_tokens = 100, output_tokens = 5, models } }));
        var result = CliSessionOutputParser.Parse(output, AgentProfile.Gemini);
        Assert.False(result.TurnFailed);
        Assert.Equal(expectedModel, result.Usage!.Model);
        Assert.Equal(100, result.Usage.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
    }

    [Fact]
    public void Copilot_UsesActualCliResultWrapperAndSkipsThoughtsToolsAndSubagents()
    {
        // Schema: @github/copilot@1.0.23 app.js EEo (direct runtime events), wEo (terminal wrapper).
        // https://registry.npmjs.org/@github/copilot/-/copilot-1.0.23.tgz
        var output = string.Join('\n',
            Json(new { type = "session.start", data = new { sessionId = Id } }),
            Json(new { type = "user.message", data = new { content = "hidden prompt" } }),
            Json(new { type = "assistant.reasoning", data = new { content = "hidden thought" } }),
            Json(new { type = "assistant.message_delta", data = new { messageId = "m", deltaContent = "hello " } }),
            Json(new { type = "assistant.message_delta", data = new { messageId = "m", deltaContent = "world" } }),
            Json(new { type = "assistant.message", data = new { messageId = "m", content = "hello world" } }),
            Json(new { type = "assistant.message", parentToolCallId = "child", data = new { content = "hidden subagent" } }),
            Json(new { type = "tool.execution_complete", data = new { result = new { content = "hidden result" } } }),
            Json(new { type = "result", sessionId = Id, exitCode = 0, usage = new { premiumRequests = 4, sessionDurationMs = 300 } }));
        var result = CliSessionOutputParser.Parse(output, AgentProfile.GitHubCopilot);
        Assert.Equal("hello world", result.FinalMessage);
        Assert.Equal(Id, result.SessionId);
        Assert.True(result.HasResult);
        Assert.False(result.TurnFailed);
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public void MissingUuidOrTerminalAndMalformedMetadataFailClosed(string provider)
    {
        var init = provider == AgentProfile.Gemini
            ? Json(new { type = "init", session_id = Id })
            : Json(new { type = "session.start", data = new { sessionId = Id } });
        var result = provider == AgentProfile.Gemini
            ? Json(new { type = "result", status = "success" })
            : Json(new { type = "result", sessionId = Id, exitCode = 0 });
        var missingId = provider == AgentProfile.Gemini ? result : Json(new { type = "result", exitCode = 0 });
        Assert.True(CliSessionOutputParser.Parse(init, provider).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(missingId, provider).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(init + "\n{bad json\n" + result, provider).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(init + "\n" + new string('x', 65537) + "\n" + result, provider).TurnFailed);
        Assert.True(CliSessionOutputParser.Parse(init + "\n" + result + "\n" + result, provider).TurnFailed);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public void ConflictingIdsAndStructuredFailureOverrideSuccessfulProcessExit(string provider)
    {
        var output = provider == AgentProfile.Gemini
            ? $"{{\"type\":\"init\",\"session_id\":\"{Id}\"}}\n{{\"type\":\"init\",\"session_id\":\"{Guid.NewGuid():D}\"}}\n{{\"type\":\"result\",\"status\":\"success\"}}"
            : $"{{\"type\":\"session.start\",\"data\":{{\"sessionId\":\"{Id}\"}}}}\n{{\"type\":\"result\",\"sessionId\":\"{Guid.NewGuid():D}\",\"exitCode\":0}}";
        var parsed = CliSessionOutputParser.Parse(output, provider);
        Assert.True(parsed.TurnFailed);
        Assert.Null(parsed.SessionId);
        var failure = provider == AgentProfile.Gemini
            ? $"{{\"type\":\"init\",\"session_id\":\"{Id}\"}}\n{{\"type\":\"result\",\"status\":\"error\"}}"
            : $"{{\"type\":\"result\",\"sessionId\":\"{Id}\",\"exitCode\":1}}";
        Assert.True(CliSessionOutputParser.Parse(failure, provider).TurnFailed);
    }

    [Fact]
    public void Copilot_IdleIsNotTheTerminalCliResultAndInvalidExitCodeFailsClosed()
    {
        foreach (var end in new[] { "{\"type\":\"session.idle\",\"data\":{}}", $"{{\"type\":\"result\",\"sessionId\":\"{Id}\",\"exitCode\":\"0\"}}" })
        {
            var parsed = CliSessionOutputParser.Parse($"{{\"type\":\"session.start\",\"data\":{{\"sessionId\":\"{Id}\"}}}}\n" + end, AgentProfile.GitHubCopilot);
            Assert.False(parsed.HasResult);
            Assert.True(parsed.TurnFailed);
        }
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task OfflineFixturePreservesConversationAndStreamsOnlySafeText(string provider)
    {
        using var workspace = new TempDirectory();
        var settings = FixtureSettings(workspace.Path, provider);
        var store = new SessionStore(Path.Combine(workspace.Path, "store"));
        var runner = new SessionAgentRunner(store, new AgentRunner());
        var key = new SessionKey("slack", "offline", "structured-fixture", "user", "slack");
        var updates = new ConcurrentQueue<AgentRunProgress>();
        var first = await runner.RunAsync(key, settings, "日本語 😀🚀", progress: new InlineProgress(updates.Enqueue));
        Assert.True(first.Success, first.Error);
        Assert.Contains("日本語 😀🚀", first.Output);
        Assert.Contains("model=fixture-model", first.Output);
        Assert.Contains("new", first.Output);
        Assert.Equal(first.Output, first.CliSession!.FinalMessage);
        var continued = await runner.RunAsync(key, settings, "follow-up");
        Assert.True(continued.Success, continued.Error);
        Assert.Contains("resumed", continued.Output);
        Assert.Equal(first.CliSession.SessionId, continued.CliSession!.SessionId);
        Assert.DoesNotContain("hidden-", string.Join('\n', updates.Select(value => value.Text)));
        Assert.DoesNotContain("hidden-", first.Output);
        var streamed = string.Concat(updates.Where(value => value.Stream == "stdout").Select(value => value.Text));
        Assert.Equal(first.Output, streamed);
        if (provider == AgentProfile.Gemini) Assert.Equal(75d, first.Usage!.CacheHitPercent);
        else Assert.Null(first.Usage);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini, "missing-id")]
    [InlineData(AgentProfile.Gemini, "missing-result")]
    [InlineData(AgentProfile.Gemini, "conflicting-id")]
    [InlineData(AgentProfile.Gemini, "malformed")]
    [InlineData(AgentProfile.Gemini, "failure")]
    [InlineData(AgentProfile.Gemini, "oversized")]
    [InlineData(AgentProfile.GitHubCopilot, "missing-id")]
    [InlineData(AgentProfile.GitHubCopilot, "missing-result")]
    [InlineData(AgentProfile.GitHubCopilot, "conflicting-id")]
    [InlineData(AgentProfile.GitHubCopilot, "malformed")]
    [InlineData(AgentProfile.GitHubCopilot, "failure")]
    [InlineData(AgentProfile.GitHubCopilot, "oversized")]
    public async Task OfflineFixtureRejectsBrokenOrFailedProtocol(string provider, string mode)
    {
        using var workspace = new TempDirectory();
        var result = await new AgentRunner().RunAsync(FixtureSettings(workspace.Path, provider) with
        { AgentArguments = "--cab-cli-fixture " + mode }, "offline");
        Assert.False(result.Success);
        Assert.True(result.CliSession!.TurnFailed);
        Assert.DoesNotContain("hidden-", result.Output);
        Assert.DoesNotContain("hidden-", result.Error);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task OfflineFixtureReadsTerminalMetadataBeyondRawCaptureLimit(string provider)
    {
        using var workspace = new TempDirectory();
        var result = await new AgentRunner().RunAsync(FixtureSettings(workspace.Path, provider) with
        { AgentArguments = "--cab-cli-fixture large-stream" }, "offline");
        Assert.True(result.Success, result.Error);
        Assert.True(Guid.TryParse(result.CliSession!.SessionId, out _));
        Assert.Contains("prompt=offline", result.Output);
        Assert.True(result.Output.Length < 1000);
    }

    private static BridgeSettings FixtureSettings(string workspace, string provider)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0", "ChatAgentBridge.TestAgent.exe"));
        Assert.True(File.Exists(fixture), $"Offline fixture was not built: {fixture}");
        return new BridgeSettings { WorkspaceRoot = workspace, AgentProvider = provider, AgentExecutable = fixture, AgentModel = "fixture-model" };
    }
    private sealed class InlineProgress(Action<AgentRunProgress> report) : IProgress<AgentRunProgress>
    { public void Report(AgentRunProgress value) => report(value); }
    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-protocol-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
