using System.Net;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports.Tests;

public sealed class ChatIdentityDirectoryDiscordTests
{
    private const string User = "111111111111111111";
    private const string Guild = "222222222222222222";
    private const string Channel = "333333333333333333";
    private const string Thread = "444444444444444444";

    [Fact]
    public async Task Discord_names_survive_restart_and_fresh_cache_requires_no_clients()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-discord-identities-" + Guid.NewGuid().ToString("N"));
        var settings = new BridgeSettings { DiscordBotToken = "synthetic-token", DiscordAllowedUserIds = User, DiscordAllowedGuildIds = Guild };
        var key = ChatSessionScope.Create("discord", Channel, Thread, User);
        try
        {
            var handler = new IdentityHandler();
            using (var directory = new ChatIdentityDirectory(handler, path))
            {
                var labels = (await directory.ResolveAsync(settings, [key]))[key];
                Assert.Equal("Bridge test server", labels.SiteName);
                Assert.Equal("agent-requests", labels.ChannelName);
                Assert.Equal("Alice", labels.UserName);
                Assert.Equal(3, handler.Calls);
                await directory.ResolveAsync(settings, [key]);
                Assert.Equal(3, handler.Calls);
            }
            using var restored = new ChatIdentityDirectory(path);
            var cached = (await restored.ReadCachedAsync([key]))[key];
            Assert.Equal("Bridge test server", cached.SiteName);
            Assert.Equal("Alice", cached.UserName);
            Assert.Equal(0, restored.CreatedHttpClientCount);
            Assert.DoesNotContain("synthetic-token", await File.ReadAllTextAsync(Path.Combine(path, "chat-identities.json")));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task Discord_identity_requests_never_follow_redirects()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-discord-identities-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new IdentityHandler { Redirect = true };
            using var directory = new ChatIdentityDirectory(handler, path);
            var key = ChatSessionScope.Create("discord", Channel, Thread, User);
            var labels = (await directory.ResolveAsync(new BridgeSettings
                { DiscordBotToken = "synthetic-token", DiscordAllowedUserIds = User, DiscordAllowedGuildIds = Guild }, [key]))[key];
            Assert.Null(labels.ChannelName);
            Assert.All(handler.Hosts, host => Assert.Equal("discord.com", host));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        public bool Redirect { get; init; }
        public int Calls { get; private set; }
        public List<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Hosts.Add(request.RequestUri!.Host);
            Assert.Equal("Bot", request.Headers.Authorization?.Scheme);
            Assert.Contains("DiscordBot", request.Headers.UserAgent.ToString());
            if (Redirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://unrelated.example/private");
                return Task.FromResult(redirect);
            }
            var json = request.RequestUri.AbsolutePath switch
            {
                "/api/v10/channels/" + Channel => $$"""{"id":"{{Channel}}","guild_id":"{{Guild}}","type":0,"name":"agent-requests"}""",
                "/api/v10/guilds/" + Guild => $$"""{"id":"{{Guild}}","name":"Bridge test server"}""",
                "/api/v10/users/" + User => $$"""{"id":"{{User}}","global_name":"Alice","username":"alice"}""",
                _ => throw new InvalidOperationException("Unexpected synthetic identity route")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
