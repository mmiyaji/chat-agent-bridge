using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationAgentServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-agent-switch-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cab-agent-workspace-" + Guid.NewGuid().ToString("N"));
    private readonly SessionKey _key = new("mattermost", "channel-1", "thread-1", "user-1", new string('a', 64));
    private readonly TestProtector _protector = new();
    private SessionStore Sessions => new(_directory, _protector);
    private AgentConnectionStore Connections => new(_directory);
    private ConversationSummaryStore Summaries => new(_directory, _protector);
    private BridgeSettings Defaults => new()
    {
        WorkspaceRoot = _workspace,
        TrustedWorkspaceRoots = _workspace,
        AgentProvider = "codex",
        AgentExecutable = "codex",
        AgentArguments = "exec --json -",
        AgentMode = "exec",
        AgentModel = "old-model",
        AgentPermissionMode = "read-only",
        ChatApprovalMode = "always",
        ChatResultMode = "manual",
        MattermostToken = "local-secret",
        SlackBotToken = "slack-secret"
    };

    public ConversationAgentServiceTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Switch_usesOnlyRegisteredProfile_andArchivesOldBindingWithoutResumingIt()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        var target = Profile("claude", model: "sonnet", permission: "workspace-write");
        await Connections.SaveVerifiedAsync(target);

        await Service().SwitchAsync(old, Defaults, new("claude", false), Allow);

        var saved = (await Sessions.FindAsync(_key))!;
        Assert.Equal(old.SessionId, saved.SessionId);
        Assert.Equal("claude", saved.NextAgentProvider);
        Assert.Equal(old.SessionId, saved.SessionId);
        Assert.Null(await Sessions.FindExecutionBindingAsync(_key));
        Assert.Equal("claude", (await Sessions.FindAgentSelectionAsync(_key))!.Provider);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "sessions.json")));
        var archive = document.RootElement.GetProperty("ArchivedBindings");
        Assert.Contains(archive.EnumerateArray(), item => item.GetProperty("SessionId").GetString() == old.SessionId);

        var effective = await Sessions.ApplyWorkspaceAsync(_key, Defaults with { WorkspaceRoot = Path.GetTempPath() });
        Assert.Equal(_workspace, effective.WorkspaceRoot);
        Assert.Equal(_workspace, effective.TrustedWorkspaceRoots);
        Assert.Equal("local-secret", effective.MattermostToken);
        Assert.Equal("slack-secret", effective.SlackBotToken);
        Assert.Equal("always", effective.ChatApprovalMode);
        Assert.Equal("manual", effective.ChatResultMode);
        Assert.Equal("claude", effective.AgentProvider);
        Assert.Equal("sonnet", effective.AgentModel);
        Assert.Equal("workspace-write", effective.AgentPermissionMode);
    }

    [Fact]
    public async Task Switch_savesExactScopedSummary_andRunnerStartsFreshClaudeSessionThenClearsItOnCompletion()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude", model: "sonnet"));
        const string request = "Request: inspect the parser";
        const string response = "Response: the old session found an edge case";
        await Summaries.RecordAsync(_key, "codex", old.SessionId, "inspect the parser", "the old session found an edge case");
        var otherUser = _key with { UserId = "other-user" };
        var otherThread = _key with { ThreadId = "different-thread" };
        var otherOrigin = _key with { Origin = new string('b', 64) };
        await Summaries.RecordAsync(otherUser, "codex", old.SessionId, "secret user prompt", "secret user response");
        await Summaries.RecordAsync(otherThread, "codex", old.SessionId, "secret thread prompt", "secret thread response");
        await Summaries.RecordAsync(otherOrigin, "codex", old.SessionId, "secret origin prompt", "secret origin response");

        await Service().SwitchAsync(old, Defaults, new("claude", true), Allow);
        var pending = (await Sessions.FindAgentSelectionAsync(_key))!;
        Assert.True(pending.StartNewSession);
        Assert.True(pending.RequiresApproval);
        Assert.Contains(request, await Sessions.PrepareHandoffPromptAsync(_key, "Do the next task"));
        Assert.Contains(response, await Sessions.PrepareHandoffPromptAsync(_key, "Do the next task"));
        var prepared = await Sessions.PrepareHandoffPromptAsync(_key, "Do the next task");
        Assert.DoesNotContain("secret user", prepared);
        Assert.DoesNotContain("secret thread", prepared);
        Assert.DoesNotContain("secret origin", prepared);
        var protectedStore = await File.ReadAllTextAsync(Path.Combine(_directory, "sessions.json"));
        Assert.DoesNotContain("inspect the parser", protectedStore);
        Assert.DoesNotContain("old session found an edge case", protectedStore);

        var newId = Guid.NewGuid();
        var runner = new RecordingAgentRunner((_, _) => new AgentRunResult(true, 0,
            $$"""{"type":"result","subtype":"success","session_id":"{{newId:D}}","result":"ok"}""", ""));
        var result = await new SessionAgentRunner(Sessions, runner).RunAsync(_key, Defaults, "Do the next task");

        Assert.True(result.Success);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("claude", call.Settings.AgentProvider);
        Assert.Equal("sonnet", call.Settings.AgentModel);
        Assert.Equal("", call.Settings.AgentArguments);
        Assert.Contains(request, call.Prompt);
        Assert.Contains(response, call.Prompt);
        Assert.Equal(1, Count(call.Prompt, "Previous-session reference follows"));
        var fresh = (await Sessions.FindAsync(_key))!;
        Assert.Equal(newId.ToString("D"), fresh.SessionId);
        Assert.Equal("claude-stream-json-v1", fresh.AgentArguments);
        Assert.Null(fresh.NextAgentProvider);
        Assert.True((await Sessions.FindAgentSelectionAsync(_key))!.RequiresApproval);
        Assert.Contains(response, await Sessions.PrepareHandoffPromptAsync(_key, "Another turn before approval"));

        await Sessions.CompleteAgentHandoffAsync(_key, newId.ToString("D"));
        var completed = (await Sessions.FindAgentSelectionAsync(_key))!;
        Assert.False(completed.RequiresApproval);
        Assert.Null(completed.ProtectedHandoff);
        var nextPrompt = await Sessions.PrepareHandoffPromptAsync(_key, "Ordinary follow-up");
        Assert.Equal("Ordinary follow-up", nextPrompt);
    }

    [Fact]
    public async Task FreshSwitch_doesNotIncludeAnySummaryInTargetPrompt()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Summaries.RecordAsync(_key, "codex", old.SessionId, "private prior request", "private prior answer");
        await Service().SwitchAsync(old, Defaults, new("claude", false), Allow);

        var newId = Guid.NewGuid();
        var runner = new RecordingAgentRunner((_, _) => ClaudeResult(newId));
        await new SessionAgentRunner(Sessions, runner).RunAsync(_key, Defaults, "Current request only");

        var prompt = Assert.Single(runner.Calls).Prompt;
        Assert.Equal("Current request only", prompt);
        Assert.DoesNotContain("private prior", prompt);
        Assert.Null((await Sessions.FindAgentSelectionAsync(_key))!.ProtectedHandoff);
    }

    [Fact]
    public async Task FailedTargetStart_retainsOldBindingAndPendingHandoff()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Summaries.RecordAsync(_key, "codex", old.SessionId, "prior request", "prior response");
        await Service().SwitchAsync(old, Defaults, new("claude", true), Allow);
        var pending = await Sessions.FindAsync(_key);

        var runner = new RecordingAgentRunner((_, _) => new AgentRunResult(false, 1, "", "target failed"));
        var result = await new SessionAgentRunner(Sessions, runner).RunAsync(_key, Defaults, "Try target");

        Assert.False(result.Success);
        Assert.Equal(pending, await Sessions.FindAsync(_key));
        Assert.Equal("claude", (await Sessions.FindAsync(_key))!.NextAgentProvider);
        Assert.True((await Sessions.FindAgentSelectionAsync(_key))!.RequiresApproval);
        Assert.Contains("prior response", Assert.Single(runner.Calls).Prompt);
    }

    [Fact]
    public async Task DenialAndSelectionFailures_leaveStateUnchanged()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        var service = Service();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SwitchAsync(old, Defaults,
            new("claude", false), _ => Task.FromResult(false)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchAsync(old, Defaults,
            new("unknown-cli", false), Allow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(old, Defaults,
            new("claude", false, Profile("claude", model: "different-confirmed-model")), Allow));
        var untrusted = Defaults with { TrustedWorkspaceRoots = Path.Combine(_workspace, "elsewhere") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(old, untrusted,
            new("claude", false), Allow));
        AssertUnchanged(old);

        var confirmedProfile = await Connections.FindAsync("claude");
        var confirmationChecks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(old, Defaults,
            new("claude", false, confirmedProfile), async _ =>
            {
                if (Interlocked.Increment(ref confirmationChecks) == 2)
                    await Connections.SaveVerifiedAsync(Profile("claude", model: "changed-during-confirmation"));
                return true;
            }));
        AssertUnchanged(old);

        var foreignKeys = new[]
        {
            _key with { UserId = "other-user" },
            _key with { ThreadId = "other-thread" },
            _key with { Origin = new string('b', 64) }
        };
        foreach (var foreign in foreignKeys)
        {
            var alteredExpected = old with { Key = foreign };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(alteredExpected, Defaults,
                new("claude", false), Allow));
            Assert.Null(await Sessions.FindAsync(foreign));
        }
        AssertUnchanged(old);
    }

    [Fact]
    public async Task UnregisteredProviderCannotBeSelected()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SwitchAsync(old, Defaults,
            new("claude", false), Allow));

        Assert.Equal(old, await Sessions.FindAsync(_key));
        Assert.Null(await Sessions.FindAgentSelectionAsync(_key));
    }

    [Fact]
    public async Task LegacyConversationWithoutAnOwnerCannotBeSwitched()
    {
        var legacy = Binding(_key with { UserId = null, Origin = null });
        await Sessions.SaveAsync(legacy);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().SwitchAsync(legacy, Defaults,
            new("claude", false), Allow));
        Assert.Equal(legacy, await Sessions.FindAsync(legacy.Key));
        Assert.Null(await Sessions.FindAgentSelectionAsync(legacy.Key));
    }

    [Fact]
    public async Task PendingSwitchCanDiscardSummaryBeforeTheFirstTargetRequest()
    {
        var source = Binding(_key);
        await Sessions.SaveAsync(source);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Summaries.RecordAsync(_key, "codex", source.SessionId, "private task", "private result");
        await Service().SwitchAsync(source, Defaults, new("claude", true), Allow);
        var pending = (await Sessions.FindAsync(_key))!;
        Assert.NotNull((await Sessions.FindAgentSelectionAsync(_key))!.ProtectedHandoff);
        await Service().SwitchAsync(pending, Defaults, new("claude", false), Allow);
        Assert.Null((await Sessions.FindAgentSelectionAsync(_key))!.ProtectedHandoff);
        Assert.Equal("new request", await Sessions.PrepareHandoffPromptAsync(_key, "new request"));
        Assert.Null(await Sessions.FindExecutionBindingAsync(_key));
    }

    [Fact]
    public async Task RegisteredCodexAppServerStartsFreshAfterClaudeWithTheSelectedPermissions()
    {
        var source = Binding(_key) with { AgentExecutable = "claude", AgentArguments = "claude-stream-json-v1" };
        await Sessions.SaveAsync(source);
        var target = Profile("codex", "target-model", "workspace-write") with { Mode = "app-server", Arguments = "app-server" };
        await Connections.SaveVerifiedAsync(target);
        await Summaries.RecordAsync(_key, "claude", source.SessionId, "previous task", "previous findings");
        await Service().SwitchAsync(source, Defaults, new("codex", true), Allow);
        var effective = await Sessions.ApplyWorkspaceAsync(_key, Defaults);
        var fake = new RecordingServerRunner();
        var result = await new AppServerSessionRunner(Sessions, fake).RunAsync(_key, effective, "new task");
        Assert.True(result.Success);
        Assert.Null(fake.Options!.SessionId);
        Assert.Contains("previous findings", fake.Prompt);
        Assert.Contains("new task", fake.Prompt);
        Assert.Equal("target-model", fake.Settings!.AgentModel);
        Assert.Equal("workspace-write", fake.Settings.AgentPermissionMode);
        Assert.NotEqual(source.SessionId, (await Sessions.FindAsync(_key))!.SessionId);
        Assert.Equal("app-server", (await Sessions.FindAsync(_key))!.AgentArguments);
    }

    [Fact]
    public async Task JapaneseHandoffIsReadableAndDoesNotExpandIntoUnicodeEscapeTokens()
    {
        var source = Binding(_key);
        await Sessions.SaveAsync(source);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Summaries.RecordAsync(_key, "codex", source.SessionId, "日本語の依頼", "日本語の引き継ぎメモ");
        await Service().SwitchAsync(source, Defaults, new("claude", true), Allow);
        var prompt = await Sessions.PrepareHandoffPromptAsync(_key, "続きをお願いします");
        Assert.Contains("日本語の引き継ぎメモ", prompt);
        Assert.Contains("続きをお願いします", prompt);
        Assert.DoesNotContain(@"\u65E5", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(prompt.Length < 1000);
    }

    [Fact]
    public async Task BusyLeaseStaleBindingAndRevocationDoNotMutateConversation()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        var service = Service();

        using (Sessions.TryBeginConversationOperation(_key))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(old, Defaults,
                new("claude", false), Allow));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(old with { Model = "stale" }, Defaults,
            new("claude", false), Allow));
        var allow = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SwitchAsync(old, Defaults,
            new("claude", false), _ => { var current = allow; allow = false; return Task.FromResult(current); }));
        Assert.Equal(old, await Sessions.FindAsync(_key));
        Assert.Null(await Sessions.FindAgentSelectionAsync(_key));
    }

    [Fact]
    public async Task NotesMustMatchCurrentSessionAndProviderExactly()
    {
        var old = Binding(_key);
        await Sessions.SaveAsync(old);
        await Connections.SaveVerifiedAsync(Profile("claude"));
        await Summaries.RecordAsync(_key, "codex", "different-session", "wrong session", "wrong session answer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SwitchAsync(old, Defaults,
            new("claude", true), Allow));
        Assert.Equal(old, await Sessions.FindAsync(_key));
        Assert.Null(await Sessions.FindAgentSelectionAsync(_key));

        await Summaries.RecordAsync(_key, "claude", old.SessionId, "wrong provider", "wrong provider answer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SwitchAsync(old, Defaults,
            new("claude", true), Allow));
        Assert.Equal(old, await Sessions.FindAsync(_key));
    }

    private ConversationAgentService Service() => new(Sessions, Connections, Summaries);
    private SessionBinding Binding(SessionKey key) => new(key, _workspace, "codex", "exec --json -",
        Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, "old-model", "high", "always", "manual",
        "read-only", new string('a', 64), "Keep this title");
    private static AgentConnectionProfile Profile(string provider, string? model = null, string permission = "read-only")
        => provider == "claude"
            ? new("claude", "claude", "", "exec", model, null, permission, false, "2.1.0", DateTimeOffset.UtcNow)
            : new("codex", "codex", "exec --json -", "exec", model, null, permission, false, "0.1.0", DateTimeOffset.UtcNow);
    private static Task<bool> Allow(CancellationToken _) => Task.FromResult(true);
    private static AgentRunResult ClaudeResult(Guid id) => new(true, 0,
        $$"""{"type":"result","subtype":"success","session_id":"{{id:D}}","result":"ok"}""", "");
    private void AssertUnchanged(SessionBinding old)
    {
        Assert.Equal(old, Sessions.FindAsync(_key).GetAwaiter().GetResult());
        Assert.Null(Sessions.FindAgentSelectionAsync(_key).GetAwaiter().GetResult());
    }
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }

    private sealed class TestProtector : IConversationTextProtector
    {
        public string Protect(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        public string Unprotect(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }

    private sealed class RecordingAgentRunner(Func<BridgeSettings, string, AgentRunResult> run) : IAgentRunner
    {
        public List<(BridgeSettings Settings, string Prompt)> Calls { get; } = [];
        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            Calls.Add((settings, prompt));
            return Task.FromResult(run(settings, prompt));
        }
    }

    private sealed class RecordingServerRunner : IAppServerRunner
    {
        public BridgeSettings? Settings { get; private set; }
        public AppServerRunOptions? Options { get; private set; }
        public string Prompt { get; private set; } = "";
        public Task<AppServerRunResult> RunAsync(BridgeSettings settings, string prompt,
            AppServerRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            Settings = settings; Prompt = prompt; Options = options;
            return Task.FromResult(new AppServerRunResult(new AgentRunResult(true, 0, "done", ""), Guid.NewGuid().ToString("D")));
        }
    }
}
