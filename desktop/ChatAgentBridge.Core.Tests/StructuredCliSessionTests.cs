using ChatAgentBridge.Core.Sessions;
using System.Text.Json;

namespace ChatAgentBridge.Core.Tests;

public sealed class StructuredCliSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-cli-sessions-" + Guid.NewGuid().ToString("N"));
    private static readonly SessionKey Key = new("slack", "channel", "thread", "user", "slack");
    private SessionStore Store => new(_directory);
    private static BridgeSettings Settings(string provider) => new()
    {
        AgentProvider = provider, AgentExecutable = "node", AgentArguments = "\"C:\\CLI Tools\\entry.mjs\" --debug",
        WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()), AgentModel = "global-model"
    };

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task New_and_resumed_sessions_keep_launch_arguments_and_use_only_saved_uuid(string provider)
    {
        var id = Guid.NewGuid();
        var fake = new RecordingRunner(_ => Success(provider, id));
        var runner = new SessionAgentRunner(Store, fake);
        var settings = Settings(provider);
        Assert.True((await runner.RunAsync(Key, settings, "first")).Success);
        var saved = (await Store.FindAsync(Key))!;
        Assert.Equal(provider, ConversationAgentService.ProviderOf(saved));
        Assert.Equal(id.ToString("D"), saved.SessionId);
        Assert.DoesNotContain("--resume", AgentProfile.BuildInvocation(fake.Calls[0]).Arguments);
        Assert.True((await runner.RunAsync(Key, settings, "second")).Success);
        Assert.Equal(settings.AgentArguments, fake.Calls[1].AgentArguments);
        var args = AgentProfile.BuildInvocation(fake.Calls[1]).Arguments.ToArray();
        Assert.Contains("C:\\CLI Tools\\entry.mjs", args);
        Assert.Contains("--debug", args);
        Assert.Equal(id.ToString("D"), args[Array.IndexOf(args, "--resume") + 1]);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Different_scope_never_resumes_other_users_session(string provider)
    {
        var fake = new RecordingRunner(_ => Success(provider, Guid.NewGuid()));
        var runner = new SessionAgentRunner(Store, fake);
        foreach (var key in new[] { Key, Key with { UserId = "other" }, Key with { ChannelId = "other" },
                     Key with { ThreadId = "other" }, new SessionKey("mattermost", "channel", "thread", "user", new string('a', 64)),
                     new SessionKey("mattermost", "channel", "thread", "user", new string('b', 64)) })
            Assert.True((await runner.RunAsync(key, Settings(provider), "first")).Success);
        Assert.Equal(6, (await Store.ListAsync()).Count);
        Assert.All(fake.Calls, settings => Assert.DoesNotContain("--resume", AgentProfile.BuildInvocation(settings).Arguments));
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Invalid_or_incomplete_new_turn_cannot_create_a_resumable_binding(string provider)
    {
        foreach (var output in new[] { "", "not-json", MissingTerminal(provider, Guid.NewGuid()), SuccessOutput(provider, "invalid"), FailedOutput(provider, Guid.NewGuid()) })
        {
            var fake = new RecordingRunner(_ => new(true, 0, output, ""));
            var result = await new SessionAgentRunner(Store, fake).RunAsync(Key, Settings(provider), "first");
            Assert.False(result.Success);
            Assert.Null(await Store.FindAsync(Key));
        }
        var failed = new RecordingRunner(_ => Success(provider, Guid.NewGuid()) with { Success = false, ExitCode = 1 });
        Assert.False((await new SessionAgentRunner(Store, failed).RunAsync(Key, Settings(provider), "failed")).Success);
        Assert.Null(await Store.FindAsync(Key));
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Resume_failure_or_changed_id_keeps_original_binding_and_approval(string provider)
    {
        var id = Guid.NewGuid();
        var output = Success(provider, id);
        var fake = new RecordingRunner(_ => output);
        var runner = new SessionAgentRunner(Store, fake);
        await runner.RunAsync(Key, Settings(provider), "first");
        await Store.UpdateExecutionPolicyAsync(Key, "first", "manual", null);
        await Store.SetFirstApprovalFingerprintAsync(Key, new string('a', 64));
        var original = await Store.FindAsync(Key);
        foreach (var result in new[] { Success(provider, Guid.NewGuid()),
                     new AgentRunResult(true, 0, MissingTerminal(provider, id), ""),
                     new AgentRunResult(true, 0, FailedOutput(provider, id), ""),
                     Success(provider, id) with { Success = false, ExitCode = 1 } })
        {
            output = result;
            Assert.False((await runner.RunAsync(Key, Settings(provider), "resume")).Success);
            Assert.Equal(original, await Store.FindAsync(Key));
        }
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Changed_launch_arguments_executable_or_workspace_cannot_resume(string provider)
    {
        var fake = new RecordingRunner(_ => Success(provider, Guid.NewGuid()));
        var runner = new SessionAgentRunner(Store, fake);
        var settings = Settings(provider);
        await runner.RunAsync(Key, settings, "first");
        foreach (var changed in new[] { settings with { AgentArguments = "other.mjs --debug" },
                     settings with { AgentArguments = settings.AgentArguments + " --other" },
                     settings with { AgentExecutable = "another-node" },
                     settings with { AgentProvider = provider == AgentProfile.Gemini ? AgentProfile.GitHubCopilot : AgentProfile.Gemini },
                     settings with { WorkspaceRoot = Path.Combine(Path.GetTempPath(), "other"), TrustedWorkspaceRoots = Path.GetTempPath() } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Key, changed, "resume"));
        Assert.Single(fake.Calls);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Raw_session_selectors_never_run_even_without_a_binding(string provider)
    {
        var fake = new RecordingRunner(_ => Success(provider, Guid.NewGuid()));
        var runner = new SessionAgentRunner(Store, fake);
        foreach (var arguments in new[] { "--resume latest", "--resume=" + Guid.NewGuid(), "--continue", "-r latest", "--session-id " + Guid.NewGuid(), "--new-session-id=" + Guid.NewGuid() })
        {
            var result = await runner.RunAsync(Key, Settings(provider) with { AgentArguments = arguments }, "first");
            Assert.False(result.Success);
        }
        Assert.Empty(fake.Calls);
        Assert.Empty(await Store.ListAsync());
    }

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task Per_session_model_override_continues_uuid_and_clearing_returns_to_default(string provider)
    {
        var id = Guid.NewGuid();
        var fake = new RecordingRunner(_ => Success(provider, id));
        var runner = new SessionAgentRunner(Store, fake);
        await runner.RunAsync(Key, Settings(provider), "first");
        await Store.SetFirstApprovalFingerprintAsync(Key, new string('a', 64));
        await Store.UpdateModelAsync(Key, "session-model");
        Assert.Null((await Store.FindAsync(Key))!.FirstApprovalFingerprint);
        await runner.RunAsync(Key, Settings(provider), "override");
        Assert.Equal("session-model", AgentProfile.BuildInvocation(fake.Calls.Last()).Model);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.UpdateModelAndEffortAsync(Key, "session-model", "high"));
        await Store.UpdateModelAsync(Key, null);
        await runner.RunAsync(Key, Settings(provider), "default");
        Assert.Equal("global-model", AgentProfile.BuildInvocation(fake.Calls.Last()).Model);
        Assert.Equal(id.ToString("D"), (await Store.FindAsync(Key))!.SessionId);
    }

    private static AgentRunResult Success(string provider, Guid id) => new(true, 0, SuccessOutput(provider, id.ToString("D")), "");
    private static string SuccessOutput(string provider, string id) => provider == AgentProfile.Gemini
        ? $$"""{"type":"init","session_id":"{{id}}","model":"fixture"}""" + "\n" +
          """{"type":"message","role":"assistant","content":"reply","delta":true}""" + "\n" +
          """{"type":"result","status":"success"}"""
        : """{"type":"assistant.message","data":{"messageId":"msg","content":"reply"}}""" + "\n" +
          JsonSerializer.Serialize(new { type = "result", sessionId = id, exitCode = 0, usage = new { premiumRequests = 1 } });
    private static string MissingTerminal(string provider, Guid id) => provider == AgentProfile.Gemini
        ? $$"""{"type":"init","session_id":"{{id:D}}","model":"fixture"}"""
        : JsonSerializer.Serialize(new { type = "session.start", data = new { sessionId = id.ToString("D") } });
    private static string FailedOutput(string provider, Guid id) => SuccessOutput(provider, id.ToString("D"))
        .Replace("\"status\":\"success\"", "\"status\":\"error\"", StringComparison.Ordinal)
        .Replace("\"exitCode\":0", "\"exitCode\":1", StringComparison.Ordinal);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class RecordingRunner(Func<BridgeSettings, AgentRunResult> run) : IAgentRunner
    {
        public List<BridgeSettings> Calls { get; } = [];
        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            Calls.Add(settings);
            return Task.FromResult(run(settings));
        }
    }
}
