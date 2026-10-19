using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostContinuationTests
{
    private const string Server = "https://chat.example.test";
    private const string Channel = "channel-1";
    private const string User = "user-1";
    private const string Root = "root-post-1";

    [Fact]
    public async Task Immediate_plain_thread_reply_is_allowed_only_after_an_explicit_request_is_delivered()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        var tracker = new MattermostContinuationTracker();
        var seen = new BoundedSeenSet(20);
        var settings = Settings(User);
        var delivered = new List<ChatRequest>();

        var explicitDelivered = await ProcessAsync(Post("root-post-1", "!cab start work"), settings, store, tracker, seen,
            (request, _) => { delivered.Add(request); return Task.FromResult(true); });
        Assert.True(explicitDelivered);

        var plainDelivered = await ProcessAsync(Post("reply-post-1", "  continue   this\ntask  ", Root), settings, store, tracker, seen,
            (request, _) => { delivered.Add(request); return Task.FromResult(true); });

        Assert.True(plainDelivered);
        Assert.Equal(2, delivered.Count);
        Assert.Equal("continue   this\ntask", delivered[1].Text);
        Assert.Equal(Root, delivered[1].ThreadId);
        Assert.Null(await store.FindAsync(Key(User)));
    }

    [Fact]
    public async Task Failed_or_rejected_explicit_delivery_does_not_seed_plain_replies()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        var tracker = new MattermostContinuationTracker();
        var seen = new BoundedSeenSet(20);
        var settings = Settings(User);

        var rejectedBot = await ProcessAsync(Post("root-post-1", "!cab start"), settings, store, tracker, seen,
            (_, _) => Task.FromResult(false));
        Assert.False(rejectedBot);
        var plainAfterBot = await ProcessAsync(Post("reply-post-1", "continue", Root), settings, store, tracker, seen,
            (_, _) => Task.FromResult(true));
        Assert.False(plainAfterBot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(Post("root-post-2", "!cab start"), settings,
            store, tracker, seen, (_, _) => throw new InvalidOperationException("delivery failed")));
        var plainAfterFailure = await ProcessAsync(Post("reply-post-2", "continue", "root-post-2"), settings, store, tracker, seen,
            (_, _) => Task.FromResult(true));
        Assert.False(plainAfterFailure);
    }

    [Fact]
    public async Task Plain_reply_continues_only_the_exact_persisted_owner_session()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        await SaveBindingAsync(store, Key(User), directory.Path);
        var tracker = new MattermostContinuationTracker();

        var request = await ResolveAsync(Post("reply-post-1", "  continue this  ", Root), Settings(User), store, tracker);
        Assert.NotNull(request);
        Assert.Equal("continue this", request.Text);
        Assert.Equal(Root, request.ThreadId);

        Assert.Null(await ResolveAsync(Post("reply-post-2", "continue", Root), Settings("user-2"), store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-3", "continue", Root, channel: "other-channel"), Settings(User), store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-4", "continue", Root, user: "user-2"), Settings("user-1,user-2"), store, tracker));
    }

    [Fact]
    public async Task Persisted_workspace_override_allows_an_exact_owner_after_reconnect()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        var key = Key(User);
        var settings = Settings(User) with { WorkspaceRoot = directory.Path, TrustedWorkspaceRoots = directory.Path };
        await store.ChangeWorkspaceAsync(key, directory.Path, settings);

        var request = await ResolveAsync(Post("reply-post-1", "continue", Root), settings, store,
            new MattermostContinuationTracker());

        Assert.NotNull(request);
        Assert.Equal("continue", request.Text);
    }

    [Fact]
    public async Task Plain_replies_reject_top_level_root_self_bot_webhook_and_unowned_legacy_sessions()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        await SaveBindingAsync(store, Key(User), directory.Path);
        var settings = Settings(User);
        var tracker = new MattermostContinuationTracker();

        Assert.Null(await ResolveAsync(Post("root-post-1", "continue"), settings, store, tracker));
        Assert.Null(await ResolveAsync(Post(Root, "continue", Root), settings, store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-1", "continue", Root, props: "{\"from_webhook\":true}"), settings, store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-2", "continue", Root, user: "bot-user"), settings, store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-3", "continue", "other-root"), settings, store, tracker));
    }

    [Fact]
    public async Task Plain_reply_does_not_adopt_an_unowned_legacy_session()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        await SaveBindingAsync(store, new SessionKey("mattermost", Channel, Root), directory.Path);

        Assert.Null(await ResolveAsync(Post("reply-post-1", "continue", Root), Settings(User), store,
            new MattermostContinuationTracker()));
    }

    [Fact]
    public async Task Empty_explicit_mentions_and_triggers_stay_ignored_inside_a_known_thread()
    {
        using var directory = new TemporaryDirectory();
        var tracker = new MattermostContinuationTracker();
        tracker.Remember(Key(User));
        var store = new SessionStore(directory.Path);

        Assert.Null(await ResolveAsync(Post("reply-post-1", "@cab-bot", Root), Settings(User), store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-2", "@cab-bot   ", Root), Settings(User), store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-3", "!cab", Root), Settings(User), store, tracker));
        Assert.Null(await ResolveAsync(Post("reply-post-4", "!cab   ", Root), Settings(User), store, tracker));
    }

    [Fact]
    public async Task Persisted_session_origin_must_match_the_current_mattermost_server()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        var key = ChatSessionScope.Create("mattermost", Channel, Root, User, "https://other.example.test");
        await SaveBindingAsync(store, key, directory.Path);

        var request = await ResolveAsync(Post("reply-post-1", "continue", Root), Settings(User), store,
            new MattermostContinuationTracker());

        Assert.Null(request);
    }

    [Fact]
    public async Task Access_policy_is_rechecked_before_session_lookup_and_memory_does_not_cross_users()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        var tracker = new MattermostContinuationTracker();
        var seen = new BoundedSeenSet(20);
        var delivered = await ProcessAsync(Post("root-post-1", "!cab start"), Settings("user-1,user-2"), store, tracker, seen,
            (_, _) => Task.FromResult(true));
        Assert.True(delivered);

        Assert.False(await ProcessAsync(Post("reply-post-1", "continue", Root), Settings("user-2"), store, tracker,
            new BoundedSeenSet(20), (_, _) => Task.FromResult(true)));
        Assert.False(await ProcessAsync(Post("reply-post-2", "continue", Root, user: "user-2"),
            Settings("user-1,user-2"), store, tracker, new BoundedSeenSet(20), (_, _) => Task.FromResult(true)));
        Assert.False(await ProcessAsync(Post("reply-post-3", "continue", Root), Settings("user-1") with
            { MattermostServerUrl = "https://other.example.test" }, store, tracker,
            new BoundedSeenSet(20), (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public void Run_tracker_evicts_oldest_session_when_capacity_is_reached()
    {
        var tracker = new MattermostContinuationTracker(capacity: 2);
        var first = Key("user-1", thread: "thread-1");
        var second = Key("user-1", thread: "thread-2");
        var third = Key("user-1", thread: "thread-3");

        tracker.Remember(first);
        tracker.Remember(second);
        tracker.Remember(third);

        Assert.False(tracker.Contains(first));
        Assert.True(tracker.Contains(second));
        Assert.True(tracker.Contains(third));
    }

    private static BridgeSettings Settings(string users) => new()
    {
        MattermostServerUrl = Server,
        MattermostChannelId = Channel,
        MattermostAllowedUserIds = users
    };

    private static SessionKey Key(string user, string thread = Root) =>
        ChatSessionScope.Create("mattermost", Channel, thread, user, Server);

    private static string Post(string id, string message, string root = "", string user = User,
        string channel = Channel, string? props = null)
    {
        var properties = props ?? "{}";
        return $$"""{"id":{{JsonSerializer.Serialize(id)}},"channel_id":{{JsonSerializer.Serialize(channel)}},"user_id":{{JsonSerializer.Serialize(user)}},"message":{{JsonSerializer.Serialize(message)}},"root_id":{{JsonSerializer.Serialize(root)}},"props":{{properties}}}""";
    }

    private static async Task<ChatRequest?> ResolveAsync(string json, BridgeSettings settings, SessionStore store,
        MattermostContinuationTracker tracker) => (await MattermostWebSocketTransport.ResolvePostCandidateAsync(
            json, settings, currentUserId: "bot-user", currentUsername: "cab-bot", store, tracker))?.Request;

    private static Task<bool> ProcessAsync(string json, BridgeSettings settings, SessionStore store,
        MattermostContinuationTracker tracker, BoundedSeenSet seen,
        Func<ChatRequest, CancellationToken, Task<bool>> deliver) => MattermostWebSocketTransport.ProcessPostAsync(
            json, settings, currentUserId: "bot-user", currentUsername: "cab-bot", store, tracker, seen, deliver);

    private static Task SaveBindingAsync(SessionStore store, SessionKey key, string workspace) => store.SaveAsync(new SessionBinding(
        key, Path.GetFullPath(workspace), "codex", "exec --json -", Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cab-mattermost-continuation-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
        }
    }
}
