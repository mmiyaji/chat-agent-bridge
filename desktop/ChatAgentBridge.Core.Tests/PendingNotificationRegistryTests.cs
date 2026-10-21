using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge.Core.Tests;

public sealed class PendingNotificationRegistryTests
{
    [Theory]
    [InlineData("approve")]
    [InlineData("decline")]
    public async Task Busy_action_can_be_retried_once_without_extending_expiration(string action)
    {
        var clock = new Clock();
        var registry = new PendingNotificationRegistry(clock);
        var calls = 0;
        Task<PendingNotificationActionResult> Callback() => Task.FromResult(++calls == 1 ? PendingNotificationActionResult.Busy : PendingNotificationActionResult.Completed);
        var entry = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(5), Callback, Callback, () => Task.CompletedTask);
        var expires = entry.ExpiresAt;
        Assert.Equal(PendingNotificationRegistry.Outcome.Busy, (await registry.InvokeAsync(entry.Token, action)).Outcome);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(PendingNotificationRegistry.Outcome.Completed, (await registry.InvokeAsync(entry.Token, action)).Outcome);
        Assert.Equal(PendingNotificationRegistry.Outcome.Missing, (await registry.InvokeAsync(entry.Token, action)).Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(expires, entry.ExpiresAt);
    }

    [Fact]
    public async Task Duplicate_click_during_processing_does_not_invoke_callback_twice()
    {
        var registry = new PendingNotificationRegistry();
        var calls = 0;
        var answer = new TaskCompletionSource<PendingNotificationActionResult>();
        var entry = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(5),
            () => { calls++; return answer.Task; }, () => throw new Exception("Wrong decision"), () => Task.CompletedTask);
        var first = registry.InvokeAsync(entry.Token, "approve");
        Assert.Equal(PendingNotificationRegistry.Outcome.Busy, (await registry.InvokeAsync(entry.Token, "decline")).Outcome);
        answer.SetResult(PendingNotificationActionResult.Completed);
        Assert.Equal(PendingNotificationRegistry.Outcome.Completed, (await first).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Expired_missing_and_unknown_actions_cannot_approve_work()
    {
        var clock = new Clock();
        var registry = new PendingNotificationRegistry(clock);
        Task<PendingNotificationActionResult> Never() => throw new Exception("Must not approve");
        var entry = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(5), Never, Never, () => Task.CompletedTask);
        Assert.Equal(PendingNotificationRegistry.Outcome.InvalidAction, (await registry.InvokeAsync(entry.Token, "unexpected")).Outcome);
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(PendingNotificationRegistry.Outcome.Expired, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
        Assert.Equal(PendingNotificationRegistry.Outcome.Missing, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
    }

    [Fact]
    public async Task Opening_request_does_not_consume_its_decision()
    {
        var registry = new PendingNotificationRegistry();
        var opens = 0;
        var entry = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(5),
            () => Task.FromResult(PendingNotificationActionResult.Completed), () => throw new Exception("Wrong decision"),
            () => { opens++; return Task.CompletedTask; });
        Assert.Equal(PendingNotificationRegistry.Outcome.Opened, (await registry.InvokeAsync(entry.Token, "open")).Outcome);
        Assert.Equal(PendingNotificationRegistry.Outcome.Completed, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Unavailable_action_is_invalidated_and_exception_does_not_leave_inflight_stuck()
    {
        var registry = new PendingNotificationRegistry();
        var calls = 0;
        var entry = registry.Add(PendingNotificationKind.ChatRequest, TimeSpan.FromMinutes(5),
            () => ++calls == 1 ? throw new InvalidOperationException() : Task.FromResult(PendingNotificationActionResult.Unavailable),
            () => throw new Exception("Wrong decision"), () => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InvokeAsync(entry.Token, "approve"));
        Assert.Equal(PendingNotificationRegistry.Outcome.Unavailable, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
        Assert.Equal(PendingNotificationRegistry.Outcome.Missing, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
    }

    [Fact]
    public async Task Clear_or_resolve_during_callback_cannot_resurrect_decision()
    {
        var registry = new PendingNotificationRegistry();
        var answer = new TaskCompletionSource<PendingNotificationActionResult>();
        var entry = registry.Add(PendingNotificationKind.AiApproval, TimeSpan.FromMinutes(5), () => answer.Task,
            () => throw new Exception("Wrong decision"), () => Task.CompletedTask);
        var pending = registry.InvokeAsync(entry.Token, "approve");
        Assert.Single(registry.Clear());
        answer.SetResult(PendingNotificationActionResult.Busy);
        Assert.Equal(PendingNotificationRegistry.Outcome.Missing, (await pending).Outcome);
        Assert.Equal(PendingNotificationRegistry.Outcome.Missing, (await registry.InvokeAsync(entry.Token, "approve")).Outcome);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
