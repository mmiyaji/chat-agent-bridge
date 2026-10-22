using System.Text.Json;
using System.Text;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.ClaudeCode;

namespace ChatAgentBridge.Core.Tests;

public sealed class ClaudeStreamingTests
{
    [Fact]
    public async Task FollowUpIsAcceptedOnlyAfterItsReplayAcknowledgement()
    {
        var channel = new FakeInputChannel();
        string? acknowledgedId = null;
        var turn = new ClaudeActiveAgentTurn(channel, "65f4e497-8b20-4f08-a7a4-3fafcb431521", _ => { }, _ => { },
            (id, _) => { acknowledgedId = id; return Task.FromResult(true); }, () => false);

        var result = await turn.AddInputAsync("continue with the next step");

        Assert.Equal(AgentTurnInputResult.Accepted, result);
        using var message = JsonDocument.Parse(Assert.Single(channel.Lines));
        var root = message.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        Assert.Equal("65f4e497-8b20-4f08-a7a4-3fafcb431521", root.GetProperty("session_id").GetString());
        Assert.Equal("continue with the next step", root.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(root.GetProperty("uuid").GetString(), acknowledgedId);
    }

    [Fact]
    public async Task CancellationAfterWriteIsReportedAsUnknownDelivery()
    {
        var channel = new FakeInputChannel();
        var turn = new ClaudeActiveAgentTurn(channel, Guid.NewGuid().ToString("D"), _ => { }, _ => { },
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return false; }, () => false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var result = await turn.AddInputAsync("one message", cancellation.Token);

        Assert.Equal(AgentTurnInputResult.DeliveryUnknown, result);
        Assert.Single(channel.Lines);
    }

    [Fact]
    public async Task SubmitReturnsBeforeDelayedReplayAndConfirmsWhenReplayArrives()
    {
        var channel = new FakeInputChannel();
        var acknowledgements = new AckController();
        var turn = CreateTurn(channel, acknowledgements);

        var submission = await turn.SubmitInputAsync("delayed replay");

        Assert.NotNull(submission);
        Assert.False(submission.Confirmation.IsCompleted);
        Assert.Single(channel.Lines);
        acknowledgements.Complete(Assert.Single(acknowledgements.PendingIds), true);
        Assert.Equal(AgentTurnInputResult.Accepted, await submission.Confirmation);
    }

    [Fact]
    public async Task ReplayConfirmationCanArriveAfterTurnDeactivation()
    {
        var channel = new FakeInputChannel();
        var acknowledgements = new AckController();
        var turn = CreateTurn(channel, acknowledgements);

        var submission = await turn.SubmitInputAsync("already submitted");
        turn.Deactivate();
        acknowledgements.Complete(Assert.Single(acknowledgements.PendingIds), true);

        Assert.Equal(AgentTurnInputResult.Accepted, await submission!.Confirmation);
        var rejected = await turn.SubmitInputAsync("after terminal");
        Assert.Null(rejected);
        Assert.Single(channel.Lines);
    }

    [Fact]
    public async Task EndOfStreamCompletesPendingReplayAsUnknown()
    {
        var session = Guid.NewGuid().ToString("D");
        var reader = Reader(session);
        const string id = "dff35df5-c918-4fc5-a81b-2ea5dc5df57f";
        reader.RegisterReplay(id);
        var pending = reader.WaitForReplayAsync(id, CancellationToken.None);

        await reader.ReadTask;

        Assert.False(await pending);
    }

    [Fact]
    public async Task ReplayRegisteredAfterEndOfStreamCompletesAsUnknown()
    {
        var reader = Reader(Guid.NewGuid().ToString("D"));
        await reader.ReadTask;

        const string id = "8e2ee8f4-dcd1-44cf-978a-15f7c3d15fa8";
        reader.RegisterReplay(id);

        Assert.False(await reader.WaitForReplayAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task MultipleSubmissionsSerializeWritesWithoutWaitingForEachReplay()
    {
        var channel = new FakeInputChannel();
        var acknowledgements = new AckController();
        var turn = CreateTurn(channel, acknowledgements);

        var first = await turn.SubmitInputAsync("first queued message");
        var second = await turn.SubmitInputAsync("second queued message");

        Assert.Equal(2, channel.Lines.Count);
        Assert.Equal("first queued message", MessageText(channel.Lines[0]));
        Assert.Equal("second queued message", MessageText(channel.Lines[1]));
        Assert.False(first!.Confirmation.IsCompleted);
        Assert.False(second!.Confirmation.IsCompleted);
        acknowledgements.Complete(acknowledgements.PendingIds[1], true);
        acknowledgements.Complete(acknowledgements.PendingIds[0], true);
        Assert.Equal(AgentTurnInputResult.Accepted, await first.Confirmation);
        Assert.Equal(AgentTurnInputResult.Accepted, await second.Confirmation);
    }

    [Fact]
    public async Task CancellationAfterSubmissionCompletesConfirmationAsUnknown()
    {
        var channel = new FakeInputChannel();
        var acknowledgements = new AckController();
        var turn = CreateTurn(channel, acknowledgements);
        using var cancellation = new CancellationTokenSource();

        var submission = await turn.SubmitInputAsync("write succeeded", cancellation.Token);
        cancellation.Cancel();

        Assert.Equal(AgentTurnInputResult.DeliveryUnknown, await submission!.Confirmation);
        Assert.Single(channel.Lines);
    }

    [Fact]
    public async Task RejectedBeforeWriteReturnsNoSubmission()
    {
        var channel = new FakeInputChannel { RejectWrites = true };
        var acknowledgements = new AckController();
        var turn = CreateTurn(channel, acknowledgements);

        var submission = await turn.SubmitInputAsync("closed input");

        Assert.Null(submission);
        Assert.Empty(channel.Lines);
        Assert.Empty(acknowledgements.PendingIds);
    }

    [Fact]
    public async Task DeactivatedTurnRejectsInputWithoutWriting()
    {
        var channel = new FakeInputChannel();
        var turn = new ClaudeActiveAgentTurn(channel, Guid.NewGuid().ToString("D"), _ => { }, _ => { },
            (_, _) => Task.FromResult(true), () => false);
        turn.Deactivate();

        var result = await turn.AddInputAsync("too late");

        Assert.Equal(AgentTurnInputResult.NotAccepted, result);
        Assert.Empty(channel.Lines);
    }

    [Fact]
    public async Task TerminalUsageIsSummedAndModelIsReadFromModelUsage()
    {
        var session = Guid.NewGuid().ToString("D");
        var reader = Reader(session,
            new { type = "system", subtype = "init", session_id = session },
            Terminal(session, "first result", 10, 3, "claude-sonnet-4"),
            Terminal(session, "follow-up result", 4, 2, "claude-sonnet-4"));

        await reader.ReadTask;
        var result = reader.Snapshot();

        Assert.True(result.Claude!.HasResult);
        Assert.Equal("first result" + Environment.NewLine + Environment.NewLine + "follow-up result", result.Claude.FinalMessage);
        Assert.Equal(14, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal("claude-sonnet-4", result.Usage.Model);
    }

    [Fact]
    public async Task MissingOrForeignTerminalSessionIdentityFailsClosed()
    {
        var expected = Guid.NewGuid().ToString("D");
        var missing = Reader(expected, new { type = "result", subtype = "success", result = "missing id" });
        await missing.ReadTask;
        Assert.True(missing.Snapshot().Claude!.TurnFailed);
        Assert.Null(missing.Snapshot().Claude!.SessionId);

        var foreign = Reader(expected, new { type = "result", subtype = "success", session_id = Guid.NewGuid().ToString("D"), result = "foreign id" });
        await foreign.ReadTask;
        Assert.True(foreign.Snapshot().Claude!.TurnFailed);
        Assert.False(foreign.Snapshot().Claude!.HasResult);
    }

    [Fact]
    public async Task ErrorEventCannotBeOverriddenByLaterSuccessResult()
    {
        var session = Guid.NewGuid().ToString("D");
        var reader = Reader(session, new { type = "error", session_id = session },
            Terminal(session, "result after error", 10, 3, "claude-sonnet-4"));

        await reader.ReadTask;

        Assert.True(reader.Snapshot().Claude!.TurnFailed);
    }

    [Fact]
    public async Task OutputTruncationPreservesValidTerminalResult()
    {
        var session = Guid.NewGuid().ToString("D");
        var reader = Reader(session, new { type = "result", subtype = "success", session_id = session, result = new string('x', 1_000_100) });

        await reader.ReadTask;
        var result = reader.Snapshot();

        Assert.True(result.HasOutputTruncated);
        Assert.True(result.Claude!.HasResult);
        Assert.False(result.Claude.TurnFailed);
        Assert.Equal(1_000_000, result.Output.Length);
    }

    [Fact]
    public async Task UnknownReplayUuidCannotAcknowledgeRegisteredInput()
    {
        var session = Guid.NewGuid().ToString("D");
        const string expectedUuid = "dff35df5-c918-4fc5-a81b-2ea5dc5df57f";
        var reader = Reader(session, new { type = "user", session_id = session, uuid = Guid.NewGuid().ToString("D"), isReplay = true });
        reader.RegisterReplay(expectedUuid);

        await reader.ReadTask;
        var accepted = await reader.WaitForReplayAsync(expectedUuid, CancellationToken.None);

        Assert.False(accepted);
    }

    private static ClaudeStreamingRunner.ProtocolReader Reader(string sessionId, params object[] events)
    {
        var contents = string.Join("\n", events.Select(item => JsonSerializer.Serialize(item))) + "\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        return new ClaudeStreamingRunner.ProtocolReader(new StreamReader(stream, Encoding.UTF8), new FakeInputChannel(), null, sessionId);
    }

    [Theory]
    [InlineData("can_use_tool")]
    [InlineData("unknown_control")]
    public async Task ControlRequestsCannotGrantAdditionalPermissions(string subtype)
    {
        var session = Guid.NewGuid().ToString("D");
        var channel = new FakeInputChannel();
        var contents = JsonSerializer.Serialize(new
        {
            type = "control_request", request_id = "control-test",
            request = new { subtype, tool_name = "Bash", input = new { command = "untrusted command" } }
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents + "\n"));
        using var stdout = new StreamReader(stream, Encoding.UTF8);
        var reader = new ClaudeStreamingRunner.ProtocolReader(stdout, channel, null, session);

        await reader.ReadTask;

        using var message = JsonDocument.Parse(Assert.Single(channel.Lines));
        var response = message.RootElement.GetProperty("response");
        Assert.Equal("control-test", response.GetProperty("request_id").GetString());
        if (subtype == "can_use_tool")
        {
            Assert.Equal("success", response.GetProperty("subtype").GetString());
            Assert.Equal("deny", response.GetProperty("response").GetProperty("behavior").GetString());
        }
        else
        {
            Assert.Equal("error", response.GetProperty("subtype").GetString());
            Assert.Equal(JsonValueKind.String, response.GetProperty("error").ValueKind);
            Assert.True(reader.Snapshot().Claude!.TurnFailed);
        }
    }

    private static object Terminal(string sessionId, string text, long input, long output, string model)
        => new
        {
            type = "result", subtype = "success", session_id = sessionId, result = text,
            usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = 2, cache_creation_input_tokens = 1 },
            modelUsage = new Dictionary<string, object> { [model] = new { } }
        };

    private static ClaudeActiveAgentTurn CreateTurn(FakeInputChannel channel, AckController acknowledgements)
        => new(channel, Guid.NewGuid().ToString("D"), acknowledgements.Register, acknowledgements.Remove,
            acknowledgements.WaitAsync, () => false);

    private static string MessageText(string line)
        => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

    private sealed class AckController
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _acks = new(StringComparer.Ordinal);
        public List<string> PendingIds => _acks.Keys.ToList();
        public void Register(string id) => _acks.Add(id, new(TaskCreationOptions.RunContinuationsAsynchronously));
        public void Remove(string id) => _acks.Remove(id);
        public Task<bool> WaitAsync(string id, CancellationToken token) => _acks[id].Task.WaitAsync(token);
        public void Complete(string id, bool accepted) => _acks[id].TrySetResult(accepted);
    }

    private sealed class FakeInputChannel : IClaudeInputChannel
    {
        public bool RejectWrites { get; init; }
        public List<string> Lines { get; } = [];
        public Task<bool> WriteAsync(string line, CancellationToken cancellationToken, Action? writeAttemptStarted = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectWrites) return Task.FromResult(false);
            writeAttemptStarted?.Invoke();
            Lines.Add(line);
            return Task.FromResult(true);
        }
        public Task CloseAsync() => Task.CompletedTask;
    }
}

[CollectionDefinition("Claude streaming process tests", DisableParallelization = true)]
public sealed class ClaudeStreamingProcessCollection { }

[Collection("Claude streaming process tests")]
public sealed class ClaudeStreamingProcessTests : IDisposable
{
    private readonly string _desktop = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private readonly string _runRoot = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "artifacts", Guid.NewGuid().ToString("D"));
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly string _fixture;

