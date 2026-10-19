using System.Collections.Concurrent;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ClaudeUsageTests
{
    [Fact]
    public void CodexOutputParser_ExposesProviderReportedUsage()
    {
        var parsed = CodexOutputParser.Parse("{\"type\":\"turn.completed\",\"model\":\"gpt-5\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"cached_input_tokens\":40}}");
        Assert.Equal(100, parsed.Usage!.TotalInputTokens);
        Assert.Equal(40, parsed.Usage.CachedInputTokens);
        Assert.Equal(20, parsed.Usage.OutputTokens);
        Assert.Equal(40d, parsed.Usage.CacheHitPercent);
        Assert.Equal("gpt-5", parsed.Usage.Model);
        Assert.Equal("codex.exec", parsed.Usage.Source);
    }

    [Fact]
    public void ClaudeOutputParser_ExtractsSessionFinalUsageAndFailureWithoutInventingUnknownCounters()
    {
        var id = Guid.NewGuid();
        var parsed = ClaudeOutputParser.Parse(string.Join('\n',
            $"{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{id:D}\",\"model\":\"claude-sonnet-current\"}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"answer\",\"usage\":{\"input_tokens\":17,\"output_tokens\":8,\"cache_read_input_tokens\":5,\"cache_creation_input_tokens\":2},\"modelUsage\":{\"claude-sonnet-current\":{}}}"));

        Assert.True(parsed.HasEvents);
        Assert.False(parsed.TurnFailed);
        Assert.Equal(id.ToString("D"), parsed.SessionId);
        Assert.Equal("answer", parsed.FinalMessage);
        Assert.Equal(17, parsed.Usage!.InputTokens);
        Assert.Equal(8, parsed.Usage.OutputTokens);
        Assert.Equal(5, parsed.Usage.CachedInputTokens);
        Assert.Equal(2, parsed.Usage.CacheWriteInputTokens);
        Assert.False(parsed.Usage.InputIncludesCache);
        Assert.Equal(24, parsed.Usage.TotalInputTokens);
        Assert.Equal("claude-sonnet-current", parsed.Usage.Model);
        Assert.Equal("turn", parsed.Usage.Scope);
        Assert.True(parsed.HasResult);

        var incomplete = ClaudeOutputParser.Parse("{\"type\":\"result\",\"result\":\"x\",\"usage\":{\"input_tokens\":10}}");
        Assert.Null(incomplete.Usage);
        Assert.False(incomplete.HasResult);

        var multiModel = ClaudeOutputParser.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"model\":\"main-model\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0},\"modelUsage\":{\"main-model\":{},\"helper-model\":{}}}");
        Assert.Null(multiModel.Usage!.Model);
    }

    [Fact]
    public void AgentProfile_UsesClaudeStreamJsonAndExplicitResumeOnly()
    {
        var id = Guid.NewGuid();
        var settings = new BridgeSettings { AgentProvider = AgentProfile.Claude, AgentModel = "claude-sonnet-current" };
        var fresh = AgentProfile.BuildInvocation(settings);
        Assert.Equal("claude", fresh.Executable);
        Assert.Equal(new[] { "--print", "--output-format", "stream-json", "--verbose", "--model", "claude-sonnet-current" }, fresh.Arguments);
        Assert.DoesNotContain("--continue", fresh.Arguments);

        var resumed = AgentProfile.BuildInvocation(settings with { AgentArguments = $"--resume {id:D}" });
        Assert.Contains("--resume", resumed.Arguments);
        Assert.Contains(id.ToString("D"), resumed.Arguments);
        Assert.DoesNotContain("--continue", resumed.Arguments);
    }

    [Fact]
    public async Task SessionAgentRunner_PersistsAndResumesClaudeSessionWithExplicitId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid();
            var key = new SessionKey("mattermost", "channel", "thread");
            var settings = new BridgeSettings
            {
                WorkspaceRoot = directory,
                AgentProvider = AgentProfile.Claude,
                AgentModel = "claude-sonnet-current"
            };
            var fake = new FakeRunner((_, _) => new AgentRunResult(true, 0, "", "")
            {
                Claude = new ClaudeOutput(id.ToString("D"), "reply", false, true) { HasResult = true }
            });
            var store = new SessionStore(directory);
            var runner = new SessionAgentRunner(store, fake);

            Assert.True((await runner.RunAsync(key, settings, "first")).Success);
            var binding = await store.FindAsync(key);
            Assert.Equal(id.ToString("D"), binding!.SessionId);
            Assert.Equal("claude-stream-json-v1", binding.AgentArguments);
            Assert.True((await runner.RunAsync(key, settings, "followup")).Success);
            Assert.Equal($"--resume {id:D}", fake.Calls[1].Settings.AgentArguments);
            Assert.Equal("followup", fake.Calls[1].Prompt);
            Assert.Equal("claude-sonnet-current", fake.Calls[1].Settings.AgentModel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AgentRunner_ClaudeFixtureCompletesAndResumesAcrossRealProcesses()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0", "ChatAgentBridge.TestAgent.exe"));
        Assert.True(File.Exists(fixture), $"Offline fixture executable was not built: {fixture}");
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var key = new SessionKey("claude", "offline", "two-turn-thread");
            var settings = new BridgeSettings
            {
                WorkspaceRoot = directory,
                AgentProvider = AgentProfile.Claude,
                AgentExecutable = fixture,
                AgentModel = "fixture-claude-model"
            };
            var store = new SessionStore(directory);
            var runner = new SessionAgentRunner(store, new AgentRunner());
            const string japanesePrompt = "日本語で応答してください 😀🚀";

            var echo = await new AgentRunner().RunAsync(settings with
            {
                AgentProvider = AgentProfile.Custom,
                AgentModel = null,
                AgentArguments = ""
            }, japanesePrompt);
            Assert.True(echo.Success, echo.Error);
            Assert.Equal(japanesePrompt, echo.Output);

            var first = await runner.RunAsync(key, settings, japanesePrompt);
            Assert.True(first.Success, first.Error);
            Assert.Equal(japanesePrompt, first.Claude!.FinalMessage!.Split("prompt=", StringSplitOptions.None).Last());
            Assert.True(first.Claude.HasResult);
            var savedId = (await store.FindAsync(key))!.SessionId;

            var resumed = await runner.RunAsync(key, settings, "second offline prompt");
            Assert.True(resumed.Success, resumed.Error);
            Assert.Equal(savedId, resumed.Claude!.SessionId);
            Assert.Contains("claude-e2e: resumed", resumed.Claude.FinalMessage);
            Assert.Contains("prompt=second offline prompt", resumed.Claude.FinalMessage);
            Assert.Equal("turn", resumed.Usage!.Scope);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeRunner(Func<BridgeSettings, string, AgentRunResult> run) : IAgentRunner
    {
        private readonly ConcurrentQueue<(BridgeSettings Settings, string Prompt)> _calls = new();
        public (BridgeSettings Settings, string Prompt)[] Calls => _calls.ToArray();

        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Enqueue((settings, prompt));
            return Task.FromResult(run(settings, prompt));
        }
    }
}
