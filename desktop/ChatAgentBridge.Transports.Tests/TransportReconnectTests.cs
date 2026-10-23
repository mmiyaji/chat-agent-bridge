using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class TransportReconnectTests
{
    [Fact]
    public async Task Mattermost_stop_reaction_targets_only_requester_and_only_while_progress_post_is_live()
    {
        var originalReady = new TaskCompletionSource<ChatRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressReady = new TaskCompletionSource<ChatProgressMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<ChatRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var ordinaryRequestCount = 0;
        var postCount = 0;
        var reactionBodies = new ConcurrentQueue<JsonDocument>();
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(HandleMattermostHttpAsync, HandleMattermostSocketAsync);
        await using var transport = new MattermostWebSocketTransport();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = new BridgeSettings
        {
            MattermostServerUrl = server.BaseUri.ToString(),
            MattermostChannelId = "channel-1",
            MattermostToken = "local-test-token",
            MattermostAllowedUserIds = "member-1,member-2",
            MattermostBypassProxy = true
        };

        var run = transport.RunAsync(settings, async (request, ct) =>
        {
            Interlocked.Increment(ref callbackCount);
            if (request.CancellationTargetMessageId is not null)
            {
                stopped.TrySetResult(request);
                return;
            }

            if (Interlocked.Increment(ref ordinaryRequestCount) == 1)
            {
                var completedProgress = await transport.PostProgressAsync(request, "Finished", ct);
                await transport.CompleteProgressAsync(completedProgress, ct);
                return;
            }

            originalReady.TrySetResult(request);
            progressReady.TrySetResult(await transport.PostProgressAsync(request, "Working", ct));
        }, cancellation.Token);

        var original = await originalReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        var stop = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);

        Assert.Equal(3, Volatile.Read(ref callbackCount));
        Assert.Equal("!cab stop", stop.Text);
        Assert.StartsWith("reaction:", stop.MessageId, StringComparison.Ordinal);
        Assert.Equal(original.ChannelId, stop.ChannelId);
        Assert.Equal(original.ThreadId, stop.ThreadId);
        Assert.Equal(original.UserId, stop.UserId);
        Assert.Equal(original.MessageId, stop.CancellationTargetMessageId);

        Assert.Equal(2, reactionBodies.Count);
        foreach (var body in reactionBodies)
        {
            using (body)
            {
                Assert.Equal("bot-1", body.RootElement.GetProperty("user_id").GetString());
                Assert.Equal("octagonal_sign", body.RootElement.GetProperty("emoji_name").GetString());
                Assert.Contains(body.RootElement.GetProperty("post_id").GetString(), new[] { "progress-1", "progress-2" });
            }
        }

        // The test socket sends a duplicate after the accepted reaction. It must not dispatch twice.
        await Task.Delay(100, cancellation.Token);
        Assert.Equal(3, Volatile.Read(ref callbackCount));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposeAsync();
        Assert.Contains(server.HttpRequests, item => item.Method == "DELETE" && item.Path == "/api/v4/users/bot-1/posts/progress-1/reactions/octagonal_sign");
        Assert.Contains(server.HttpRequests, item => item.Method == "DELETE" && item.Path == "/api/v4/users/bot-1/posts/progress-2/reactions/octagonal_sign");

        async Task HandleMattermostHttpAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/me")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"bot-1\",\"username\":\"cab-bot\",\"is_bot\":true}");
            else if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/member-1")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"member-1\",\"username\":\"member\",\"is_bot\":false}");
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/posts")
            {
                var index = Interlocked.Increment(ref postCount);
                var id = index == 1 ? "progress-1" : "progress-2";
                var rootId = index == 1 ? "incoming-1" : "incoming-2";
                await server!.WriteJsonAsync(context, HttpStatusCode.Created,
                    JsonSerializer.Serialize(new { id, channel_id = "channel-1", root_id = rootId }));
            }
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/reactions")
            {
                using var body = await JsonDocument.ParseAsync(context.Request.InputStream);
                reactionBodies.Enqueue(JsonDocument.Parse(body.RootElement.GetRawText()));
                var status = body.RootElement.GetProperty("post_id").GetString() == "progress-1"
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.Created;
                await server!.WriteJsonAsync(context, status, "{}");
            }
            else await server!.WriteJsonAsync(context, HttpStatusCode.NotFound, "{}");
        }

        async Task HandleMattermostSocketAsync(int connection, WebSocket socket, CancellationToken ct)
        {
            await ReadSocketMessageAsync(socket, ct);
            await SendSocketJsonAsync(socket, "{\"seq_reply\":1,\"status\":\"OK\"}", ct);
            var post = JsonSerializer.Serialize(new
            {
                id = "incoming-1", channel_id = "channel-1", user_id = "member-1", message = "!cab do work", root_id = ""
            });
            await SendSocketJsonAsync(socket, JsonSerializer.Serialize(new { @event = "posted", data = new { post } }), ct);
            var secondPost = JsonSerializer.Serialize(new
            {
                id = "incoming-2", channel_id = "channel-1", user_id = "member-1", message = "!cab do second", root_id = ""
            });
            await SendSocketJsonAsync(socket, JsonSerializer.Serialize(new { @event = "posted", data = new { post = secondPost } }), ct);
            await progressReady.Task.WaitAsync(ct);

            await SendReactionAsync("reaction_added", "{bad json");
            await SendReactionAsync("reaction_added", Reaction("thumbsup", "member-1", "progress-2", "channel-1"));
            await SendReactionAsync("reaction_removed", Reaction("octagonal_sign", "member-1", "progress-2", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "bot-1", "progress-2", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-2", "progress-2", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "progress-1", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "result-1", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "progress-2", "channel-1"), "channel-other");
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "progress-2", "channel-1"), 42);
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "progress-2", "channel-1"));
            await SendReactionAsync("reaction_added", Reaction("octagonal_sign", "member-1", "progress-2", "channel-1"));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            Task SendReactionAsync(string eventName, string reactionJson, object? broadcastChannel = null) =>
                SendSocketJsonAsync(socket, JsonSerializer.Serialize(new
                {
                    @event = eventName,
                    data = new { reaction = reactionJson },
                    broadcast = new { channel_id = broadcastChannel ?? "channel-1" }
                }), ct);

            static string Reaction(string emoji, string actor, string postId, string channelId) =>
                JsonSerializer.Serialize(new { emoji_name = emoji, user_id = actor, post_id = postId, channel_id = channelId, id = "reaction-1" });
        }
    }

    [Fact]
    public async Task Mattermost_stalled_seed_and_cleanup_stay_bounded_and_late_seed_is_removed()
    {
        var completed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteCount = 0;
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(HandleMattermostHttpAsync, HandleMattermostSocketAsync);
        await using var transport = new MattermostWebSocketTransport();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = transport.RunAsync(new BridgeSettings
        {
            MattermostServerUrl = server.BaseUri.ToString(),
            MattermostChannelId = "channel-1",
            MattermostToken = "local-test-token",
            MattermostAllowedUserIds = "member-1",
            MattermostBypassProxy = true
        }, async (request, ct) =>
        {
            var handle = await transport.PostProgressAsync(request, "Working", ct);
            var started = DateTime.UtcNow;
            await transport.CompleteProgressAsync(handle, ct);
            completed.TrySetResult(DateTime.UtcNow - started);
        }, cancellation.Token);

        var cleanupElapsed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(7), cancellation.Token);
        Assert.True(cleanupElapsed < TimeSpan.FromSeconds(3), $"Cleanup took {cleanupElapsed}.");
        await lateDelete.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation.Token);
        Assert.True(Volatile.Read(ref deleteCount) >= 2);

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        async Task HandleMattermostHttpAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/me")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"bot-1\",\"username\":\"cab-bot\",\"is_bot\":true}");
            else if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/member-1")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"member-1\",\"username\":\"member\",\"is_bot\":false}");
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/posts")
                await server!.WriteJsonAsync(context, HttpStatusCode.Created,
                    "{\"id\":\"progress-late\",\"channel_id\":\"channel-1\",\"root_id\":\"incoming-1\"}");
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/reactions")
            {
                _ = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(2300)); // Ignores client cancellation to model a late provider response.
                try { await server!.WriteJsonAsync(context, HttpStatusCode.Created, "{}"); }
                catch (HttpListenerException) { }
            }
            else if (context.Request.HttpMethod == "DELETE" && path == "/api/v4/users/bot-1/posts/progress-late/reactions/octagonal_sign")
            {
                if (Interlocked.Increment(ref deleteCount) == 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(2300)); // Ignores client cancellation to model stalled cleanup.
                    try { await server!.WriteJsonAsync(context, HttpStatusCode.NoContent, "{}"); }
                    catch (HttpListenerException) { }
                }
                else
                {
                    await server!.WriteJsonAsync(context, HttpStatusCode.NoContent, "{}");
                    lateDelete.TrySetResult();
                }
            }
            else await server!.WriteJsonAsync(context, HttpStatusCode.NotFound, "{}");
        }

        async Task HandleMattermostSocketAsync(int connection, WebSocket socket, CancellationToken ct)
        {
            await ReadSocketMessageAsync(socket, ct);
            await SendSocketJsonAsync(socket, "{\"seq_reply\":1,\"status\":\"OK\"}", ct);
            var post = JsonSerializer.Serialize(new
            {
                id = "incoming-1", channel_id = "channel-1", user_id = "member-1", message = "!cab work", root_id = ""
            });
            await SendSocketJsonAsync(socket, JsonSerializer.Serialize(new { @event = "posted", data = new { post } }), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    [Fact]
    public async Task Mattermost_ignores_cancellation_from_noncooperative_seed_handler_and_cleans_late_marker()
    {
        var seedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LateSeedMattermostHandler(seedStarted, releaseSeed, secondDelete);
        var http = new HttpClient(handler);
        await using var transport = new MattermostWebSocketTransport(http,
            new Uri("https://mattermost.test/"), "test-token", "channel-1", "bot-1");
        var request = new ChatRequest("mattermost", "channel-1", "thread-1", "member-1", "work", "incoming-1");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var progress = await transport.PostProgressAsync(request, "Working", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        timer.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Progress post took {timer.Elapsed}.");
        await seedStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await transport.CompleteProgressAsync(progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, handler.DeleteCount);
        releaseSeed.TrySetResult();
        await secondDelete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, handler.DeleteCount);
    }

    [Fact]
    public async Task Mattermost_dispose_during_run_reset_prevents_http_client_recreation()
    {
        var progressPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identityRequests = 0;
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(HandleMattermostHttpAsync, HandleMattermostSocketAsync);
        await using var transport = new MattermostWebSocketTransport();
        var settings = new BridgeSettings
        {
            MattermostServerUrl = server.BaseUri.ToString(),
            MattermostChannelId = "channel-1",
            MattermostToken = "local-test-token",
            MattermostAllowedUserIds = "member-1",
            MattermostBypassProxy = true
        };

        var firstRun = transport.RunAsync(settings, async (request, ct) =>
        {
            _ = await transport.PostProgressAsync(request, "Working", ct);
            progressPosted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, CancellationToken.None);
        await progressPosted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetRun = transport.RunAsync(settings, static (_, _) => Task.CompletedTask, CancellationToken.None);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = transport.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => resetRun);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref identityRequests));

        async Task HandleMattermostHttpAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/me")
            {
                Interlocked.Increment(ref identityRequests);
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"bot-1\",\"username\":\"cab-bot\",\"is_bot\":true}");
            }
            else if (context.Request.HttpMethod == "GET" && path == "/api/v4/users/member-1")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"member-1\",\"username\":\"member\",\"is_bot\":false}");
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/posts")
                await server!.WriteJsonAsync(context, HttpStatusCode.Created,
                    "{\"id\":\"progress-reset\",\"channel_id\":\"channel-1\",\"root_id\":\"incoming-1\"}");
            else if (context.Request.HttpMethod == "POST" && path == "/api/v4/reactions")
                await server!.WriteJsonAsync(context, HttpStatusCode.Created, "{}");
            else if (context.Request.HttpMethod == "DELETE" && path == "/api/v4/users/bot-1/posts/progress-reset/reactions/octagonal_sign")
            {
                deleteStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(2300)); // Ignore client cancellation during reset cleanup.
                try { await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{}"); }
                catch (HttpListenerException) { }
            }
            else await server!.WriteJsonAsync(context, HttpStatusCode.NotFound, "{}");
        }

        async Task HandleMattermostSocketAsync(int connection, WebSocket socket, CancellationToken ct)
        {
            await ReadSocketMessageAsync(socket, ct);
            await SendSocketJsonAsync(socket, "{\"seq_reply\":1,\"status\":\"OK\"}", ct);
            var post = JsonSerializer.Serialize(new
            {
                id = "incoming-1", channel_id = "channel-1", user_id = "member-1", message = "!cab work", root_id = ""
            });
            await SendSocketJsonAsync(socket, JsonSerializer.Serialize(new { @event = "posted", data = new { post } }), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    [Fact]
    public async Task Mattermost_reconnects_after_socket_close_deduplicates_posts_and_keeps_progress_handles_valid()
    {
        var accepted = new TaskCompletionSource<ChatRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<ChatProgressMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnecting = new TaskCompletionSource<ChatReconnectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        var connectedCount = 0;
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(HandleMattermostHttpAsync, HandleMattermostSocketAsync);
        await using var transport = new MattermostWebSocketTransport();
        transport.Connected += () => { if (Interlocked.Increment(ref connectedCount) == 2) secondConnected.TrySetResult(); };
        transport.ReconnectionStatusChanged += status =>
        {
            if (status.State == ChatReconnectionState.Reconnecting) reconnecting.TrySetResult(status);
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = new BridgeSettings
        {
            MattermostServerUrl = server.BaseUri.ToString(),
            MattermostChannelId = "channel-1",
            MattermostToken = "local-test-token",
            MattermostAllowedUserIds = "member-1",
            MattermostBypassProxy = true
        };
        var run = transport.RunAsync(settings, async (request, ct) =>
        {
            Interlocked.Increment(ref delivered);
            callbackEntered.TrySetResult();
            await continueCallback.Task.WaitAsync(ct);
            accepted.TrySetResult(request);
            progress.TrySetResult(await transport.PostProgressAsync(request, "Working", ct));
        }, cancellation.Token);

        var request = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        var handle = await progress.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        var retry = await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.NextRetryDelay);

        await transport.UpdateProgressAsync(request, handle, "Still working", cancellation.Token);
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        await Task.Delay(150, cancellation.Token);
        Assert.Equal(1, Volatile.Read(ref delivered));
        Assert.Contains(server.HttpRequests, item => item.Method == "PUT" && item.Path.EndsWith("/api/v4/posts/progress-1/patch", StringComparison.Ordinal));

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        async Task HandleMattermostHttpAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/api/v4/users/me")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"bot-1\",\"username\":\"cab-bot\",\"is_bot\":true}");
            else if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/api/v4/users/member-1")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{\"id\":\"member-1\",\"username\":\"member\",\"is_bot\":false}");
            else if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/api/v4/posts")
                await server!.WriteJsonAsync(context, HttpStatusCode.Created,
                    "{\"id\":\"progress-1\",\"channel_id\":\"channel-1\",\"root_id\":\"incoming-1\"}");
            else if (context.Request.HttpMethod == "PUT" && context.Request.Url?.AbsolutePath == "/api/v4/posts/progress-1/patch")
                await server!.WriteJsonAsync(context, HttpStatusCode.OK, "{}");
            else await server!.WriteJsonAsync(context, HttpStatusCode.NotFound, "{}");
        }

        async Task HandleMattermostSocketAsync(int connection, WebSocket socket, CancellationToken ct)
        {
            await ReadSocketMessageAsync(socket, ct); // Authentication challenge.
            await SendSocketJsonAsync(socket, "{\"seq_reply\":1,\"status\":\"OK\"}", ct);
            var post = JsonSerializer.Serialize(new
            {
                id = "incoming-1", channel_id = "channel-1", user_id = "member-1", message = "!cab do work", root_id = ""
            });
            var envelope = JsonSerializer.Serialize(new { @event = "posted", data = new { post } });
            await SendSocketJsonAsync(socket, envelope, ct);
            if (connection == 1)
            {
                await callbackEntered.Task.WaitAsync(ct);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ct);
                continueCallback.TrySetResult();
                return;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    [Fact]
    public async Task Mattermost_bad_credentials_fail_without_reconnect_retry()
    {
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(
            context => serverWriteUnauthorized(context),
            (_, socket, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await using var transport = new MattermostWebSocketTransport();
        var reconnects = 0;
        transport.ReconnectionStatusChanged += _ => Interlocked.Increment(ref reconnects);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.RunAsync(new BridgeSettings
        {
            MattermostServerUrl = server.BaseUri.ToString(), MattermostChannelId = "channel-1", MattermostToken = "bad-token",
            MattermostBypassProxy = true
        }, static (_, _) => Task.CompletedTask, cancellation.Token));

        Assert.DoesNotContain("bad-token", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref reconnects));
        Assert.Single(server.HttpRequests);

        Task serverWriteUnauthorized(HttpListenerContext context) => server!.WriteJsonAsync(context, HttpStatusCode.Unauthorized, "{}");
    }

    [Fact]
    public async Task Slack_refresh_reconnects_with_fresh_url_deduplicates_events_and_keeps_progress_handle_valid()
    {
        var accepted = new TaskCompletionSource<ChatRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<ChatProgressMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnecting = new TaskCompletionSource<ChatReconnectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        var connectedCount = 0;
        var openCount = 0;
        LoopbackServer? server = null;
        await using var serverLifetime = server = new LoopbackServer(
            context => WriteSlackHttpAsync(context), HandleSlackSocketAsync);
        var handler = new SlackApiHandler(() => Interlocked.Increment(ref openCount), server.BaseUri);
        using var http = new HttpClient(handler);
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "test-token",
            allowInsecureLoopbackSocketForTests: true);
        transport.Connected += () => { if (Interlocked.Increment(ref connectedCount) == 2) secondConnected.TrySetResult(); };
        transport.ReconnectionStatusChanged += status =>
        {
            if (status.State == ChatReconnectionState.Reconnecting) reconnecting.TrySetResult(status);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = transport.RunAsync(new BridgeSettings
        {
            SlackBotToken = "test-token", SlackAppToken = "test-app-token", SlackAllowedUserIds = "U1"
        }, async (request, ct) =>
        {
            Interlocked.Increment(ref delivered);
            accepted.TrySetResult(request);
            progress.TrySetResult(await transport.PostProgressAsync(request, "Working", ct));
        }, cancellation.Token);

        var request = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        var handle = await progress.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        var retry = await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.NextRetryDelay);
        await transport.UpdateProgressAsync(request, handle, "Still working", cancellation.Token);
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        await Task.Delay(150, cancellation.Token);

        Assert.Equal(1, Volatile.Read(ref delivered));
        Assert.Equal(2, Volatile.Read(ref openCount));
        Assert.Contains(handler.Requests, item => item.Method == "chat.update");
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        async Task WriteSlackHttpAsync(HttpListenerContext context) =>
            await server!.WriteJsonAsync(context, HttpStatusCode.NotFound, "{}");
        async Task HandleSlackSocketAsync(int connection, WebSocket socket, CancellationToken ct)
        {
            if (connection == 1)
            {
                await SendSocketJsonAsync(socket, "{\"type\":\"disconnect\",\"reason\":\"refresh_requested\"}", ct);
                return;
            }
            await SendSocketJsonAsync(socket, SlackEventEnvelope, ct);
            await ReadSocketMessageAsync(socket, ct); // Acknowledge the duplicate event.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    [Fact]
    public async Task Slack_link_disabled_is_terminal_and_does_not_reconnect()
    {
        var openCount = 0;
        await using var server = new LoopbackServer(_ => Task.CompletedTask,
            async (_, socket, ct) =>
            {
                await SendSocketJsonAsync(socket, "{\"type\":\"disconnect\",\"reason\":\"link_disabled\"}", ct);
                // Keep the transport open until the client has processed the terminal event.
                // Disposing the server socket immediately can abort the frame on Windows.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        using var http = new HttpClient(new SlackApiHandler(() => Interlocked.Increment(ref openCount), server.BaseUri));
        await using var transport = new SlackSocketModeTransport(http, new Uri("https://slack.test/api/"), "test-token",
            allowInsecureLoopbackSocketForTests: true);
        var reconnects = 0;
        transport.ReconnectionStatusChanged += _ => Interlocked.Increment(ref reconnects);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.RunAsync(new BridgeSettings
        {
            SlackBotToken = "test-token", SlackAppToken = "test-app-token"
        }, static (_, _) => Task.CompletedTask, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref openCount));
        Assert.Equal(0, Volatile.Read(ref reconnects));
    }

    private const string SlackEventEnvelope = """{"type":"events_api","envelope_id":"event-1","payload":{"event":{"type":"message","channel":"C1","user":"U1","text":"!cab do work","ts":"2.0","thread_ts":"1.0"}}}""";

    private static async Task SendSocketJsonAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReadSocketMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return "";
            content.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(content.ToArray());
    }

    private sealed class LateSeedMattermostHandler(TaskCompletionSource seedStarted, TaskCompletionSource releaseSeed,
        TaskCompletionSource secondDelete) : HttpMessageHandler
    {
        private int _deleteCount;
        public int DeleteCount => Volatile.Read(ref _deleteCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v4/posts")
                return Json(HttpStatusCode.Created, "{\"id\":\"progress-async\",\"channel_id\":\"channel-1\",\"root_id\":\"thread-1\"}");
            if (request.Method == HttpMethod.Post && path == "/api/v4/reactions")
            {
                seedStarted.TrySetResult();
                await releaseSeed.Task; // Deliberately ignores the supplied cancellation token.
                return Json(HttpStatusCode.Created, "{}");
            }
            if (request.Method == HttpMethod.Delete && path == "/api/v4/users/bot-1/posts/progress-async/reactions/octagonal_sign")
            {
                if (Interlocked.Increment(ref _deleteCount) >= 2) secondDelete.TrySetResult();
                return Json(HttpStatusCode.OK, "{}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SlackApiHandler(Action onOpen, Uri serverBase) : HttpMessageHandler
    {
        public List<(string Method, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path[(path.LastIndexOf('/') + 1)..], body));
            if (path.EndsWith("/auth.test", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"ok\":true,\"user_id\":\"UBOT\"}");
            if (path.EndsWith("/apps.connections.open", StringComparison.Ordinal))
            {
                onOpen();
                return Json(HttpStatusCode.OK, $$"""{"ok":true,"url":"ws://127.0.0.1:{{serverBase.Port}}/slack/socket"}""");
            }
            if (path.EndsWith("/chat.postMessage", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"progress-1\",\"channel\":\"C1\",\"message\":{\"thread_ts\":\"1.0\"}}");
            if (path.EndsWith("/chat.update", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"ok\":true}");
            return Json(HttpStatusCode.NotFound, "{}");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly Task _acceptLoop;
        private int _socketCount;

        public LoopbackServer(Func<HttpListenerContext, Task> handleHttp,
            Func<int, WebSocket, CancellationToken, Task> handleSocket) : this(handleHttp, handleSocket, GetPort()) { }

        private LoopbackServer(Func<HttpListenerContext, Task> handleHttp,
            Func<int, WebSocket, CancellationToken, Task> handleSocket, int port)
        {
            _baseUri = new Uri($"http://127.0.0.1:{port}/");
            _handleHttp = handleHttp;
            _handleSocket = handleSocket;
            _listener.Prefixes.Add(_baseUri.ToString());
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private readonly Uri _baseUri;
        private readonly Func<HttpListenerContext, Task> _handleHttp;
        private readonly Func<int, WebSocket, CancellationToken, Task> _handleSocket;
        public Uri BaseUri => _baseUri;
        public ConcurrentQueue<(string Method, string Path)> HttpRequests { get; } = new();

        public async Task WriteJsonAsync(HttpListenerContext context, HttpStatusCode status, string body)
        {
            HttpRequests.Enqueue((context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? ""));
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cancellation.Token);
            context.Response.Close();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token); }
                catch (OperationCanceledException) { return; }
                catch (HttpListenerException) when (_cancellation.IsCancellationRequested) { return; }
                _handlers.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (context.Request.IsWebSocketRequest)
                        {
                            var webSocket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                            using (webSocket) await _handleSocket(Interlocked.Increment(ref _socketCount), webSocket, _cancellation.Token);
                        }
                        else await _handleHttp(context);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
                    catch (HttpListenerException) when (_cancellation.IsCancellationRequested) { }
                }, _cancellation.Token));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Close();
            try { await _acceptLoop; } catch { }
            try { await Task.WhenAll(_handlers.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _cancellation.Dispose();
        }

        private static int GetPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