    public ClaudeStreamingProcessTests()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        _fixture = Path.Combine(_desktop, "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0", "ChatAgentBridge.TestAgent.exe");
        var data = Path.Combine(_runRoot, "data");
        var workspace = Path.Combine(data, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(data, ".cab-claude-follow-up-fixture"), "Isolated offline Claude follow-up fixture");
        File.WriteAllText(Path.Combine(workspace, ".cab-live-steering-workspace"), "offline smoke workspace");
        File.WriteAllText(Path.Combine(data, "protocol-observations.jsonl"), "");
        File.WriteAllText(Path.Combine(data, ".cab-live-steering-release"), "release");
        SetEnvironment("CAB_CLAUDE_STEERING_FIXTURE", "1");
        SetEnvironment("CAB_LIVE_STEERING_OBSERVATIONS_FILE", Path.Combine(data, "protocol-observations.jsonl"));
        SetEnvironment("CAB_LIVE_STEERING_RELEASE_FILE", Path.Combine(data, ".cab-live-steering-release"));
        SetEnvironment("CAB_CLAUDE_STEERING_INIT_UNSUPPORTED", null);
        SetEnvironment("CAB_CLAUDE_STEERING_INIT_DELAY_MS", null);
        SetEnvironment("CAB_CLAUDE_STEERING_FIRST_ACK_DELAY_MS", null);
    }

    [Fact]
    public async Task UnsupportedInitializeFallsBackBeforeWritingPrompt()
    {
        Assert.True(File.Exists(_fixture), $"Offline fixture executable was not built: {_fixture}");
        SetEnvironment("CAB_CLAUDE_STEERING_INIT_UNSUPPORTED", "1");
        var prompt = "LIVE_STEER_STEER_1";
        var changed = new List<IActiveAgentTurn?>();

        var result = await new AgentRunner().RunAsync(Settings(), prompt, null, changed.Add);

        Assert.True(result.Success, result.Error);
        Assert.Contains("LIVE_STEER_FINAL", result.Claude!.FinalMessage);
        Assert.DoesNotContain(changed, turn => turn is not null);
        Assert.Contains("turn/completed", File.ReadAllText(Path.Combine(_runRoot, "data", "protocol-observations.jsonl")));
    }

    [Fact]
    public async Task CancellationDuringInitializeKillsProcessWithoutFallbackPrompt()
    {
        Assert.True(File.Exists(_fixture), $"Offline fixture executable was not built: {_fixture}");
        SetEnvironment("CAB_CLAUDE_STEERING_INIT_DELAY_MS", "5000");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentRunner().RunAsync(Settings(), "LIVE_STEER_STEER_1", null, _ => { }, cancellation.Token));

        Assert.Empty(File.ReadAllText(Path.Combine(_runRoot, "data", "protocol-observations.jsonl")));
    }

    [Fact]
    public async Task InitialPromptReplayCanArriveAfterFiveSeconds()
    {
        Assert.True(File.Exists(_fixture), $"Offline fixture executable was not built: {_fixture}");
        SetEnvironment("CAB_CLAUDE_STEERING_FIRST_ACK_DELAY_MS", "6000");

        var result = await new AgentRunner().RunAsync(Settings(), "LIVE_STEER_STEER_1", null, _ => { });

        Assert.True(result.Success, result.Error);
        Assert.Contains("LIVE_STEER_FINAL", result.Claude!.FinalMessage);
    }

    private BridgeSettings Settings() => new()
    {
        WorkspaceRoot = Path.Combine(_runRoot, "data", "workspace"),
        AgentProvider = AgentProfile.Claude,
        AgentExecutable = _fixture,
        AgentModel = "fixture-claude",
        ChatFollowUpMode = ChatFollowUpPolicy.Steer
    };

    private void SetEnvironment(string name, string? value)
    {
        if (!_previous.ContainsKey(name)) _previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (Directory.Exists(_runRoot)) Directory.Delete(_runRoot, recursive: true);
    }
}
