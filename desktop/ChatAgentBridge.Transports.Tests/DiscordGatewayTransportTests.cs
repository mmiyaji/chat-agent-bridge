using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class DiscordGatewayTransportTests
{
    private const string ApiBase = "https://discord.test/api/v10/";

    [Fact]
    public void Parser_binds_top_level_and_thread_messages_to_logical_parent_and_source_channel()
    {
        using var root = JsonDocument.Parse("""{"id":"m1","channel_id":"parent","guild_id":"guild","author":{"id":"user"},"content":"<@!bot> inspect","attachments":[{"id":"a1","filename":"spec.txt","size":12,"content_type":"text/plain"}],"type":0}""");
        var top = DiscordGatewayTransport.ParseMessageRequest(root.RootElement, "bot");
        Assert.NotNull(top);
        Assert.Equal("parent", top.ChannelId);
        Assert.Equal("parent", top.ThreadId);
        Assert.Equal("parent", top.SourceChannelId);
        Assert.Equal("inspect", top.Text);
        Assert.Equal("guild", top.GuildId);
        Assert.Equal(new IncomingChatFile("a1", "spec.txt", 12, "text/plain"), Assert.Single(top.IncomingFiles));

        using var followup = JsonDocument.Parse("""{"id":"m2","channel_id":"thread","thread_id":"parent","guild_id":"guild","author":{"id":"user"},"content":"continue","type":0}""");
        var reply = DiscordGatewayTransport.ParseMessageRequest(followup.RootElement, "bot");
        Assert.NotNull(reply);
        Assert.Equal("parent", reply.ChannelId);
        Assert.Equal("thread", reply.ThreadId);
        Assert.Equal("thread", reply.SourceChannelId);
        Assert.Equal("continue", reply.Text);
    }

    [Fact]
    public void Parser_rejects_bots_webhooks_system_messages_and_untriggered_top_level_messages()
    {
        Assert.Null(Parse("""{"id":"1","channel_id":"c","author":{"id":"u"},"content":"plain","type":0}"""));
        Assert.Null(Parse("""{"id":"1","channel_id":"c","author":{"id":"u","bot":true},"content":"!cab run","type":0}"""));
        Assert.Null(Parse("""{"id":"1","channel_id":"c","author":{"id":"u"},"webhook_id":"w","content":"!cab run","type":0}"""));
        Assert.Null(Parse("""{"id":"1","channel_id":"c","author":{"id":"u"},"content":"!cab run","type":18}"""));
    }

    [Fact]
    public async Task Download_rechecks_exact_source_message_and_sends_no_bot_auth_to_allowlisted_cdn()
    {
        var api = new RecordingHandler(request =>
        {
            Assert.Equal("Bot token", request.Headers.Authorization?.ToString());
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v10/channels/parent" => Json(HttpStatusCode.OK, """{"id":"parent","guild_id":"456","type":0}"""),
                "/api/v10/channels/parent/messages/original" => Json(HttpStatusCode.OK, """{"id":"original","channel_id":"parent","author":{"id":"123"},"attachments":[{"id":"file","filename":"note.txt","size":5,"content_type":"text/plain","url":"https://cdn.discordapp.com/attachments/a/b/note.txt"}]}"""),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected source verification: {request.RequestUri}")
            };
        });
        var cdn = new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("cdn.discordapp.com", request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")) };
        });
        await using var transport = NewTransport(api, cdn);
        transport.UpdateAccessSettings(AllowedSettings());
        var request = Request(source: "parent");
        var files = await transport.ResolveIncomingFilesAsync(request, CancellationToken.None);
        Assert.Equal(new IncomingChatFile("file", "note.txt", 5, "text/plain"), Assert.Single(files));
        using var output = new MemoryStream();
        await transport.DownloadFileAsync(request, files[0], output, 10, CancellationToken.None);
        Assert.Equal("hello", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(4, api.Count);
        Assert.Equal(1, cdn.Count);
    }

    [Fact]
    public async Task Download_uses_the_thread_source_for_a_followup_and_verifies_its_parent()
    {
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v10/channels/thread" => Json(HttpStatusCode.OK, """{"id":"thread","parent_id":"parent","guild_id":"456","type":11}"""),
            "/api/v10/channels/thread/messages/original" => Json(HttpStatusCode.OK,
                """{"id":"original","channel_id":"thread","author":{"id":"123"},"attachments":[{"id":"file","filename":"reply.txt","size":4,"url":"https://cdn.discordapp.com/attachments/a/b/reply.txt"}]}"""),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected exact-source fetch: {request.RequestUri}")
        });
        var cdn = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("data")) });
        await using var transport = NewTransport(api, cdn);
        transport.UpdateAccessSettings(AllowedSettings());
        var request = Request(source: "thread");
        var files = await transport.ResolveIncomingFilesAsync(request, CancellationToken.None);
        Assert.Equal("reply.txt", Assert.Single(files).Name);
        using var output = new MemoryStream();
        await transport.DownloadFileAsync(request, files[0], output, 10, CancellationToken.None);
        Assert.Equal("data", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(4, api.Count);
    }

    [Fact]
    public async Task Download_rejects_cross_owner_and_untrusted_cdn_hosts()
    {
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v10/channels/parent" => Json(HttpStatusCode.OK, """{"id":"parent","guild_id":"456","type":0}"""),
            _ => Json(HttpStatusCode.OK,
                """{"id":"original","channel_id":"parent","author":{"id":"777"},"attachments":[{"id":"file","filename":"x","size":1,"url":"https://evil.example/file"}]}""")
        });
        var cdn = new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("untrusted CDN must not be contacted"));
        await using var transport = NewTransport(api, cdn);
        transport.UpdateAccessSettings(AllowedSettings());
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.DownloadFileAsync(Request(source: "parent"),
            new IncomingChatFile("file", "x", 1, null), output, 10, CancellationToken.None));
        Assert.Equal(0, cdn.Count);
    }

    [Fact]
    public async Task Upload_uses_multipart_and_disables_every_mention_type()
    {
        var api = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("multipart/form-data", request.Content!.Headers.ContentType!.ToString());
            string body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("payload_json", body);
            using var multipart = JsonDocument.Parse(ExtractPayloadJson(body));
            var allowedMentions = multipart.RootElement.GetProperty("allowed_mentions");
            Assert.Empty(allowedMentions.GetProperty("parse").EnumerateArray());
            Assert.Empty(allowedMentions.GetProperty("users").EnumerateArray());
            Assert.Empty(allowedMentions.GetProperty("roles").EnumerateArray());
            Assert.False(allowedMentions.GetProperty("replied_user").GetBoolean());
            Assert.Contains("files[0]", body);
            return Json(HttpStatusCode.OK, """{"attachments":[{"url":"https://cdn.discordapp.com/file"}]}""");
        });
        await using var transport = NewTransport(api, new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("no CDN access expected")));
        transport.UpdateAccessSettings(AllowedSettings());
        var file = Prepared("result.txt", Encoding.UTF8.GetBytes("result"));
        var links = await transport.UploadFilesAsync(Request(source: "parent"), [file], CancellationToken.None);
        Assert.Equal("https://cdn.discordapp.com/file", Assert.Single(links));
    }

    [Fact]
    public async Task Terminal_progress_edit_does_not_recreate_the_stop_button()
    {
        var api = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, """{"id":"progress-message"}""")
            : Json(HttpStatusCode.OK, "{}"));
        await using var transport = NewTransport(api, new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("No CDN request expected")));
        transport.UpdateAccessSettings(AllowedSettings());
        var request = Request(source: "thread");
        var progress = await ((IChatProgressTransport)transport).PostProgressAsync(request, "working", CancellationToken.None);
        await ((IChatProgressTransport)transport).UpdateProgressAsync(request, progress, "running", CancellationToken.None);
        await ((IChatProgressLifecycleTransport)transport).CompleteProgressAsync(progress, CancellationToken.None);
        await ((IChatProgressTransport)transport).UpdateProgressAsync(request, progress, "Failed", CancellationToken.None);

        string[] bodies = api.Bodies.ToArray();
        Assert.Equal(4, bodies.Length);
        using var removed = JsonDocument.Parse(bodies[2]);
        Assert.Empty(removed.RootElement.GetProperty("components").EnumerateArray());
        using var terminal = JsonDocument.Parse(bodies[3]);
        Assert.Equal("Failed", terminal.RootElement.GetProperty("content").GetString());
        Assert.Empty(terminal.RootElement.GetProperty("components").EnumerateArray());
    }

    [Fact]
    public async Task Gateway_identifies_with_required_intents_acks_button_within_loop_and_routes_owner_scoped_decision()
    {
        var socket = new FakeSocket();
        var api = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v10/gateway/bot") return Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}""");
            if (path == "/api/v10/channels/thread/messages" && request.Method == HttpMethod.Post)
                return Json(HttpStatusCode.OK, """{"id":"action-message"}""");
            if (path.StartsWith("/api/v10/interactions/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path.StartsWith("/api/v10/webhooks/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path == "/api/v10/channels/thread/messages/action-message" && request.Method == HttpMethod.Patch)
                return Json(HttpStatusCode.OK, "{}" );
            throw new Xunit.Sdk.XunitException($"Unexpected API request: {request.Method} {path}");
        });
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => socket, retryDelay: _ => TimeSpan.FromMilliseconds(5));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connected += () => ready.TrySetResult();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = transport.RunAsync(AllowedSettings(), (_, _) => Task.CompletedTask, lifetime.Token);
        socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        socket.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"},"application":{"id":"app"}}}""");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        string? customId = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionTransport = (IChatActionTransport)transport;
        var lease = await actionTransport.PostActionsAsync(Request(source: "thread"), "Approval", ChatActionKind.RequestApproval,
            (decision, _) => { Assert.Equal(ChatActionDecision.Approve, decision); posted.TrySetResult(); return Task.FromResult(ChatActionResult.Completed); }, lifetime.Token);
        // Extract the generated nonce from the captured JSON request.
        string outbound = await api.LastRequestBodyAsync();
        using (var body = JsonDocument.Parse(outbound))
            customId = body.RootElement.GetProperty("components")[0].GetProperty("components")[0].GetProperty("custom_id").GetString();
        Assert.StartsWith("cab1.", customId);
        void Click(string id, string actor = "123", string guild = "456", string channel = "thread", string message = "action-message") =>
            socket.Enqueue(JsonSerializer.Serialize(new
            {
                op = 0, s = 2, t = "INTERACTION_CREATE", d = new
                {
                    type = 3, id, token = $"token-{id}", guild_id = guild, channel_id = channel,
                    member = new { user = new { id = actor } }, message = new { id = message }, data = new { custom_id = customId }
                }
            }));
        Click("wrong-owner", actor: "777");
        Click("wrong-guild", guild: "999");
        Click("wrong-channel", channel: "other");
        Click("wrong-message", message: "other");
        Click("interaction");
        Click("replay");
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.DisposeAsync();
        string closedBody = await api.LastRequestBodyAsync();
        using (var closed = JsonDocument.Parse(closedBody))
        {
            Assert.Equal(CoreText.Get("DiscordActionClosed"), closed.RootElement.GetProperty("content").GetString());
            Assert.Empty(closed.RootElement.GetProperty("components").EnumerateArray());
        }
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(6, api.Requests.Count(request => request.RequestUri!.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal)));
        Assert.True(api.Requests.Count(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v10/webhooks/", StringComparison.Ordinal)) >= 4);
        Assert.Contains(api.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal));
        Assert.Contains(socket.Sent, value => value.Contains("\"op\":2", StringComparison.Ordinal) && value.Contains("37377", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gateway_heartbeats_and_resumes_after_a_socket_disconnect()
    {
        var first = new FakeSocket { AckHeartbeats = true, CloseAfterHeartbeatDelayMs = 200 };
        var second = new FakeSocket { AckHeartbeats = true };
        first.Enqueue("""{"op":10,"d":{"heartbeat_interval":100}}""");
        first.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
        first.Enqueue("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{"id":"dm-message","channel_id":"101","author":{"id":"123"},"content":"!cab inspect","type":0}}""");
        second.Enqueue("""{"op":10,"d":{"heartbeat_interval":100}}""");
        second.Enqueue("""{"op":0,"s":2,"t":"RESUMED","d":{}}""");
        int socketIndex = 0;
        var api = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}"""));
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => Interlocked.Increment(ref socketIndex) == 1 ? first : second,
            retryDelay: _ => TimeSpan.FromMilliseconds(5));
        var connectedCount = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connected += () => { if (Interlocked.Increment(ref connectedCount) > 1) reconnected.TrySetResult(); };
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatched = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = transport.RunAsync(AllowedSettings(), async (_, _) =>
        {
            Interlocked.Increment(ref dispatched);
            dispatchStarted.TrySetResult();
            await releaseDispatch.Task;
        }, lifetime.Token);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseDispatch.TrySetResult();
        await Task.Delay(50);
        Assert.Contains(first.Sent, value => value.Contains("\"op\":1", StringComparison.Ordinal));
        Assert.Contains(second.Sent, value => value.Contains("\"op\":6", StringComparison.Ordinal));
        Assert.Equal(1, dispatched);
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Continuation_tracker_keeps_pending_thread_owned_by_its_first_human()
    {
        var tracker = new DiscordContinuationTracker();
        var first = ChatSessionScope.Create("discord", "parent", "thread", "123");
        var second = ChatSessionScope.Create("discord", "parent", "thread", "777");
        Assert.False(tracker.IsOwnedByAnotherUser(first));
        tracker.Remember(first);
        Assert.True(tracker.IsOwnedByAnotherUser(second));
        Assert.False(tracker.IsOwnedByAnotherUser(first));
    }

    [Fact]
    public void Gateway_websocket_uses_system_proxy_except_for_loopback_fixtures()
    {
        using var proxiedSocket = new System.Net.WebSockets.ClientWebSocket();
        var proxied = proxiedSocket.Options;
        TransportHttp.ConfigureDiscordGatewayProxy(proxied, new Uri("wss://gateway.discord.gg/"));
        Assert.Same(HttpClient.DefaultProxy, proxied.Proxy);

        using var directSocket = new System.Net.WebSockets.ClientWebSocket();
        var direct = directSocket.Options;
        TransportHttp.ConfigureDiscordGatewayProxy(direct, new Uri("ws://127.0.0.1:43210/"));
        Assert.Null(direct.Proxy);
    }

    [Fact]
    public async Task Progress_edits_and_stop_remain_bound_to_the_thread_across_gateway_resume()
    {
        var first = new FakeSocket { AckHeartbeats = true, CloseAfterHeartbeatDelayMs = 200 };
        var second = new FakeSocket { AckHeartbeats = true };
        first.Enqueue("""{"op":10,"d":{"heartbeat_interval":100}}""");
        first.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"},"application":{"id":"app"}}}""");
        second.Enqueue("""{"op":10,"d":{"heartbeat_interval":100}}""");
        second.Enqueue("""{"op":0,"s":2,"t":"RESUMED","d":{}}""");
        int socketIndex = 0;
        var api = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v10/gateway/bot") return Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}""");
            if (path == "/api/v10/channels/thread/messages" && request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, """{"id":"progress-message"}""");
            if (path == "/api/v10/channels/thread/messages/progress-message" && request.Method == HttpMethod.Patch) return Json(HttpStatusCode.OK, "{}" );
            if (path.StartsWith("/api/v10/interactions/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (path.StartsWith("/api/v10/webhooks/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new Xunit.Sdk.XunitException($"Unexpected API request: {request.Method} {path}");
        });
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => Interlocked.Increment(ref socketIndex) == 1 ? first : second,
            retryDelay: _ => TimeSpan.FromMilliseconds(5));
        var connectedCount = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connected += () => { if (Interlocked.Increment(ref connectedCount) > 1) reconnected.TrySetResult(); };
        var stopped = new TaskCompletionSource<ChatRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = transport.RunAsync(AllowedSettings(), (request, _) => { if (request.CancellationTargetMessageId is not null) stopped.TrySetResult(request); return Task.CompletedTask; }, lifetime.Token);
        await WaitUntilAsync(() => connectedCount > 0, TimeSpan.FromSeconds(2));
        var progress = await ((IChatProgressTransport)transport).PostProgressAsync(Request(source: "thread"), "working", lifetime.Token);
        string body = await api.LastRequestBodyAsync();
        string stopId;
        using (var document = JsonDocument.Parse(body))
            stopId = document.RootElement.GetProperty("components")[0].GetProperty("components")[0].GetProperty("custom_id").GetString()!;
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ((IChatProgressTransport)transport).UpdateProgressAsync(Request(source: "thread"), progress, "still working", lifetime.Token);
        second.Enqueue(JsonSerializer.Serialize(new
        {
            op = 0, s = 3, t = "INTERACTION_CREATE", d = new
            {
                type = 3, id = "stop-interaction", token = "stop-token", guild_id = "456", channel_id = "thread",
                member = new { user = new { id = "123" } }, message = new { id = "progress-message" }, data = new { custom_id = stopId }
            }
        }));
        var stopRequest = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("original", stopRequest.CancellationTargetMessageId);
        Assert.Contains(api.Requests, request => request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath == "/api/v10/channels/thread/messages/progress-message");
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Denied_user_and_guild_cannot_create_a_public_thread()
    {
        var socket = new FakeSocket();
        socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        socket.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
        socket.Enqueue("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{"id":"denied-user","channel_id":"101","guild_id":"456","author":{"id":"777"},"content":"!cab inspect","type":0}}""");
        socket.Enqueue("""{"op":0,"s":3,"t":"MESSAGE_CREATE","d":{"id":"denied-guild","channel_id":"101","guild_id":"999","author":{"id":"123"},"content":"!cab inspect","type":0}}""");
        var api = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v10/gateway/bot") return Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}""");
            throw new Xunit.Sdk.XunitException($"A denied message must not make a Discord REST request: {request.RequestUri}");
        });
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => socket, retryDelay: _ => TimeSpan.FromMilliseconds(5));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = transport.RunAsync(AllowedSettings(), (_, _) => throw new Xunit.Sdk.XunitException("Denied request was dispatched"), lifetime.Token);
        await Task.Delay(100);
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(api.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Thread_permission_failure_surfaces_without_waiting_for_the_next_heartbeat()
    {
        var socket = new FakeSocket();
        socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        socket.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
        socket.Enqueue("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{"id":"start","channel_id":"101","guild_id":"456","author":{"id":"123"},"content":"!cab inspect","type":0}}""");
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v10/gateway/bot" => Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}"""),
            "/api/v10/channels/101" => Json(HttpStatusCode.OK, """{"id":"101","guild_id":"456","type":0}"""),
            _ when request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)
                => Json(HttpStatusCode.Forbidden, "{}"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected API request: {request.RequestUri}")
        });
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => socket, retryDelay: _ => TimeSpan.FromMilliseconds(5));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = transport.RunAsync(AllowedSettings(), (_, _) => Task.CompletedTask, lifetime.Token);
        var error = await Assert.ThrowsAsync<DiscordConfigurationException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("missing_permissions", error.Code);
    }

    [Fact]
    public async Task Restored_pending_thread_owner_is_deny_only_until_same_owner_explicitly_restarts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "discord-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sessionStore = new SessionStore(directory);
            var inbox = new InboxConversationStore(directory);
            await inbox.SaveAsync([new InboxConversationSnapshot(
                new SessionKey("discord", "111", "222", "123", "discord"), directory,
                DateTimeOffset.UtcNow, "pending", 1)]);

            var socket = new FakeSocket();
            socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
            socket.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
            socket.Enqueue("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{"id":"other-owner","channel_id":"222","thread_id":"111","guild_id":"456","author":{"id":"777"},"content":"!cab takeover","type":0}}""");
            socket.Enqueue("""{"op":0,"s":3,"t":"MESSAGE_CREATE","d":{"id":"same-owner-reply","channel_id":"222","thread_id":"111","guild_id":"456","author":{"id":"123"},"content":"continue","type":0}}""");
            socket.Enqueue("""{"op":0,"s":4,"t":"MESSAGE_CREATE","d":{"id":"same-owner-restart","channel_id":"222","thread_id":"111","guild_id":"456","author":{"id":"123"},"content":"!cab restart","type":0}}""");
            var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/v10/gateway/bot"
                ? Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}""")
                : throw new Xunit.Sdk.XunitException($"Unexpected API request: {request.RequestUri}"));
            await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
                socketFactory: _ => socket, sessions: sessionStore, retryDelay: _ => TimeSpan.FromMilliseconds(5));
            var handled = new ConcurrentQueue<ChatRequest>();
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var run = transport.RunAsync(AllowedSettings(), (request, _) => { handled.Enqueue(request); return Task.CompletedTask; }, lifetime.Token);
            await Task.Delay(150);
            lifetime.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            var onlyRequest = Assert.Single(handled);
            Assert.Equal("same-owner-restart", onlyRequest.MessageId);
            Assert.Equal("123", onlyRequest.UserId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Restored_owner_reservation_remains_deny_only_after_original_user_is_revoked()
    {
        string directory = Path.Combine(Path.GetTempPath(), "discord-revoked-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sessionStore = new SessionStore(directory);
            await new InboxConversationStore(directory).SaveAsync([new InboxConversationSnapshot(
                new SessionKey("discord", "111", "222", "123", "discord"), directory,
                DateTimeOffset.UtcNow, "interrupted", 1)]);
            var socket = new FakeSocket();
            socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
            socket.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
            socket.Enqueue("""{"op":0,"s":2,"t":"MESSAGE_CREATE","d":{"id":"takeover","channel_id":"222","thread_id":"111","guild_id":"456","author":{"id":"777"},"content":"!cab takeover","type":0}}""");
            socket.Enqueue("""{"op":0,"s":3,"t":"MESSAGE_CREATE","d":{"id":"revoked-owner","channel_id":"222","thread_id":"111","guild_id":"456","author":{"id":"123"},"content":"!cab restart","type":0}}""");
            var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/v10/gateway/bot"
                ? Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}""")
                : throw new Xunit.Sdk.XunitException($"Unexpected API request: {request.RequestUri}"));
            await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
                socketFactory: _ => socket, sessions: sessionStore, retryDelay: _ => TimeSpan.FromMilliseconds(5));
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var run = transport.RunAsync(AllowedSettings() with { DiscordAllowedUserIds = "777" },
                (_, _) => throw new Xunit.Sdk.XunitException("A revoked owner or another user took over a reserved thread"), lifetime.Token);
            await Task.Delay(150);
            lifetime.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(4007)]
    [InlineData(4009)]
    public async Task Invalid_gateway_session_close_codes_clear_session_before_identifying_again(int closeCode)
    {
        var first = new FakeSocket();
        first.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        first.Enqueue("""{"op":0,"s":1,"t":"READY","d":{"session_id":"session","resume_gateway_url":"wss://gateway.discord.gg","user":{"id":"bot"}}}""");
        first.EnqueueCloseCode(closeCode);
        var second = new FakeSocket();
        second.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        second.Enqueue("""{"op":0,"s":2,"t":"RESUMED","d":{}}""");
        int socketIndex = 0;
        var api = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}"""));
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => Interlocked.Increment(ref socketIndex) == 1 ? first : second,
            retryDelay: _ => TimeSpan.FromMilliseconds(5));
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedCount = 0;
        transport.Connected += () => { if (Interlocked.Increment(ref connectedCount) > 1) reconnected.TrySetResult(); };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = transport.RunAsync(AllowedSettings(), (_, _) => Task.CompletedTask, lifetime.Token);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(second.Sent, value => value.Contains("\"op\":2", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Sent, value => value.Contains("\"op\":6", StringComparison.Ordinal));
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(4010)]
    [InlineData(4011)]
    [InlineData(4012)]
    [InlineData(4013)]
    public async Task Invalid_gateway_configuration_close_codes_stop_retrying(int closeCode)
    {
        var socket = new FakeSocket();
        socket.Enqueue("""{"op":10,"d":{"heartbeat_interval":300000}}""");
        socket.EnqueueCloseCode(closeCode);
        int sockets = 0;
        var api = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"url":"wss://gateway.discord.gg"}"""));
        await using var transport = new DiscordGatewayTransport(new HttpClient(api), new Uri(ApiBase),
            socketFactory: _ => { Interlocked.Increment(ref sockets); return socket; },
            retryDelay: _ => TimeSpan.FromMilliseconds(5));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<DiscordConfigurationException>(() => transport.RunAsync(AllowedSettings(), (_, _) => Task.CompletedTask, lifetime.Token));
        Assert.Equal("gateway_configuration", error.Code);
        Assert.Equal(1, sockets);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "Condition was not reached before timeout");
    }

    private static ChatRequest? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DiscordGatewayTransport.ParseMessageRequest(document.RootElement, "bot");
    }

    private static DiscordGatewayTransport NewTransport(RecordingHandler api, RecordingHandler cdn) =>
        new(new HttpClient(api), new Uri(ApiBase), "token", cdnHttp: new HttpClient(cdn));

    private static BridgeSettings AllowedSettings() => new()
    {
        DiscordBotToken = "token",
        DiscordAllowedUserIds = "123",
        DiscordAllowedGuildIds = "456",
        DiscordAllowDirectMessages = true
    };

    private static ChatRequest Request(string source) => new("discord", "parent", "thread", "123", "inspect", "original")
    {
        GuildId = "456",
        SourceChannelId = source
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ExtractPayloadJson(string multipart)
    {
        int marker = multipart.IndexOf("payload_json", StringComparison.Ordinal);
        Assert.True(marker >= 0, "payload_json part should be present");
        int start = multipart.IndexOf("\r\n\r\n", marker, StringComparison.Ordinal) + 4;
        int end = multipart.IndexOf("\r\n--", start, StringComparison.Ordinal);
        Assert.True(start >= 4 && end > start, "payload_json part should be well-formed");
        return multipart[start..end];
    }

    private static PreparedFileAttachment Prepared(string name, byte[] bytes)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var constructor = typeof(PreparedFileAttachment).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, new[] { typeof(string), typeof(string), typeof(byte[]), typeof(string) }, modifiers: null)!;
        return (PreparedFileAttachment)constructor.Invoke(new object?[] { name, name, bytes, hash });
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();
        private readonly ConcurrentQueue<string> _bodies = new();
        internal int Count => _requests.Count;
        internal IReadOnlyCollection<HttpRequestMessage> Requests => _requests.ToArray();
        internal IReadOnlyCollection<string> Bodies => _bodies.ToArray();
        internal async Task<string> LastRequestBodyAsync()
        {
            await Task.Delay(10);
            return _bodies.Last();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(DiscordHttp.UserAgent, request.Headers.UserAgent.ToString());
            _requests.Enqueue(request);
            if (request.Content is not null) _bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class FakeSocket : IDiscordGatewaySocket
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        private readonly ConcurrentQueue<string> _sent = new();
        internal IReadOnlyCollection<string> Sent => _sent.ToArray();
        internal bool AckHeartbeats { get; init; }
        internal int CloseAfterHeartbeatDelayMs { get; init; }
        internal void Enqueue(string? value) => _incoming.Writer.TryWrite(value);
        internal void EnqueueCloseCode(int code) => _incoming.Writer.TryWrite($"__CLOSE:{code}__");
        internal void Close() => _incoming.Writer.TryWrite(null);
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            _sent.Enqueue(text);
            if (AckHeartbeats && text.Contains("\"op\":1", StringComparison.Ordinal))
            {
                _incoming.Writer.TryWrite("{\"op\":11}");
                if (CloseAfterHeartbeatDelayMs > 0)
                    _ = Task.Run(async () => { await Task.Delay(CloseAfterHeartbeatDelayMs); Close(); });
            }
            return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            string? value = await _incoming.Reader.ReadAsync(cancellationToken);
            if (value is not null && value.StartsWith("__CLOSE:", StringComparison.Ordinal) && value.EndsWith("__", StringComparison.Ordinal) &&
                int.TryParse(value.AsSpan(8, value.Length - 10), out int code)) throw new DiscordGatewayCloseException(code);
            return value;
        }
        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
