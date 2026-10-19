using ChatAgentBridge.Transports;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatProgressReporterTests
{
    private static readonly ChatRequest Request = new("mattermost", "channel", "thread", "user", "prompt", "request-post");

    [Fact]
    public async Task ReceiptRunningHeartbeatAndFinishEditOneMessage()
    {
        var transport = new FakeProgressTransport();
        await using var reporter = CreateReporter(transport, interval: TimeSpan.FromMilliseconds(25));

        await reporter.ReceiveAsync();
        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        await reporter.StartAsync();
        await transport.WaitForUpdatesAsync(2);
        await reporter.FinishAsync(ChatProgressState.Completed);
        await reporter.FinishAsync(ChatProgressState.Failed);

        Assert.Single(transport.Posts);
        Assert.All(transport.Updates, update => Assert.Same(transport.PostedMessage, update.Message));
        Assert.Equal("Completed", transport.Updates[^1].Text);
        Assert.DoesNotContain(transport.Updates.SkipWhile(update => update.Text != "Completed"), update => update.Text == "Running");
    }

    [Fact]
    public async Task FinishWaitsForHeartbeatCallBeforeWritingTerminalState()
    {
        var transport = new FakeProgressTransport { BlockHeartbeat = true };
        var failures = 0;
        await using var reporter = CreateReporter(transport, interval: TimeSpan.FromMilliseconds(20),
            failure: _ => Interlocked.Increment(ref failures));

        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        await transport.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var finish = reporter.FinishAsync(ChatProgressState.Completed);
        await transport.HeartbeatCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await finish.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Completed", transport.Updates[^1].Text);
        Assert.Single(transport.Updates, update => update.Text == "Completed");
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task PermissionIsRecheckedBeforeEveryPostAndEdit()
    {
        var transport = new FakeProgressTransport();
        var allowedChecks = 0;
        await using var reporter = new ChatProgressReporter(
            transport,
            Request,
            (state, _) => state.ToString(),
            _ => Task.FromResult(Interlocked.Increment(ref allowedChecks) == 1),
            interval: TimeSpan.FromHours(1));

        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        await reporter.FinishAsync(ChatProgressState.Completed);

        Assert.Equal(3, allowedChecks);
        Assert.Single(transport.Posts);
        Assert.Empty(transport.Updates);
    }

    [Fact]
    public async Task ProviderAndCallbackFailuresDoNotEscapeOrCreateFallbackPosts()
    {
        var transport = new FakeProgressTransport { FailPost = true };
        var failures = 0;
        await using var reporter = new ChatProgressReporter(
            transport,
            Request,
            (state, _) => state.ToString(),
            _ => Task.FromResult(true),
            _ => { Interlocked.Increment(ref failures); throw new InvalidOperationException("callback failure"); },
            interval: TimeSpan.FromHours(1));

        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        await reporter.FinishAsync(ChatProgressState.Failed);

        Assert.Single(transport.Posts);
        Assert.Empty(transport.Updates);
        Assert.True(failures > 0);
    }

    [Fact]
    public async Task FinishWithoutReceiptDoesNotPostAfterTerminalState()
    {
        var transport = new FakeProgressTransport();
        await using var reporter = CreateReporter(transport, interval: TimeSpan.FromHours(1));

        await reporter.FinishAsync(ChatProgressState.Rejected);
        await reporter.ReceiveAsync();
        await reporter.StartAsync();

        Assert.Empty(transport.Posts);
        Assert.Empty(transport.Updates);
    }

    [Fact]
    public async Task DisposeStopsHeartbeatAndPreventsLaterWrites()
    {
        var transport = new FakeProgressTransport();
        var reporter = CreateReporter(transport, interval: TimeSpan.FromMilliseconds(20));
        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        await transport.WaitForUpdatesAsync(2);
        await reporter.DisposeAsync();
        var countAfterDispose = transport.Updates.Count;

        await Task.Delay(100);

        Assert.Equal(countAfterDispose, transport.Updates.Count);
        await reporter.FinishAsync(ChatProgressState.Disconnected);
        Assert.Equal(countAfterDispose, transport.Updates.Count);
    }

    private static ChatProgressReporter CreateReporter(
        FakeProgressTransport transport,
        TimeSpan interval,
        Action<Exception>? failure = null) => new(
            transport,
            Request,
            (state, _) => state.ToString(),
            _ => Task.FromResult(true),
            failure,
            interval,
            TimeSpan.FromSeconds(1));

    private sealed class FakeProgressTransport : IChatTransport, IChatProgressTransport
    {
        private readonly object _sync = new();
        private int _heartbeatCalls;

        public List<string> Posts { get; } = [];
        public List<(ChatProgressMessage Message, string Text)> Updates { get; } = [];
        public ChatProgressMessage? PostedMessage { get; private set; }
        public bool FailPost { get; init; }
        public bool BlockHeartbeat { get; init; }
        public TaskCompletionSource HeartbeatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken)
        {
            lock (_sync) Posts.Add(text);
            if (FailPost) throw new InvalidOperationException("test post failure");
            PostedMessage = new ChatProgressMessage(request.Provider, request.ChannelId, request.ThreadId,
                request.UserId, "progress-post", this, 1);
            return Task.FromResult(PostedMessage);
        }

        public async Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken)
        {
            var isHeartbeat = text == "Running" && Interlocked.Increment(ref _heartbeatCalls) > 1;
            if (BlockHeartbeat && isHeartbeat)
            {
                HeartbeatEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    HeartbeatCancelled.TrySetResult();
                    throw;
                }
            }
            lock (_sync) Updates.Add((message, text));
        }

        public async Task WaitForUpdatesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                lock (_sync)
                {
                    if (Updates.Count >= count) return;
                }
                await Task.Delay(5, timeout.Token);
            }
        }
    }
}
