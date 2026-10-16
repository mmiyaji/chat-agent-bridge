using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Transports;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatApprovalBrokerTests
{
    private static readonly ChatRequest Origin = new("mattermost", "channel-1", "thread-1", "user-1", "run", "message-1");
    private static readonly AppServerApproval Approval = new("item/command", "thread-1", "turn-1", "item-1", "Run command", "git status", "C:/repo");

    [Theory]
    [InlineData("approve", AppServerDecision.Accept)]
    [InlineData("reject", AppServerDecision.Decline)]
    public async Task RemoteDecisionResolvesOnce(string verb, AppServerDecision expected)
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeTransport();
        Task<AppServerDecision> request = broker.RequestAsync(transport, Origin, Approval, null);
        string code = await GetCodeAsync(transport);

        Assert.True(broker.TryHandle(Origin with { Text = $"!cab {verb} {code}" }));
        Assert.Equal(expected, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(broker.TryHandle(Origin with { Text = $"approve {code}" }));
    }

    [Fact]
    public async Task LongApprovalDetailsKeepBothControlCommandsInBoundedMessage()
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeTransport();
        Task<AppServerDecision> request = broker.RequestAsync(transport, Origin,
            Approval with { Description = new string('d', 5000), Command = new string('c', 5000) }, null);
        string text = await transport.LastReply.Task.WaitAsync(TimeSpan.FromSeconds(2));
        string code = await GetCodeAsync(transport);

        Assert.True(text.Length <= 3000);
        Assert.Contains($"!cab approve {code}", text);
        Assert.Contains($"!cab reject {code}", text);
        broker.TryHandle(Origin with { Text = $"reject {code}" });
        Assert.Equal(AppServerDecision.Decline, await request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("channel")]
    [InlineData("thread")]
    [InlineData("provider")]
    public async Task WrongScopeControlIsConsumedButCannotApprove(string changedScope)
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeTransport();
        Task<AppServerDecision> request = broker.RequestAsync(transport, Origin, Approval, null);
        string code = await GetCodeAsync(transport);

        var otherScope = changedScope switch
        {
            "user" => Origin with { UserId = "other-user" },
            "channel" => Origin with { ChannelId = "other-channel" },
            "thread" => Origin with { ThreadId = "other-thread" },
            "provider" => Origin with { Provider = "slack" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedScope))
        };
        Assert.True(broker.TryHandle(otherScope with { Text = $"approve {code}" }));
        Assert.False(request.IsCompleted);
        Assert.True(broker.TryHandle(Origin with { Text = $"approve {code}" }));
        Assert.Equal(AppServerDecision.Accept, await request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StaleCodeCannotResolveLaterRequest()
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var firstTransport = new FakeTransport();
        Task<AppServerDecision> first = broker.RequestAsync(firstTransport, Origin, Approval, null);
        string oldCode = await GetCodeAsync(firstTransport);
        broker.TryHandle(Origin with { Text = $"reject {oldCode}" });
        Assert.Equal(AppServerDecision.Decline, await first.WaitAsync(TimeSpan.FromSeconds(2)));

        var secondTransport = new FakeTransport();
        Task<AppServerDecision> second = broker.RequestAsync(secondTransport, Origin, Approval, null);
        string newCode = await GetCodeAsync(secondTransport);
        Assert.NotEqual(oldCode, newCode);
        Assert.True(broker.TryHandle(Origin with { Text = $"approve {oldCode}" }));
        Assert.False(second.IsCompleted);
        broker.TryHandle(Origin with { Text = $"approve {newCode}" });
        Assert.Equal(AppServerDecision.Accept, await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LocalDecisionWinsAndCancelsLocalCallbackToken()
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeTransport();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AppServerDecision> request = broker.RequestAsync(transport, Origin, Approval, async (_, token) =>
        {
            callbackStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { callbackCancelled.SetResult(); throw; }
            return AppServerDecision.Decline;
        });
        string code = await GetCodeAsync(transport);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        broker.TryHandle(Origin with { Text = $"approve {code}" });
        Assert.Equal(AppServerDecision.Accept, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        await callbackCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationClearsPendingAndPropagates()
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeTransport();
        using var cancellation = new CancellationTokenSource();
        Task<AppServerDecision> request = broker.RequestAsync(transport, Origin, Approval, null, cancellation.Token);
        string code = await GetCodeAsync(transport);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(broker.TryHandle(Origin with { Text = $"approve {code}" }));
        var nextTransport = new FakeTransport();
        Task<AppServerDecision> next = broker.RequestAsync(nextTransport, Origin, Approval, null);
        string nextCode = await GetCodeAsync(nextTransport);
        Assert.NotEqual(code, nextCode);
        broker.TryHandle(Origin with { Text = $"reject {nextCode}" });
        Assert.Equal(AppServerDecision.Decline, await next.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static async Task<string> GetCodeAsync(FakeTransport transport)
    {
        string text = await transport.LastReply.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Match match = Regex.Match(text, @"!cab approve ([0-9a-fA-F]{12})");
        Assert.True(match.Success, text);
        return match.Groups[1].Value;
    }

    private sealed class FakeTransport : IChatTransport
    {
        public TaskCompletionSource<string> LastReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
        {
            LastReply.TrySetResult(text);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
