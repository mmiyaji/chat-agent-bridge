using System.Collections.Concurrent;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class SessionTests
{
    private static readonly BridgeSettings Settings = new()
    {
        WorkspaceRoot = Path.GetTempPath(), AgentExecutable = "codex", AgentArguments = "exec --json -"
    };

    [Fact]
    public async Task SessionRunner_PersistsThenResumesAcrossInstancesWithStableArguments()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var fake = new FakeAgentRunner((settings, _) => Task.FromResult(Success(settings.AgentArguments.Contains("resume", StringComparison.Ordinal) ? id : id)));
            var first = new SessionAgentRunner(new SessionStore(directory), fake);
            var newTurn = await first.RunAsync(key, Settings, "first prompt");
            Assert.True(newTurn.Success);
            Assert.Equal("exec --json -", fake.Calls[0].Settings.AgentArguments);

            var restarted = new SessionAgentRunner(new SessionStore(directory), fake);
            var resumed = await restarted.RunAsync(key, Settings, "follow up");
            Assert.True(resumed.Success);
            Assert.Equal($"exec resume --json {id:D} -", fake.Calls[1].Settings.AgentArguments);
            Assert.Equal("follow up", fake.Calls[1].Prompt);
            Assert.Equal(id.ToString("D"), (await new SessionStore(directory).FindAsync(key))!.SessionId);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ScopedSessionRunner_ResumesAcrossInstancesForSameUserAndThread()
    {
        var directory = NewDirectory();
        try
        {
            var key = ChatSessionScope.Create("mattermost", "channel-1", "thread-1", "user-1", "https://chat.example/base");
            var id = Guid.NewGuid();
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(id)));
            Assert.True((await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(key, Settings, "first")).Success);
            Assert.True((await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(key, Settings, "again")).Success);
            Assert.Equal($"exec resume --json {id:D} -", fake.Calls[1].Settings.AgentArguments);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ScopedSessions_IsolateUsersAndMattermostServers()
    {
        var directory = NewDirectory();
        try
        {
            var store = new SessionStore(directory);
            var first = ChatSessionScope.Create("mattermost", "channel-1", "thread-1", "user-1", "https://chat.example/base");
            var otherUser = ChatSessionScope.Create("mattermost", "channel-1", "thread-1", "user-2", "https://chat.example/base");
            var otherServer = ChatSessionScope.Create("mattermost", "channel-1", "thread-1", "user-1", "https://chat.example/other");
            await store.SaveAsync(Binding(first, Guid.NewGuid()));
            await store.SaveAsync(Binding(otherUser, Guid.NewGuid()));
            await store.SaveAsync(Binding(otherServer, Guid.NewGuid()));
            Assert.Equal(3, (await store.ListAsync()).Count);
            Assert.NotEqual((await store.FindAsync(first))!.SessionId, (await store.FindAsync(otherUser))!.SessionId);
            Assert.NotEqual((await store.FindAsync(first))!.SessionId, (await store.FindAsync(otherServer))!.SessionId);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ScopedSessionRunner_DoesNotResumeLegacyUnownedBinding()
    {
        var directory = NewDirectory();
        try
        {
            var legacyKey = Key();
            var key = ChatSessionScope.Create("mattermost", "channel-1", "thread-1", "user-1", "https://chat.example");
            var oldId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            await new SessionStore(directory).SaveAsync(Binding(legacyKey, oldId));
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(newId)));
            var result = await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(key, Settings, "new owned session");
            Assert.True(result.Success);
            Assert.Equal("exec --json -", fake.Calls.Single().Settings.AgentArguments);
            Assert.Equal(oldId.ToString("D"), (await new SessionStore(directory).FindAsync(legacyKey))!.SessionId);
            Assert.Equal(newId.ToString("D"), (await new SessionStore(directory).FindAsync(key))!.SessionId);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ScopedSessionKey_RejectsMissingOrMalformedUserAndServerIdentity()
    {
        Assert.Throws<ArgumentException>(() => ChatSessionScope.Create("slack", "channel", "thread", " "));
        Assert.Throws<ArgumentException>(() => ChatSessionScope.Create("mattermost", "channel", "thread", "user", "not a URL"));
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.FindAsync(
            new SessionKey("slack", "channel", "thread", "user", "not-slack")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.FindAsync(
            new SessionKey("slack", "channel", "thread", "bad\nuser", "slack")));
    }

    [Fact]
    public async Task SessionRunner_PreservesSessionStartedByFailedTurn()
    {
        var directory = NewDirectory();
        try
        {
            var id = Guid.NewGuid();
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(new AgentRunResult(false, 9,
                ThreadStarted(id), "failed")));
            var result = await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(Key(), Settings, "prompt");
            Assert.False(result.Success);
            Assert.Equal(id.ToString("D"), (await new SessionStore(directory).FindAsync(Key()))!.SessionId);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_FailsClosedOnWorkspaceOrCustomArgumentConflict()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(id)));
            var runner = new SessionAgentRunner(new SessionStore(directory), fake);
            Assert.True((await runner.RunAsync(key, Settings, "first")).Success);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(key,
                Settings with { WorkspaceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "different-workspace")) }, "second"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(key,
                Settings with { AgentArguments = "exec --full-auto -" }, "third"));
            Assert.Single(fake.Calls);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_RejectsChangedResumeIdAndRetainsBinding()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var changed = Guid.NewGuid();
            var calls = 0;
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(++calls == 1 ? Success(id) : Success(changed)));
            var runner = new SessionAgentRunner(new SessionStore(directory), fake);
            Assert.True((await runner.RunAsync(key, Settings, "first")).Success);
            var result = await runner.RunAsync(key, Settings, "resume");
            Assert.False(result.Success);
            Assert.Contains("different thread id", result.Error);
            Assert.Equal(id.ToString("D"), (await new SessionStore(directory).FindAsync(key))!.SessionId);
            Assert.Equal($"exec resume --json {id:D} -", fake.Calls[1].Settings.AgentArguments);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_CustomArgumentsAreStatelessUntilAStoredBindingExists()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(Guid.NewGuid())));
            var runner = new SessionAgentRunner(new SessionStore(directory), fake);
            var result = await runner.RunAsync(key, Settings with { AgentArguments = "exec --json --sandbox read-only -" }, "prompt");
            Assert.True(result.Success);
            Assert.Empty(await new SessionStore(directory).ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_GenericExecutableWithDefaultLookingArgumentsStaysStateless()
    {
        var directory = NewDirectory();
        try
        {
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(Guid.NewGuid())));
            var settings = Settings with { AgentExecutable = "claude" };
            var result = await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(Key(), settings, "prompt");
            Assert.True(result.Success);
            Assert.Equal("exec --json -", fake.Calls.Single().Settings.AgentArguments);
            Assert.Empty(await new SessionStore(directory).ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_MissingOrInvalidThreadIdFailsClosed()
    {
        var directory = NewDirectory();
        try
        {
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(new AgentRunResult(true, 0,
                "{\"type\":\"thread.started\",\"thread_id\":\"invalid\"}", "")));
            var result = await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(Key(), Settings, "prompt");
            Assert.False(result.Success);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("valid thread.started", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await new SessionStore(directory).ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionRunner_TurnFailedIsFailureEvenWhenProcessExitCodeIsZero()
    {
        var directory = NewDirectory();
        try
        {
            var id = Guid.NewGuid();
            var fake = new FakeAgentRunner((_, _) => Task.FromResult(new AgentRunResult(true, 0,
                ThreadStarted(id) + "\n{\"type\":\"turn.failed\"}", "")));
            var result = await new SessionAgentRunner(new SessionStore(directory), fake).RunAsync(Key(), Settings, "prompt");
            Assert.False(result.Success);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("turn.failed", result.Error);
            Assert.Equal(id.ToString("D"), (await new SessionStore(directory).FindAsync(Key()))!.SessionId);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionAgentRunner_SerializesSameKeyAcrossInstances()
    {
        var directory = NewDirectory();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var key = Key();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new ConcurrentQueue<string>();
            var fake = new FakeAgentRunner(async (settings, prompt) =>
            {
                calls.Enqueue(prompt);
                if (prompt == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    return Success(Guid.Parse("8a7e7c20-6820-4ee1-9f64-593e8e07102e"));
                }
                return Success(Guid.Parse("8a7e7c20-6820-4ee1-9f64-593e8e07102e"));
            });
            var one = new SessionAgentRunner(new SessionStore(directory), fake);
            var two = new SessionAgentRunner(new SessionStore(directory), fake);
            var firstTask = one.RunAsync(key, Settings, "first");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var secondTask = two.RunAsync(key, Settings, "second");
            Assert.Equal(new[] { "first" }, calls.ToArray());
            releaseFirst.SetResult();
            Assert.All(await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.True(result.Success, result.Error));
            Assert.Equal(new[] { "first", "second" }, calls.ToArray());
            Assert.Contains("resume", fake.Calls[1].Settings.AgentArguments);
        }
        finally
        {
            releaseFirst.TrySetResult();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SessionStore_DetachWaitsForActiveTurnAndCannotBeOverwrittenByItsSave()
    {
        var directory = NewDirectory();
        var releaseResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var resumedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = new FakeAgentRunner(async (_, _) =>
            {
                resumedStarted.SetResult();
                await releaseResume.Task;
                return Success(id);
            });
            var store = new SessionStore(directory);
            await store.SaveAsync(Binding(key, id));
            var run = new SessionAgentRunner(store, fake).RunAsync(key, Settings, "resumed");
            await resumedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var detach = new SessionStore(directory).RemoveAsync(key);
            Assert.False(detach.IsCompleted);
            releaseResume.SetResult();
            Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))).Success);
            await detach.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(await new SessionStore(directory).FindAsync(key));
        }
        finally
        {
            releaseResume.TrySetResult();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SessionStore_SeparatesProvidersAndRejectsCorruptionAndTraversal()
    {
        var directory = NewDirectory();
        try
        {
            var store = new SessionStore(directory);
            var first = Key("slack");
            var second = Key("mattermost");
            await store.SaveAsync(Binding(first, Guid.NewGuid()));
            await new SessionStore(directory).SaveAsync(Binding(second, Guid.NewGuid()));
            Assert.Equal(2, (await store.ListAsync()).Count);
            Assert.NotNull(await store.FindAsync(first));
            await Assert.ThrowsAsync<ArgumentException>(() => store.FindAsync(Key(thread: "..\\escape")));

            await File.WriteAllTextAsync(Path.Combine(directory, "sessions.json"), "{");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
            await File.WriteAllTextAsync(Path.Combine(directory, "sessions.json"), "{\"Bindings\":[]}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionStore_ModelOverrideUpdatesAtomicallyAndRunnerUsesItUntilCleared()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var store = new SessionStore(directory);
            await store.SaveAsync(Binding(key, id));
            await store.UpdateModelAsync(key, "session-model");
            Assert.Equal("session-model", (await store.FindAsync(key))!.Model);

            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(id)));
            var runner = new SessionAgentRunner(store, fake);
            Assert.True((await runner.RunAsync(key, Settings with { AgentModel = "global-model" }, "override turn")).Success);
            Assert.Equal("session-model", fake.Calls.Single().Settings.AgentModel);

            await store.UpdateModelAsync(key, null);
            Assert.Null((await store.FindAsync(key))!.Model);
            Assert.True((await runner.RunAsync(key, Settings with { AgentModel = "global-model" }, "default turn")).Success);
            Assert.Equal("global-model", fake.Calls.Last().Settings.AgentModel);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.UpdateModelAsync(Key(thread: "missing"), "x"));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SessionStore_ModelAndEffortOverrideAreAtomicAndClearingFollowsCompatibleGlobalDefault()
    {
        var directory = NewDirectory();
        try
        {
            var key = Key();
            var id = Guid.NewGuid();
            var store = new SessionStore(directory);
            await store.SaveAsync(Binding(key, id));
            await store.UpdateModelAndEffortAsync(key, "alternate", "future-level");
            var binding = (await store.FindAsync(key))!;
            Assert.Equal("alternate", binding.Model);
            Assert.Equal("future-level", binding.ReasoningEffort);

            var fake = new FakeAgentRunner((_, _) => Task.FromResult(Success(id)));
            var runner = new SessionAgentRunner(store, fake);
            await runner.RunAsync(key, Settings with { AgentModel = "global", AgentReasoningEffort = "high" }, "override");
            Assert.Equal("future-level", fake.Calls.Single().Settings.AgentReasoningEffort);

            await store.UpdateModelAndEffortAsync(key, "alternate", null);
            await runner.RunAsync(key, Settings with { AgentModel = "global", AgentReasoningEffort = "high" }, "different model");
            Assert.Null(fake.Calls.Last().Settings.AgentReasoningEffort);

            await store.UpdateModelAndEffortAsync(key, null, null);
            await runner.RunAsync(key, Settings with { AgentModel = "global", AgentReasoningEffort = "high" }, "reset");
            Assert.Null((await store.FindAsync(key))!.ReasoningEffort);
            Assert.Equal("high", fake.Calls.Last().Settings.AgentReasoningEffort);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void CodexOutputParser_ExtractsSessionAndFinalAgentMessage()
    {
        var id = Guid.NewGuid();
        var output = "noise\n" + ThreadStarted(id) + "\n" +
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"done\"}}\n" +
            "{\"type\":\"turn.failed\"}";
        var parsed = CodexOutputParser.Parse(output);
        Assert.Equal(id.ToString("D"), parsed.SessionId);
        Assert.Equal("done", parsed.FinalMessage);
        Assert.True(parsed.TurnFailed);
        Assert.True(parsed.HasEvents);
        Assert.Null(CodexOutputParser.Parse("{\"type\":\"thread.started\",\"thread_id\":\"not-a-guid\"}").SessionId);
    }

    [Fact]
    public void CodexOutputParser_DistinguishesProtocolEventsFromOtherOutput()
    {
        var unknown = CodexOutputParser.Parse("noise\n{malformed\n{\"type\":\"custom.event\",\"text\":\"kept by caller\"}\n42");
        Assert.False(unknown.HasEvents);
        Assert.Null(unknown.FinalMessage);

        var toolOnly = CodexOutputParser.Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}");
        Assert.True(toolOnly.HasEvents);
        Assert.Null(toolOnly.FinalMessage);
    }

    private static AgentRunResult Success(Guid id) => new(true, 0,
        ThreadStarted(id) + "\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"done\"}}", "");

    private static string ThreadStarted(Guid id) => $"{{\"type\":\"thread.started\",\"thread_id\":\"{id:D}\"}}";
    private static SessionKey Key(string provider = "slack", string channel = "channel-1", string thread = "thread-1") => new(provider, channel, thread);
    private static SessionBinding Binding(SessionKey key, Guid id) => new(key, Path.GetFullPath(Path.GetTempPath()), "codex", "exec --json -", id.ToString("D"), DateTimeOffset.UtcNow);
    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }

    private sealed class FakeAgentRunner(Func<BridgeSettings, string, Task<AgentRunResult>> run) : IAgentRunner
    {
        public ConcurrentQueue<(BridgeSettings Settings, string Prompt)> CallQueue { get; } = new();
        public (BridgeSettings Settings, string Prompt)[] Calls => CallQueue.ToArray();

        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallQueue.Enqueue((settings, prompt));
            return run(settings, prompt);
        }
    }
}
