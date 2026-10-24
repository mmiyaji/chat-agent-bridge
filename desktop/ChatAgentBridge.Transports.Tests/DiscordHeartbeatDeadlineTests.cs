using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class DiscordHeartbeatDeadlineTests
{
    [Fact]
    public void Callback_queued_before_reset_cannot_complete_the_new_deadline()
    {
        using var deadline = new DiscordGatewayTransport.HeartbeatDeadline();
        deadline.Reset(TimeSpan.Zero);
        deadline.Reset(TimeSpan.FromHours(1));
        var current = deadline.Task;
        deadline.OnElapsed(null); // simulate a callback that was queued for the previous reset
        Assert.False(current.IsCompleted);
        deadline.Reset(TimeSpan.Zero);
        deadline.OnElapsed(null);
        Assert.True(deadline.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Callback_queued_before_disposal_is_safe_and_reset_after_disposal_is_rejected()
    {
        var deadline = new DiscordGatewayTransport.HeartbeatDeadline();
        deadline.Reset(TimeSpan.FromHours(1));
        deadline.Dispose();
        deadline.OnElapsed(null);
        deadline.Dispose();
        Assert.Throws<ObjectDisposedException>(() => deadline.Reset(TimeSpan.Zero));
    }
}
