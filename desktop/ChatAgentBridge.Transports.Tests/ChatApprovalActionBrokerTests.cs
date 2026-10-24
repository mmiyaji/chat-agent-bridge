using System.Globalization;
using System.Text.RegularExpressions;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatApprovalActionBrokerTests
{
    private static readonly ChatRequest Origin = new("discord", "323456789012345678", "423456789012345678",
        "123456789012345678", "run", "message-1") { GuildId = "223456789012345678" };
    private static readonly AppServerApproval Approval = new("item/command", "thread-1", "turn-1", "item-1",
        "Run command", "git status", "C:/repo");

    [Theory]
    [InlineData(ChatActionDecision.Approve, AppServerDecision.Accept)]
    [InlineData(ChatActionDecision.Reject, AppServerDecision.Decline)]
    public async Task TypedActionsResolveOnce(ChatActionDecision button, AppServerDecision expected)
    {
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var transport = new FakeActionTransport();
        var request = broker.RequestAsync(transport, Origin, Approval, null, publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ChatActionKind.ToolApproval, transport.Kind);
        Assert.Equal(ChatActionResult.Completed, await transport.ClickAsync(button));
        Assert.Equal(expected, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(transport.LeaseDisposed.Task.IsCompleted);
        Assert.Equal(ChatActionResult.Unavailable, await transport.ClickAsync(button));
    }

    [Fact]
    public async Task ApprovalIsPrivateByDefaultAndDoesNotPostActions()
    {
        var transport = new FakeActionTransport();
        var result = await new ChatApprovalBroker().RequestAsync(transport, Origin, Approval, null);
        Assert.Equal(AppServerDecision.Decline, result);
        Assert.Equal(0, transport.PostCount);
        Assert.Equal(0, transport.ReplyCount);
    }

    [Fact]
    public async Task LocalDecisionDisposesLeaseAndMakesStaleButtonUnavailable()
    {
        var transport = new FakeActionTransport();
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var request = broker.RequestAsync(transport, Origin, Approval,
            (_, _) => Task.FromResult(AppServerDecision.Accept), publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AppServerDecision.Accept, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        await transport.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatActionResult.Unavailable, await transport.ClickAsync(ChatActionDecision.Approve));
    }

    [Fact]
    public async Task CallerCancellationDisposesActionLease()
    {
        var transport = new FakeActionTransport();
        using var cancellation = new CancellationTokenSource();
        var request = new ChatApprovalBroker(TimeSpan.FromSeconds(5)).RequestAsync(
            transport, Origin, Approval, null, cancellation.Token, publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await transport.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatActionResult.Unavailable, await transport.ClickAsync(ChatActionDecision.Approve));
    }

    [Fact]
    public async Task TimeoutDisposesActionLease()
    {
        var transport = new FakeActionTransport();
        var request = new ChatApprovalBroker(TimeSpan.FromMilliseconds(50)).RequestAsync(
            transport, Origin, Approval, null, publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AppServerDecision.Decline, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        await transport.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatActionResult.Unavailable, await transport.ClickAsync(ChatActionDecision.Approve));
    }

    [Fact]
    public async Task FailedLiveAuthorizationCannotAcceptFromButton()
    {
        var transport = new FakeActionTransport();
        var request = new ChatApprovalBroker(TimeSpan.FromMilliseconds(100)).RequestAsync(
            transport, Origin, Approval, null, publishToThread: true,
            canDecideInChat: _ => Task.FromResult(false));
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ChatActionResult.Unavailable, await transport.ClickAsync(ChatActionDecision.Approve));
        Assert.Equal(AppServerDecision.Decline, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(transport.LeaseDisposed.Task.IsCompleted);
    }

    [Fact]
    public async Task TextControlFromWrongGuildIsConsumedButCannotApprove()
    {
        var transport = new FakeActionTransport();
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var request = broker.RequestAsync(transport, Origin, Approval, null, publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var code = GetCode(transport.Text);

        Assert.True(broker.TryHandle(Origin with { GuildId = "923456789012345678", Text = $"approve {code}" }));
        Assert.False(request.IsCompleted);
        Assert.True(broker.TryHandle(Origin with { Text = $"approve {code}" }));
        Assert.Equal(AppServerDecision.Accept, await request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DiscordActionTextFits1800CharactersAndKeepsBothControlCommands()
    {
        var transport = new FakeActionTransport();
        var broker = new ChatApprovalBroker(TimeSpan.FromSeconds(3));
        var request = broker.RequestAsync(transport, Origin,
            Approval with { Description = new string('d', 5000), Command = new string('c', 5000) },
            null, publishToThread: true);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var code = GetCode(transport.Text);

        Assert.True(transport.Text.Length <= 1800);
        Assert.Contains($"!cab approve {code}", transport.Text);
        Assert.Contains($"!cab reject {code}", transport.Text);
        Assert.Equal(ChatActionResult.Completed, await transport.ClickAsync(ChatActionDecision.Reject));
        Assert.Equal(AppServerDecision.Decline, await request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("en-US", "Codex is requesting approval to perform an action.", "Approve: !cab approve ", "Reject: !cab reject ")]
    [InlineData("ja-JP", "Codex が操作の承認を求めています。", "承認: !cab approve ", "拒否: !cab reject ")]
    public async Task PublishedApprovalUsesLocalizedCopyAndKeepsCommandTokens(string cultureName, string title, string approveLine, string rejectLine)
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var transport = new FakeActionTransport();
            var request = new ChatApprovalBroker(TimeSpan.FromSeconds(3)).RequestAsync(
                transport, Origin, Approval, null, publishToThread: true);
            await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains(title, transport.Text, StringComparison.Ordinal);
            Assert.Matches(Regex.Escape(approveLine) + "[0-9A-F]{12}", transport.Text);
            Assert.Matches(Regex.Escape(rejectLine) + "[0-9A-F]{12}", transport.Text);
            Assert.Equal(ChatActionResult.Completed, await transport.ClickAsync(ChatActionDecision.Reject));
            Assert.Equal(AppServerDecision.Decline, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static string GetCode(string text)
    {
        var match = Regex.Match(text, @"!cab approve ([0-9a-fA-F]{12})");
        Assert.True(match.Success, text);
        return match.Groups[1].Value;
    }

    private sealed class FakeActionTransport : IChatTransport, IChatActionTransport
    {
        private Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>>? _handler;

        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LeaseDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get; private set; } = "";
        public ChatActionKind Kind { get; private set; }
        public int PostCount { get; private set; }
        public int ReplyCount { get; private set; }

        public Task RunAsync(ChatAgentBridge.Core.BridgeSettings settings,
            Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken)
        {
            ReplyCount++;
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> PostActionsAsync(ChatRequest origin, string text, ChatActionKind kind,
            Func<ChatActionDecision, CancellationToken, Task<ChatActionResult>> handler, CancellationToken cancellationToken)
        {
            PostCount++;
            Text = text;
            Kind = kind;
            _handler = handler;
            Posted.TrySetResult();
            return Task.FromResult<IAsyncDisposable>(new FakeLease(() => LeaseDisposed.TrySetResult()));
        }

        public Task<ChatActionResult> ClickAsync(ChatActionDecision decision) =>
            _handler!(decision, CancellationToken.None);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeLease(Action dispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
