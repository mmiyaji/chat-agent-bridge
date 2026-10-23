using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatIdentityDirectoryTests
{
    private const string MmUser = "0123456789abcdefghijklmnop";
    private const string MmChannel = "1123456789abcdefghijklmnop";

    [Fact]
    public async Task Mattermost_resolves_cached_labels_once_per_user_and_channel_and_persists_only_labels()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/v4/users/{MmUser}" => Json($"{{\"id\":\"{MmUser}\",\"username\":\"alice\",\"nickname\":\"Alice Example\"}}"),
            $"/api/v4/channels/{MmChannel}" => Json($"{{\"id\":\"{MmChannel}\",\"type\":\"O\",\"display_name\":\"Release room\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var keyA = ChatSessionScope.Create("mattermost", MmChannel, "thread-1", MmUser, "https://chat.example.test/");
        var keyB = keyA with { ThreadId = "thread-2" };
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret-mm-token" };
        using (var directory = new ChatIdentityDirectory(handler, temp.Path))
        {
            var labels = await directory.ResolveAsync(settings, [keyA, keyB]);
            Assert.Equal(new ChatIdentityLabels("chat.example.test", "Release room", "@alice (Alice Example)", false), labels[keyA]);
            Assert.Equal(labels[keyA], labels[keyB]);
        }
        Assert.Equal(1, handler.CountContaining($"/users/{MmUser}"));
        Assert.Equal(1, handler.CountContaining($"/channels/{MmChannel}"));
        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "chat-identities.json"));
        Assert.Contains("Alice Example", json);
        Assert.DoesNotContain("secret-mm-token", json);
        Assert.DoesNotContain("/api/v4", json);

        using var offline = new ChatIdentityDirectory(new RecordingHandler(_ => throw new InvalidOperationException()), temp.Path);
        var cached = await offline.ReadCachedAsync([keyA]);
        Assert.Equal("Release room", cached[keyA].ChannelName);
        Assert.Equal("@alice (Alice Example)", cached[keyA].UserName);
    }

    [Fact]
    public async Task Mattermost_origin_mismatch_never_sends_the_configured_token()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => Json("{}"));
        var key = ChatSessionScope.Create("mattermost", MmChannel, "thread", MmUser, "https://one.example.test/");
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        await directory.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://two.example.test/", MattermostToken = "do-not-send" }, [key]);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Mattermost_lookup_rejects_redirects_and_proxy_handler_can_bypass()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.example.test/") } });
        var key = ChatSessionScope.Create("mattermost", MmChannel, "thread", MmUser, "https://chat.example.test/");
        using (var proxied = ChatIdentityDirectory.CreateHandler(bypassProxy: false)) Assert.True(proxied.UseProxy);
        using (var direct = ChatIdentityDirectory.CreateHandler(bypassProxy: true)) Assert.False(direct.UseProxy);
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        await directory.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "test", MattermostBypassProxy = true }, [key]);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Mattermost_keeps_stale_labels_when_refresh_fails_and_rejects_mismatched_response_ids()
    {
        using var temp = new TemporaryDirectory();
        var origin = ChatSessionScope.Create("mattermost", MmChannel, "t", MmUser, "https://chat.example.test/").Origin!;
        var old = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
        var seed = $$"""{"version":1,"entries":[{"key":{"provider":"mattermost","origin":"{{origin}}","kind":"user","id":"{{MmUser}}"},"siteName":"chat.example.test","channelName":null,"userName":"@old","isDirectMessage":false,"success":true,"checkedAtUtc":"{{old}}"},{"key":{"provider":"mattermost","origin":"{{origin}}","kind":"channel","id":"{{MmChannel}}"},"siteName":"chat.example.test","channelName":"Old room","userName":null,"isDirectMessage":false,"success":true,"checkedAtUtc":"{{old}}"}]}""";
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "chat-identities.json"), seed);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            $"/api/v4/users/{MmUser}" => Json($"{{\"id\":\"bad-id\",\"username\":\"evil\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        var key = ChatSessionScope.Create("mattermost", MmChannel, "thread", MmUser, "https://chat.example.test/");
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        var result = await directory.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "token" }, [key]);
        Assert.Equal("Old room", result[key].ChannelName);
        Assert.Equal("@old", result[key].UserName);
    }

    [Fact]
    public async Task Mattermost_direct_channel_is_labeled_semantically()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.Contains("/channels/", StringComparison.Ordinal)
            ? Json($"{{\"id\":\"{MmChannel}\",\"type\":\"D\",\"display_name\":\"opaque-channel-name\"}}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var key = new SessionKey("mattermost", MmChannel, "thread", null,
            ChatSessionScope.Create("mattermost", MmChannel, "thread", "u", "https://chat.example.test/").Origin);
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        var result = await directory.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "token" }, [key]);
        Assert.Equal("Direct message", result[key].ChannelName);
        Assert.True(result[key].IsDirectMessage);
        Assert.DoesNotContain("opaque-channel-name", await File.ReadAllTextAsync(Path.Combine(temp.Path, "chat-identities.json")));
    }

    [Fact]
    public async Task Slack_uses_auth_user_and_conversation_info_and_marks_direct_messages_semantically()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth.test" => Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Example workspace\"}"),
            "/api/users.info" => Json("{\"ok\":true,\"user\":{\"id\":\"U12345678\",\"name\":\"alice\",\"real_name\":\"Alice Example\",\"profile\":{\"display_name\":\"Alice\"}}}"),
            "/api/conversations.info" => Json("{\"ok\":true,\"channel\":{\"id\":\"D12345678\",\"is_im\":true}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var key = new SessionKey("slack", "D12345678", "1700000000.0", "U12345678", "slack");
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        var labels = await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "xoxb-secret" }, [key]);
        Assert.Equal("Example workspace", labels[key].SiteName);
        Assert.Equal("Direct message", labels[key].ChannelName);
        Assert.True(labels[key].IsDirectMessage);
        Assert.Equal("@alice (Alice Example)", labels[key].UserName);
        Assert.Equal(1, handler.CountContaining("/api/auth.test"));
        Assert.Equal(1, handler.CountContaining("/api/users.info"));
        Assert.Equal(1, handler.CountContaining("/api/conversations.info"));
        Assert.DoesNotContain("xoxb-secret", await File.ReadAllTextAsync(Path.Combine(temp.Path, "chat-identities.json")));
    }

    [Fact]
    public async Task Slack_success_and_negative_results_are_cached_and_corrupt_cache_is_empty()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth.test" => Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Workspace\"}"),
            "/api/users.info" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":false,\"error\":\"user_not_found\"}", Encoding.UTF8, "application/json") },
            "/api/conversations.info" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":false,\"error\":\"channel_not_found\"}", Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var key = new SessionKey("slack", "C12345678", "1700000000.0", "U12345678", "slack");
        using (var directory = new ChatIdentityDirectory(handler, temp.Path))
        {
            await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "xoxb-test" }, [key]);
            var requestCount = handler.Requests.Count;
            await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "xoxb-test" }, [key]);
            Assert.Equal(requestCount, handler.Requests.Count);
        }
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "chat-identities.json"), "not-json");
        using var corrupt = new ChatIdentityDirectory(new RecordingHandler(_ => throw new InvalidOperationException()), temp.Path);
        var result = await corrupt.ReadCachedAsync([key]);
        Assert.Equal(new ChatIdentityLabels(), result[key]);
    }

    [Fact]
    public async Task Resolve_batches_cache_writes_and_warm_cache_makes_no_requests()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth.test" => Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Workspace\"}"),
            "/api/users.info" => Json("{\"ok\":true,\"user\":{\"id\":\"U12345678\",\"name\":\"alice\"}}"),
            "/api/conversations.info" => Json("{\"ok\":true,\"channel\":{\"id\":\"C12345678\",\"name\":\"room\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var key = new SessionKey("slack", "C12345678", "t", "U12345678", "slack");
        using var directory = new ChatIdentityDirectory(handler, temp.Path);

        await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "token" }, [key]);
        Assert.Equal(1, directory.CacheWriteCount);
        var requestCount = handler.Requests.Count;
        await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "token" }, [key]);

        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(1, directory.CacheWriteCount);
    }

    [Fact]
    public async Task Cache_serialization_stays_within_reader_limit_for_long_japanese_labels()
    {
        using var temp = new TemporaryDirectory();
        const int maxCacheBytes = 1024 * 1024;
        var label = new string('あ', 128);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var rows = new List<object>();
        string SerializeRows() => JsonSerializer.Serialize(new { version = 1, entries = rows }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        object MakeRow(int index) => new
        {
            key = new { provider = "slack", origin = "slack", kind = "user", id = "U" + (10000000 + index).ToString() },
            siteName = label, channelName = label, userName = label,
            isDirectMessage = false, success = true, checkedAtUtc = now
        };
        object MakeTailRow(int index, string tailLabel) => new
        {
            key = new { provider = "slack", origin = "slack", kind = "user", id = "U" + (10000000 + index).ToString() },
            siteName = tailLabel, channelName = tailLabel, userName = tailLabel,
            isDirectMessage = false, success = true, checkedAtUtc = now
        };
        var emptySize = Encoding.UTF8.GetByteCount(SerializeRows());
        rows.Add(MakeRow(0));
        var rowSize = Encoding.UTF8.GetByteCount(SerializeRows()) - emptySize;
        rows.Clear();
        var rowCount = (maxCacheBytes - 4000 - emptySize) / rowSize;
        for (var index = 0; index < rowCount; index++) rows.Add(MakeRow(index));
        var bestTail = "";
        var bestSize = Encoding.UTF8.GetByteCount(SerializeRows());
        for (var length = 0; length <= 128; length++)
        {
            rows.Add(MakeTailRow(rowCount, new string('あ', length)));
            var candidateSize = Encoding.UTF8.GetByteCount(SerializeRows());
            rows.RemoveAt(rows.Count - 1);
            if (candidateSize <= maxCacheBytes - 100 && candidateSize > bestSize)
            {
                bestTail = new string('あ', length);
                bestSize = candidateSize;
            }
        }
        rows.Add(MakeTailRow(rowCount, bestTail));
        var seed = SerializeRows();
        Assert.InRange(Encoding.UTF8.GetByteCount(seed), 1, maxCacheBytes);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "chat-identities.json"), seed, Encoding.UTF8);

        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth.test" => Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Workspace\"}"),
            "/api/users.info" => Json("{\"ok\":true,\"user\":{\"id\":\"U99999999\",\"name\":\"alice\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var key = new SessionKey("slack", "C11111111", "t", "U99999999", "slack");
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "token" }, [key]);

        var cachePath = Path.Combine(temp.Path, "chat-identities.json");
        Assert.True(new FileInfo(cachePath).Length <= maxCacheBytes);
        var cached = await directory.ReadCachedAsync([key]);
        Assert.Equal("@alice", cached[key].UserName);
    }

    [Fact]
    public async Task Cancellation_persists_completed_partial_lookup_results()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new CancelOnSecondUserHandler();
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        var keys = new[]
        {
            new SessionKey("slack", "C12345678", "t", "U11111111", "slack"),
            new SessionKey("slack", "C12345678", "t", "U22222222", "slack")
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ResolveAsync(
            new BridgeSettings { SlackBotToken = "token" }, keys, cancellation.Token));

        using var offline = new ChatIdentityDirectory(new RecordingHandler(_ => throw new InvalidOperationException()), temp.Path);
        var cached = await offline.ReadCachedAsync(keys);
        Assert.Equal("@first", cached[keys[0]].UserName);
        Assert.Null(cached[keys[1]].UserName);
    }

    [Fact]
    public async Task Slack_rate_limit_cools_down_provider_and_workspace_switch_does_not_relabel_cached_channels()
    {
        using var temp = new TemporaryDirectory();
        var teamName = "First workspace";
        var hitLimit = false;
        var failOldChannel = false;
        var oldChannelId = "C12345678";
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth.test")
                return Json($"{{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"{teamName}\"}}");
            if (request.RequestUri.AbsolutePath == "/api/conversations.info")
            {
                var channelId = Uri.UnescapeDataString(request.RequestUri.Query.Split('=', 2)[1]);
                if (hitLimit) return new HttpResponseMessage((HttpStatusCode)429);
                if (failOldChannel && channelId == oldChannelId) return Json("{\"ok\":false,\"error\":\"channel_not_found\"}");
                return Json($"{{\"ok\":true,\"channel\":{{\"id\":\"{channelId}\",\"name\":\"room\"}}}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var oldKey = new SessionKey("slack", oldChannelId, "t1", null, "slack");
        var newKey = new SessionKey("slack", "C22222222", "t2", null, "slack");
        var settings = new BridgeSettings { SlackBotToken = "token" };
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        await directory.ResolveAsync(settings, [oldKey]);
        teamName = "Second workspace";
        await directory.ResolveAsync(settings, [newKey]);
        var cachePath = Path.Combine(temp.Path, "chat-identities.json");
        var cachedDocument = JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!;
        foreach (var entry in cachedDocument["entries"]!.AsArray())
            if (entry?["key"]?["id"]?.GetValue<string>() == oldKey.ChannelId)
                entry["checkedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
        await File.WriteAllTextAsync(cachePath, cachedDocument.ToJsonString());
        failOldChannel = true;
        await directory.ResolveAsync(settings, [oldKey]);
        var cached = await directory.ReadCachedAsync([oldKey, newKey]);
        Assert.Equal("First workspace", cached[oldKey].SiteName);
        Assert.Equal("Second workspace", cached[newKey].SiteName);

        hitLimit = true;
        var limitedKey = new SessionKey("slack", "C33333333", "t3", null, "slack");
        await directory.ResolveAsync(settings, [limitedKey]);
        var count = handler.Requests.Count;
        await directory.ResolveAsync(settings, [limitedKey]);
        Assert.Equal(count, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_lookup_work_is_bounded()
    {
        using var temp = new TemporaryDirectory();
        var cancelled = new CancellationToken(true);
        using var directory = new ChatIdentityDirectory(new RecordingHandler(_ => Json("{}")), temp.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ReadCachedAsync([], cancelled));

        var keys = Enumerable.Range(0, 30).Select(index => ChatSessionScope.Create("mattermost",
            index.ToString("D26"), $"thread-{index}", (index + 30).ToString("D26"), "https://chat.example.test/")).ToArray();
        var limitedHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var limited = new ChatIdentityDirectory(limitedHandler, temp.Path);
        await limited.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "token" }, keys);
        Assert.InRange(limitedHandler.Requests.Count, 1, 40);
    }

    [Fact]
    public async Task Concurrent_directory_instances_merge_cache_updates()
    {
        using var temp = new TemporaryDirectory();
        var keyOne = new SessionKey("slack", "C11111111", "t1", null, "slack");
        var keyTwo = new SessionKey("slack", "C22222222", "t2", null, "slack");
        RecordingHandler Handler(string team) => new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth.test") return Json($"{{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"{team}\"}}");
            if (request.RequestUri.AbsolutePath == "/api/conversations.info")
            {
                var id = Uri.UnescapeDataString(request.RequestUri.Query.Split('=', 2)[1]);
                return Json($"{{\"ok\":true,\"channel\":{{\"id\":\"{id}\",\"name\":\"{team} room\"}}}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var first = new ChatIdentityDirectory(Handler("One"), temp.Path);
        using var second = new ChatIdentityDirectory(Handler("Two"), temp.Path);
        await Task.WhenAll(
            first.ResolveAsync(new BridgeSettings { SlackBotToken = "token-one" }, [keyOne]),
            second.ResolveAsync(new BridgeSettings { SlackBotToken = "token-two" }, [keyTwo]));

        using var offline = new ChatIdentityDirectory(new RecordingHandler(_ => throw new InvalidOperationException()), temp.Path);
        var cached = await offline.ReadCachedAsync([keyOne, keyTwo]);
        Assert.Equal("One room", cached[keyOne].ChannelName);
        Assert.Equal("Two room", cached[keyTwo].ChannelName);
    }

    [Fact]
    public async Task Caller_cancellation_releases_resolve_gate_and_null_cache_rows_are_ignored()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth.test") return Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Workspace\"}");
            var id = Uri.UnescapeDataString(request.RequestUri.Query.Split('=', 2)[1]);
            return Json($"{{\"ok\":true,\"channel\":{{\"id\":\"{id}\",\"name\":\"room\"}}}}");
        });
        using var slowOnce = new CancelOnceHandler(handler);
        using var directory = new ChatIdentityDirectory(slowOnce, temp.Path);
        var key = new SessionKey("slack", "C12345678", "t", null, "slack");
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ResolveAsync(
            new BridgeSettings { SlackBotToken = "token" }, [key], cancellation.Token));
        var recovered = await directory.ResolveAsync(new BridgeSettings { SlackBotToken = "token" }, [key]);
        Assert.Equal("room", recovered[key].ChannelName);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "chat-identities.json"), "{\"version\":1,\"entries\":[null]}");
        var rows = await directory.ReadCachedAsync([key]);
        Assert.Equal(new ChatIdentityLabels(), rows[key]);
    }

    [Fact]
    public async Task Mattermost_site_name_is_cached_shared_and_refreshed_without_refetching_people_or_channels()
    {
        using var temp = new TemporaryDirectory();
        var siteName = "Company Chat";
        var failSite = false;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v4/config/client" => failSite ? new(HttpStatusCode.Forbidden) : Json($"{{\"SiteName\":\"{siteName}\",\"UnusedSetting\":\"not-retained\"}}"),
            $"/api/v4/users/{MmUser}" => Json($"{{\"id\":\"{MmUser}\",\"username\":\"alice\"}}"),
            $"/api/v4/channels/{MmChannel}" => Json($"{{\"id\":\"{MmChannel}\",\"type\":\"O\",\"display_name\":\"Release room\"}}"),
            _ => new(HttpStatusCode.NotFound)
        });
        var key = ChatSessionScope.Create("mattermost", MmChannel, "thread", MmUser, "https://chat.example.test/");
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "test-secret" };
        using var directory = new ChatIdentityDirectory(handler, temp.Path);
        Assert.Equal("Company Chat", (await directory.ResolveAsync(settings, [key]))[key].SiteName);
        Assert.Equal(3, handler.Requests.Count);
        await directory.ResolveAsync(settings, [key]);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Company Chat", (await directory.ReadCachedAsync([key]))[key].SiteName);

        var cachePath = Path.Combine(temp.Path, "chat-identities.json");
        async Task ExpireSiteAsync()
        {
            var cache = JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!;
            foreach (var entry in cache["entries"]!.AsArray())
                if (entry?["key"]?["kind"]?.GetValue<string>() == "site")
                    entry["checkedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
            await File.WriteAllTextAsync(cachePath, cache.ToJsonString());
        }
        await ExpireSiteAsync();
        siteName = "Renamed Company Chat";
        Assert.Equal(siteName, (await directory.ResolveAsync(settings, [key]))[key].SiteName);
        Assert.Equal(4, handler.Requests.Count); // A shared site rename updates fresh channel labels too.
        await ExpireSiteAsync();
        failSite = true;
        Assert.Equal(siteName, (await directory.ResolveAsync(settings, [key]))[key].SiteName);
        await directory.ResolveAsync(settings, [key]);
        Assert.Equal(5, handler.Requests.Count); // Failed site lookup is negatively cached.
        var serialized = await File.ReadAllTextAsync(cachePath);
        Assert.DoesNotContain("not-retained", serialized);
        Assert.DoesNotContain("test-secret", serialized);
    }

    [Fact]
    public async Task Mattermost_site_rate_limit_stops_other_requests_and_survives_directory_restart()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => new((HttpStatusCode)429));
        var key = ChatSessionScope.Create("mattermost", MmChannel, "thread", MmUser, "https://chat.example.test/");
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "test" };
        using (var directory = new ChatIdentityDirectory(handler, temp.Path))
            await directory.ResolveAsync(settings, [key]);
        Assert.Single(handler.Requests);
        var noRequests = new RecordingHandler(_ => throw new InvalidOperationException("Fresh cooldown must suppress network"));
        using var restarted = new ChatIdentityDirectory(noRequests, temp.Path);
        await restarted.ResolveAsync(settings, [key]);
        Assert.Empty(noRequests.Requests);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public int CountContaining(string path) => Requests.Count(request => request.RequestUri!.AbsolutePath.Contains(path, StringComparison.Ordinal));
        public Task<HttpResponseMessage> SendAsyncForTest(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri) { Headers = { Authorization = request.Headers.Authorization } });
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "identity-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private sealed class CancelOnceHandler(RecordingHandler inner) : HttpMessageHandler
    {
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await inner.SendAsyncForTest(request, cancellationToken);
        }
    }

    private sealed class CancelOnSecondUserHandler : HttpMessageHandler
    {
        private int _userCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth.test")
                return Json("{\"ok\":true,\"team_id\":\"T12345678\",\"team\":\"Workspace\"}");
            if (request.RequestUri.AbsolutePath == "/api/users.info" && Interlocked.Increment(ref _userCalls) == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var id = Uri.UnescapeDataString(request.RequestUri!.Query.Split('=', 2)[1]);
            return Json($"{{\"ok\":true,\"user\":{{\"id\":\"{id}\",\"name\":\"first\"}}}}");
        }
    }
}
