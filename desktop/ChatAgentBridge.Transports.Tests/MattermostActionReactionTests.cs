using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostActionReactionTests
{
    private static readonly ChatRequest Origin = new("mattermost", "channel-1", "root-1", "member-1", "prompt", "input-1");
    private static readonly BridgeSettings Settings = new()
    {
        MattermostChannelId = "channel-1",
        MattermostAllowedUserIds = "member-1,member-2"
    };

    private static (MattermostWebSocketTransport Transport, FakeMattermost Fake) Create()
    {
        var fake = new FakeMattermost();
        var transport = new MattermostWebSocketTransport(new HttpClient(fake), new Uri("https://mattermost.test/"),
            "secret", "channel-1", "bot-1");
        return (transport, fake);
    }

    private static JsonElement Reaction(string emoji, string actor, string postId = "action-1", string channel = "channel-1")
    {
        var reaction = JsonSerializer.Serialize(new { emoji_name = emoji, user_id = actor, post_id = postId, channel_id = channel, id = "r-1" });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            @event = "reaction_added",
            data = new { reaction },
            broadcast = new { channel_id = channel }
        }));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Approve_by_requester_invokes_handler_and_cleans_up()
    {
        var (transport, fake) = Create();
        await using var _ = transport;
        ChatActionDecision? seen = null;
        var lease = await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (d, _) => { seen = d; return Task.FromResult(ChatActionResult.Completed); }, default);

        Assert.Contains(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v4/posts" && r.Body.Contains("\"root_id\":\"root-1\""));
        Assert.Equal(2, fake.Requests.Count(r => r.Path == "/api/v4/reactions"));
        Assert.Contains(fake.Requests, r => r.Body.Contains("white_check_mark"));
        Assert.Contains(fake.Requests, r => r.Body.Contains("\"emoji_name\":\"x\""));
        Assert.Equal(1, transport.PendingActionCount);

        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"), Settings).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ChatActionDecision.Approve, seen);
        Assert.Equal(0, transport.PendingActionCount);
        Assert.Contains(fake.Requests, r => r.Method == "DELETE" && r.Path.EndsWith("/posts/action-1/reactions/white_check_mark"));
        Assert.Contains(fake.Requests, r => r.Method == "DELETE" && r.Path.EndsWith("/posts/action-1/reactions/x"));

        var deletes = fake.Requests.Count(r => r.Method == "DELETE");
        await lease.DisposeAsync();
        Assert.Equal(deletes, fake.Requests.Count(r => r.Method == "DELETE"));
    }

    [Theory]
    [InlineData("x", ChatActionDecision.Reject)]
    [InlineData("-1", ChatActionDecision.Reject)]
    [InlineData("thumbsdown", ChatActionDecision.Reject)]
    [InlineData("+1", ChatActionDecision.Approve)]
    public async Task Alias_emoji_map_to_decisions(string emoji, ChatActionDecision expected)
    {
        var (transport, _) = Create();
        await using var _ = transport;
        ChatActionDecision? seen = null;
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.RequestApproval,
            (d, _) => { seen = d; return Task.FromResult(ChatActionResult.Completed); }, default);

        await transport.ProcessActionReactionAsync(Reaction(emoji, "member-1"), Settings).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task Reactions_from_others_the_bot_or_wrong_scope_are_ignored()
    {
        var (transport, _) = Create();
        await using var _ = transport;
        var calls = 0;
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(ChatActionResult.Completed); }, default);

        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-2"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "bot-1"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1", "other-post"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1", channel: "channel-2"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("thumbsup_unknown", "member-1"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"),
            new BridgeSettings { MattermostChannelId = "channel-1", MattermostAllowedUserIds = "member-2" });

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(1, transport.PendingActionCount);
    }

    [Fact]
    public async Task Busy_keeps_registration_so_a_later_reaction_can_retry()
    {
        var (transport, _) = Create();
        await using var _ = transport;
        var calls = 0;
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ChatActionResult.Busy : ChatActionResult.Completed), default);

        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"), Settings).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.PendingActionCount);

        await transport.ProcessActionReactionAsync(Reaction("x", "member-1"), Settings).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(0, transport.PendingActionCount);
    }

    [Fact]
    public async Task Only_one_decision_runs_at_a_time()
    {
        var (transport, _) = Create();
        await using var _ = transport;
        var release = new TaskCompletionSource<ChatActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => { Interlocked.Increment(ref calls); return release.Task; }, default);

        var first = transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"), Settings);
        await transport.ProcessActionReactionAsync(Reaction("x", "member-1"), Settings);
        release.SetResult(ChatActionResult.Completed);
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Handler_exception_is_treated_as_unavailable()
    {
        var (transport, _) = Create();
        await using var _ = transport;
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => throw new InvalidOperationException("boom"), default);

        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"), Settings).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, transport.PendingActionCount);
    }

    [Fact]
    public async Task Disposing_the_lease_unregisters_and_removes_reactions()
    {
        var (transport, fake) = Create();
        await using var _ = transport;
        var calls = 0;
        var lease = await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(ChatActionResult.Completed); }, default);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(0, transport.PendingActionCount);
        Assert.Equal(2, fake.Requests.Count(r => r.Method == "DELETE"));
        await transport.ProcessActionReactionAsync(Reaction("white_check_mark", "member-1"), Settings);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Transport_dispose_clears_pending_actions()
    {
        var (transport, fake) = Create();
        await transport.PostActionsAsync(Origin, "Approve?", ChatActionKind.ToolApproval,
            (_, _) => Task.FromResult(ChatActionResult.Completed), default);

        await transport.DisposeAsync();

        Assert.Equal(0, transport.PendingActionCount);
        Assert.Equal(2, fake.Requests.Count(r => r.Method == "DELETE"));
    }

    [Fact]
    public async Task Wrong_channel_is_rejected_before_http()
    {
        var (transport, fake) = Create();
        await using var _ = transport;

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PostActionsAsync(
            Origin with { ChannelId = "channel-2" }, "x", ChatActionKind.ToolApproval,
            (_, _) => Task.FromResult(ChatActionResult.Completed), default));

        Assert.Empty(fake.Requests);
    }

    private sealed class FakeMattermost : HttpMessageHandler
    {
        public ConcurrentQueue<Snapshot> RequestQueue { get; } = new();
        public IReadOnlyCollection<Snapshot> Requests => RequestQueue.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            RequestQueue.Enqueue(new Snapshot(request.Method.Method, path, body));
            var (status, json) = request.Method == HttpMethod.Post && path == "/api/v4/posts"
                ? (HttpStatusCode.Created, "{\"id\":\"action-1\",\"channel_id\":\"channel-1\",\"root_id\":\"root-1\"}")
                : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Snapshot(string Method, string Path, string Body);
}
