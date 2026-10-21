using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge.Core.Tests;

public sealed class NotificationActivationRelayTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("approve")]
    [InlineData("decline")]
    [InlineData("open")]
    public async Task Started_owner_accepts_activation_and_acknowledges_only_enqueue(string action)
    {
        using var owner = new NotificationActivationRelay();
        var queue = new ConcurrentQueue<(string Token, string Action)>();
        owner.Start((token, decision) => { queue.Enqueue((token, decision)); return true; });

        Assert.Matches("^[0-9a-f]{32}$", owner.OwnerId);
        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, action));
        Assert.Equal((Token, action), Assert.Single(queue));
    }

    [Fact]
    public async Task Each_owner_has_its_own_nonce_and_dispatch_queue()
    {
        using var first = new NotificationActivationRelay();
        using var second = new NotificationActivationRelay();
        var firstCalls = 0;
        var secondCalls = 0;
        first.Start((_, _) => { firstCalls++; return true; });
        second.Start((_, _) => { secondCalls++; return true; });

        Assert.NotEqual(first.OwnerId, second.OwnerId);
        Assert.True(await NotificationActivationRelay.TryForwardAsync(second.OwnerId, Token, "open"));
        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Theory]
    [InlineData("../bad", Token, "approve")]
    [InlineData("0123456789abcdef0123456789abcdef/pipe", Token, "approve")]
    [InlineData(Token, "bad", "approve")]
    [InlineData(Token, "g123456789abcdef0123456789abcdef", "approve")]
    [InlineData(Token, Token, "APPROVE")]
    [InlineData(Token, Token, "unknown")]
    [InlineData(null, Token, "approve")]
    [InlineData(Token, null, "approve")]
    [InlineData(Token, Token, null)]
    public async Task Invalid_input_is_rejected_before_connecting(string? owner, string? token, string? action)
    {
        Assert.False(await NotificationActivationRelay.TryForwardAsync(owner!, token!, action!));
    }

    [Fact]
    public async Task Unknown_owner_has_a_bounded_connection_deadline()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(await NotificationActivationRelay.TryForwardAsync(Guid.NewGuid().ToString("N"), Token, "open"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(7), $"Unknown owner took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Caller_cancellation_stops_connection_attempt()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.False(await NotificationActivationRelay.TryForwardAsync(Guid.NewGuid().ToString("N"), Token, "open", cancellation.Token));
    }

    [Fact]
    public async Task Dispatch_rejection_and_exception_do_not_stop_listener()
    {
        using var owner = new NotificationActivationRelay();
        var calls = 0;
        owner.Start((_, _) => ++calls switch { 1 => false, 2 => throw new InvalidOperationException(), _ => true });

        Assert.False(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "approve"));
        Assert.False(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "approve"));
        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "approve"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Disposed_owner_closes_an_idle_client_and_cannot_accept_more_work()
    {
        var owner = new NotificationActivationRelay();
        owner.Start((_, _) => throw new Exception("Must not enqueue"));
        using var idle = Client(owner.OwnerId);
        await idle.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        owner.Dispose();

        Assert.False(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "approve"));
        Assert.Throws<ObjectDisposedException>(() => owner.Start((_, _) => true));
    }

    [Fact]
    public async Task An_idle_client_is_evicted_and_next_activation_is_serviced()
    {
        using var owner = new NotificationActivationRelay();
        owner.Start((_, _) => true);
        using var idle = Client(owner.OwnerId);
        await idle.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Let the server's bounded read expire; this connection never sends a frame.
        await Task.Delay(TimeSpan.FromMilliseconds(2250));

        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "approve"));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("token")]
    [InlineData("action")]
    [InlineData("overlong")]
    public async Task Malformed_wire_frame_is_rejected_and_listener_remains_usable(string malformed)
    {
        if (malformed == "overlong" && !OperatingSystem.IsWindows()) return;
        using var owner = new NotificationActivationRelay();
        var calls = 0;
        owner.Start((_, _) => { calls++; return true; });
        var frame = Frame();
        if (malformed == "version") frame[0] = 2;
        if (malformed == "token") frame[1] = 0xff;
        if (malformed == "action") frame[33] = 255;
        if (malformed == "overlong") Array.Resize(ref frame, 4096);
        using (var client = Client(owner.OwnerId))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(deadline.Token);
            try
            {
                await client.WriteAsync(frame, deadline.Token);
                var acknowledgement = new byte[1];
                await client.ReadExactlyAsync(acknowledgement, deadline.Token);
                Assert.Equal(0, acknowledgement[0]);
            }
            catch (IOException) when (malformed == "overlong")
            {
                // The server may close while the oversized message is still being written.
            }
        }

        Assert.Equal(0, calls);
        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "open"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Truncated_frame_cannot_enqueue_and_does_not_stop_listener()
    {
        using var owner = new NotificationActivationRelay();
        var calls = 0;
        owner.Start((_, _) => { calls++; return true; });
        using (var client = Client(owner.OwnerId))
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await client.WriteAsync(Frame().AsMemory(0, 12));
        }
        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, Token, "open"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Duplicate_delivery_only_enqueues_and_registry_controls_single_use()
    {
        using var owner = new NotificationActivationRelay();
        var registry = new PendingNotificationRegistry();
        var calls = 0;
        var pending = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(1),
            () => { calls++; return Task.FromResult(PendingNotificationActionResult.Completed); },
            () => throw new Exception("Wrong action"), () => Task.CompletedTask);
        var queue = new ConcurrentQueue<(string Token, string Action)>();
        owner.Start((token, action) => { queue.Enqueue((token, action)); return true; });

        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, pending.Token, "approve"));
        Assert.True(await NotificationActivationRelay.TryForwardAsync(owner.OwnerId, pending.Token, "approve"));
        Assert.Equal(0, calls);
        while (queue.TryDequeue(out var activation))
            await registry.InvokeAsync(activation.Token, activation.Action);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Connected_owner_that_never_acknowledges_is_bounded_by_sender_deadline()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream("ChatAgentBridge.Notification." + ownerId,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var watch = Stopwatch.StartNew();
        var send = NotificationActivationRelay.TryForwardAsync(ownerId, Token, "approve");
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var frame = new byte[34];
        await server.ReadExactlyAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(await send);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(7), $"Missing acknowledgement took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Lost_acknowledgement_does_not_reconnect_or_replay()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream("ChatAgentBridge.Notification." + ownerId,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var send = NotificationActivationRelay.TryForwardAsync(ownerId, Token, "approve");
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var frame = new byte[34];
        await server.ReadExactlyAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        server.Disconnect();

        Assert.False(await send);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.WaitForConnectionAsync(deadline.Token));
    }

    private static NamedPipeClientStream Client(string ownerId) => new(".",
        "ChatAgentBridge.Notification." + ownerId, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static byte[] Frame()
    {
        var frame = new byte[34];
        frame[0] = 1;
        Encoding.ASCII.GetBytes(Token, frame.AsSpan(1, 32));
        frame[33] = 1;
        return frame;
    }
}
