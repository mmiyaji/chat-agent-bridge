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
    public async Task RequestUpdateCoalescesBurstsAndRespectsMinimumInterval()
    {
        var transport = new FakeProgressTransport();
        await using var reporter = new ChatProgressReporter(transport, Request, (state, _) => state.ToString(),
            _ => Task.FromResult(true), null, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300));
        await reporter.ReceiveAsync();
        await reporter.StartAsync();
        Assert.Single(transport.Updates);

        for (var i = 0; i < 20; i++) reporter.RequestUpdate();
        await Task.Delay(100);
        Assert.Single(transport.Updates);

        await transport.WaitForUpdatesAsync(2);
        await Task.Delay(500);
        Assert.Equal(2, transport.Updates.Count);
        Assert.All(transport.Updates, update => Assert.Equal("Running", update.Text));
    }

    [Fact]
    public async Task RequestUpdateIsNoOpBeforeStartAndAfterFinish()
    {
        var transport = new FakeProgressTransport();
        await using var reporter = new ChatProgressReporter(transport, Request, (state, _) => state.ToString(),
            _ => Task.FromResult(true), null, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20));
        await reporter.ReceiveAsync();
        reporter.RequestUpdate();
        await Task.Delay(100);
        Assert.Empty(transport.Updates);

        await reporter.StartAsync();
        reporter.RequestUpdate();
        await reporter.FinishAsync(ChatProgressState.Completed);
        var count = transport.Updates.Count;
        reporter.RequestUpdate();
        await Task.Delay(150);

        Assert.Equal(count, transport.Updates.Count);
        Assert.Equal("Completed", transport.Updates[^1].Text);
    }

    [Theory]
    [InlineData(ChatProgressState.FollowUpApplied)]
    [InlineData(ChatProgressState.FollowUpUncertain)]
    public async Task FollowUpPendingHeartbeatRemainsPendingAndFinishesOnce(ChatProgressState terminal)
    {
        var transport = new FakeProgressTransport();
        await using var reporter = CreateReporter(transport, interval: TimeSpan.FromMilliseconds(25));

        await reporter.ReceiveAsync();
        await reporter.StartFollowUpAsync();
        await transport.WaitForUpdatesAsync(2);

        Assert.All(transport.Updates, update => Assert.Equal(ChatProgressState.FollowUpPending.ToString(), update.Text));
        Assert.Single(transport.CompletedProgress);

        await reporter.FinishAsync(terminal);
        await reporter.DisposeAsync();

        Assert.Equal(terminal.ToString(), transport.Updates[^1].Text);
        Assert.DoesNotContain(transport.Updates.Take(transport.Updates.Count - 1), update => update.Text == ChatProgressState.Running.ToString());
        Assert.All(transport.Updates.Take(transport.Updates.Count - 1), update => Assert.Equal(ChatProgressState.FollowUpPending.ToString(), update.Text));
        Assert.Single(transport.CompletedProgress);
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
        Assert.Single(transport.CompletedProgress);
    }

    [Fact]
    public async Task TerminalLifecycleIsClearedBeforeFinalEditEvenWhenThatEditFails()
    {
        var transport = new FakeProgressTransport { FailTerminalUpdate = true };
        await using var reporter = CreateReporter(transport, interval: TimeSpan.FromHours(1));

        await reporter.ReceiveAsync();
        await reporter.FinishAsync(ChatProgressState.Completed);

        Assert.Same(transport.PostedMessage, Assert.Single(transport.CompletedProgress));
        Assert.Equal(new[] { "complete", "terminal-update" }, transport.LifecycleOrder);
    }

    [Fact]
    public async Task FinishAwaitsControlCleanupBeforeFinalEditAndDisposesOnlyOnce()
    {
        var transport = new AsyncCleanupTransport();
        var reporter = CreateReporter(transport, interval: TimeSpan.FromHours(1));
        await reporter.ReceiveAsync();
        var finish = reporter.FinishAsync(ChatProgressState.Completed);
        await transport.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finish.IsCompleted);
        Assert.Empty(transport.Updates);
        Assert.Single(transport.CompletedProgress);
        transport.ReleaseCleanup.TrySetResult();
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        await reporter.DisposeAsync();
        Assert.Equal("Completed", Assert.Single(transport.Updates).Text);
        Assert.Single(transport.CompletedProgress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureOrTimeoutDoesNotBlockTerminalEdit(bool fail)
    {
        var transport = new AsyncCleanupTransport { FailCleanup = fail };
        var failures = 0;
        await using var reporter = new ChatProgressReporter(transport, Request, (state, _) => state.ToString(),
            _ => Task.FromResult(true), _ => Interlocked.Increment(ref failures),
            TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(100));
        await reporter.ReceiveAsync();
        await reporter.FinishAsync(ChatProgressState.Completed).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Completed", Assert.Single(transport.Updates).Text);
        Assert.Single(transport.CompletedProgress);
        Assert.True(failures > 0);
        transport.ReleaseCleanup.TrySetResult();
    }

    private sealed class AsyncCleanupTransport : FakeProgressTransport, IChatProgressLifecycleTransport
    {
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailCleanup { get; init; }

        public async Task CompleteProgressAsync(ChatProgressMessage message, CancellationToken cancellationToken = default)
        {
            CompleteProgress(message);
            CleanupStarted.TrySetResult();
            if (FailCleanup) throw new IOException("test cleanup failure");
            // Deliberately ignores cancellation to verify the reporter's own time bound.
            await ReleaseCleanup.Task;
        }
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

    private class FakeProgressTransport : IChatTransport, IChatProgressTransport, IChatProgressLifecycleTransport
    {
        private readonly object _sync = new();
        private int _heartbeatCalls;

        private readonly List<string> _posts = [];
        private readonly List<(ChatProgressMessage Message, string Text)> _updates = [];
        private readonly List<ChatProgressMessage> _completedProgress = [];
        private readonly List<string> _lifecycleOrder = [];

        // Assertions can run while the heartbeat is writing; enumerate stable snapshots.
        public IReadOnlyList<string> Posts { get { lock (_sync) return _posts.ToArray(); } }
        public IReadOnlyList<(ChatProgressMessage Message, string Text)> Updates { get { lock (_sync) return _updates.ToArray(); } }
        public IReadOnlyList<ChatProgressMessage> CompletedProgress { get { lock (_sync) return _completedProgress.ToArray(); } }
        public IReadOnlyList<string> LifecycleOrder { get { lock (_sync) return _lifecycleOrder.ToArray(); } }
        public ChatProgressMessage? PostedMessage { get; private set; }
        public bool FailPost { get; init; }
        public bool FailTerminalUpdate { get; init; }
        public bool BlockHeartbeat { get; init; }
        public TaskCompletionSource HeartbeatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(BridgeSettings settings, Func<ChatRequest, CancellationToken, Task> onRequest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReplyAsync(ChatRequest request, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ChatProgressMessage> PostProgressAsync(ChatRequest request, string text, CancellationToken cancellationToken)
        {
            lock (_sync) _posts.Add(text);
            if (FailPost) throw new InvalidOperationException("test post failure");
            PostedMessage = new ChatProgressMessage(request.Provider, request.ChannelId, request.ThreadId,
                request.UserId, "progress-post", this, 1);
            return Task.FromResult(PostedMessage);
        }

        public async Task UpdateProgressAsync(ChatRequest request, ChatProgressMessage message, string text, CancellationToken cancellationToken)
        {
            if (text is "Completed" or "FollowUpApplied" or "FollowUpUncertain")
            {
                lock (_sync) _lifecycleOrder.Add("terminal-update");
                if (FailTerminalUpdate) throw new InvalidOperationException("terminal update failed");
            }
            var isHeartbeat = (text is "Running" or "FollowUpPending") && Interlocked.Increment(ref _heartbeatCalls) > 1;
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
            lock (_sync) _updates.Add((message, text));
        }

        public void CompleteProgress(ChatProgressMessage message)
        {
            lock (_sync)
            {
                _completedProgress.Add(message);
                _lifecycleOrder.Add("complete");
            }
        }

        public async Task WaitForUpdatesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                lock (_sync)
                {
                    if (_updates.Count >= count) return;
                }
                await Task.Delay(5, timeout.Token);
            }
        }
    }
}
