using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatProfileImageCacheTests
{
    private const string MmUser = "0123456789abcdefghijklmnop";
    private const string PngContentType = "image/png";

    [Fact]
    public async Task Mattermost_uses_only_the_configured_origin_and_caches_valid_image()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("https://chat.example.test/api/v4/users/" + MmUser + "/image", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("mm-secret", request.Headers.Authorization?.Parameter);
            return ImageResponse(Png());
        });
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://chat.example.test/");
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "mm-secret" };
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        var first = await cache.ResolveAsync(settings, key);
        var second = await cache.ResolveAsync(settings, key);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(1, cache.CreatedHttpClientCount);
        Assert.True(File.Exists(first));
        Assert.DoesNotContain("mm-secret", string.Join(" ", Directory.EnumerateFiles(Path.Combine(temp.Path, "avatar-cache"))));
    }

    [Fact]
    public async Task A_new_cache_instance_reuses_the_saved_image_without_network()
    {
        using var temp = new TemporaryDirectory();
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" };
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, settings.MattermostServerUrl);
        string? saved;
        using (var first = new ChatProfileImageCache(new RecordingHandler(_ => ImageResponse(Png())), temp.Path))
            saved = await first.ResolveAsync(settings, key);
        Assert.NotNull(saved);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Restart must use disk cache."));
        using var restarted = new ChatProfileImageCache(handler, temp.Path);
        Assert.Equal(saved, await restarted.ResolveAsync(settings, key));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, restarted.CreatedHttpClientCount);
    }

    [Fact]
    public async Task Cache_pruning_keeps_recently_accessed_images_within_the_count_limit()
    {
        using var temp = new TemporaryDirectory();
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" };
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, settings.MattermostServerUrl);
        string saved;
        using (var cache = new ChatProfileImageCache(new RecordingHandler(_ => ImageResponse(Png())), temp.Path))
            saved = Assert.IsType<string>(await cache.ResolveAsync(settings, key));

        var directory = Path.Combine(temp.Path, "avatar-cache");
        File.SetLastAccessTimeUtc(saved, DateTime.UtcNow.AddDays(-2));
        for (var index = 0; index < 600; index++)
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old-" + index))).ToLowerInvariant();
            var file = Path.Combine(directory, name + ".png");
            await File.WriteAllBytesAsync(file, Png());
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-1));
        }

        using (var cache = new ChatProfileImageCache(new RecordingHandler(_ => throw new InvalidOperationException()), temp.Path))
        {
            Assert.Equal(saved, await cache.ResolveAsync(settings, key));
            var newKey = ChatSessionScope.Create("mattermost", "other", "thread", "abcdef0123456789abcdef0123", settings.MattermostServerUrl);
            await cache.ResolveAsync(settings, newKey);
        }

        var remaining = Directory.EnumerateFiles(directory, "*.png").ToArray();
        Assert.True(remaining.Length <= 600);
        Assert.Contains(saved, remaining, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mattermost_origin_mismatch_never_sends_configured_token()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("unexpected network request"));
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://other.example.test/");
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        Assert.Null(await cache.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" }, key));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Slack_verifies_user_and_never_sends_bearer_to_avatar_host()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "slack.com")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("slack-secret", request.Headers.Authorization?.Parameter);
                return Json("""{"ok":true,"user":{"id":"U12345678","profile":{"image_72":"https://avatars.slack-edge.com/team/U12345678/avatar.png"}}}""");
            }
            Assert.Equal("avatars.slack-edge.com", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            return ImageResponse(Png());
        });
        var key = new SessionKey("slack", "C12345678", "thread", "U12345678", "slack");
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        var path = await cache.ResolveAsync(new BridgeSettings { SlackBotToken = "slack-secret" }, key);

        Assert.NotNull(path);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Slack_rejects_mismatched_user_and_untrusted_avatar_url_without_fetching_it()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("slack.com", request.RequestUri!.Host);
            return Json("""{"ok":true,"user":{"id":"U99999999","profile":{"image_72":"https://attacker.example/avatar.png"}}}""");
        });
        var key = new SessionKey("slack", "C12345678", "thread", "U12345678", "slack");
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        Assert.Null(await cache.ResolveAsync(new BridgeSettings { SlackBotToken = "secret" }, key));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Slack_does_not_fetch_an_untrusted_avatar_host_for_a_verified_user()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("slack.com", request.RequestUri!.Host);
            return Json("""{"ok":true,"user":{"id":"U12345678","profile":{"image_72":"https://attacker.example/avatar.png"}}}""");
        });
        var key = new SessionKey("slack", "C12345678", "thread", "U12345678", "slack");
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        Assert.Null(await cache.ResolveAsync(new BridgeSettings { SlackBotToken = "secret" }, key));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Discord_verifies_user_and_constructs_only_the_canonical_cdn_url()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "discord.com")
            {
                Assert.Equal("Bot", request.Headers.Authorization?.Scheme);
                Assert.Equal("discord-secret", request.Headers.Authorization?.Parameter);
                Assert.Equal("/api/v10/users/123456789012345678", request.RequestUri.AbsolutePath);
                return Json("""{"id":"123456789012345678","avatar":"0123456789abcdef"}""");
            }
            Assert.Equal("cdn.discordapp.com", request.RequestUri.Host);
            Assert.Equal("/avatars/123456789012345678/0123456789abcdef.png", request.RequestUri.AbsolutePath);
            Assert.Equal("size=64", request.RequestUri.Query.TrimStart('?'));
            Assert.Null(request.Headers.Authorization);
            return ImageResponse(Png());
        });
        var key = new SessionKey("discord", "123", "thread", "123456789012345678", "discord");
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        Assert.NotNull(await cache.ResolveAsync(new BridgeSettings { DiscordBotToken = "discord-secret" }, key));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirects_oversized_images_and_invalid_image_formats_are_not_cached()
    {
        using var temp = new TemporaryDirectory();
        var caseNumber = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref caseNumber) switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } },
            2 => ImageResponse(new byte[512 * 1024 + 1]),
            3 => ImageResponse(Encoding.UTF8.GetBytes("<svg></svg>"), "image/svg+xml"),
            _ => ImageResponse(Png(width: 2049))
        });
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://chat.example.test/");
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" };
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        Assert.Null(await cache.ResolveAsync(settings, key));
        SetFailureCacheExpired(temp.Path);
        Assert.Null(await cache.ResolveAsync(settings, key));
        SetFailureCacheExpired(temp.Path);
        Assert.Null(await cache.ResolveAsync(settings, key));
        SetFailureCacheExpired(temp.Path);
        Assert.Null(await cache.ResolveAsync(settings, key));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Path, "avatar-cache"), "*.png"));
    }

    [Fact]
    public async Task Cache_is_scoped_to_credentials_and_success_expires_after_24_hours()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => ImageResponse(Png()));
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://chat.example.test/");
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret-one" };
        using var cache = new ChatProfileImageCache(handler, temp.Path);

        var first = await cache.ResolveAsync(settings, key);
        Assert.NotNull(first);
        await cache.ResolveAsync(settings, key);
        Assert.Single(handler.Requests);

        var second = await cache.ResolveAsync(settings with { MattermostToken = "secret-two" }, key);
        Assert.NotNull(second);
        Assert.Equal(2, handler.Requests.Count);

        foreach (var path in Directory.EnumerateFiles(Path.Combine(temp.Path, "avatar-cache"), "*.png"))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-25));
        await cache.ResolveAsync(settings with { MattermostToken = "secret-two" }, key);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_does_not_leave_temporary_files()
    {
        using var temp = new TemporaryDirectory();
        using var entered = new ManualResetEventSlim();
        var handler = new RecordingHandler(async (_, token) =>
        {
            entered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageResponse(Png());
        });
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://chat.example.test/");
        using var cache = new ChatProfileImageCache(handler, temp.Path);
        using var cancellation = new CancellationTokenSource();
        var pending = cache.ResolveAsync(new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" }, key, cancellation.Token);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Path, "avatar-cache"), "*.tmp"));
    }

    [Fact]
    public async Task Timeout_is_bounded_and_negative_result_is_cached_for_five_minutes()
    {
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageResponse(Png());
        });
        var key = ChatSessionScope.Create("mattermost", "channel", "thread", MmUser, "https://chat.example.test/");
        using var cache = new ChatProfileImageCache(handler, temp.Path);
        var settings = new BridgeSettings { MattermostServerUrl = "https://chat.example.test/", MattermostToken = "secret" };

        Assert.Null(await cache.ResolveAsync(settings, key));
        Assert.Null(await cache.ResolveAsync(settings, key));
        Assert.Single(handler.Requests);

        SetFailureCacheExpired(temp.Path);
        Assert.Null(await cache.ResolveAsync(settings, key));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static byte[] Png(int width = 32, int height = 32)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        return bytes;
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes, string contentType = PngContentType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } }
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void SetFailureCacheExpired(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "avatar-cache"), "*.fail"))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-6));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri) { Headers = { Authorization = request.Headers.Authorization } });
            return await _respond(request, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChatProfileImageCacheTests", Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public void Dispose() { try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
