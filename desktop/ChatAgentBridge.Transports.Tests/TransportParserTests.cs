using System.Text.Json;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class TransportParserTests
{
    [Fact]
    public void Mattermost_requires_explicit_trigger_and_matching_channel()
    {
        Assert.Null(Mattermost("Please help", channel: "channel-1"));
        Assert.Null(Mattermost("!cab help", channel: "other-channel"));
        Assert.Null(Mattermost("!cabinet help", channel: "channel-1"));

        var request = Mattermost("!cab fix the issue", channel: "channel-1");
        Assert.NotNull(request);
        Assert.Equal("fix the issue", request.Text);
        Assert.Equal("channel-1", request.ChannelId);
    }

    [Fact]
    public void Mattermost_skips_self_bots_and_webhook_posts()
    {
        Assert.Null(Mattermost("!cab run", user: "self"));
        Assert.Null(Mattermost("!cab run", isBot: true));
        Assert.Null(Mattermost("!cab run", props: "{\"from_webhook\":true}"));
        Assert.Null(Mattermost("!cab run", props: "{\"from_webhook\":\"true\"}"));
    }

    [Fact]
    public void Mattermost_empty_root_id_uses_post_id_and_nonempty_root_preserves_thread()
    {
        Assert.Equal("post-1", Mattermost("!cab run", root: "")?.ThreadId);
        Assert.Equal("root-1", Mattermost("!cab run", root: "root-1")?.ThreadId);
    }

    [Fact]
    public void Slack_accepts_app_mentions_and_only_triggered_or_mentioned_thread_messages()
    {
        using var mentionDoc = JsonDocument.Parse("""{"type":"app_mention","channel":"C1","user":"U2","text":"<@UBOT> summarize","ts":"1.2"}""");
        var mention = SlackSocketModeTransport.ParseRequest(mentionDoc.RootElement, "UBOT");
        Assert.NotNull(mention);
        Assert.Equal("summarize", mention.Text);
        Assert.Equal("1.2", mention.ThreadId);

        using var untriggeredDoc = JsonDocument.Parse("""{"type":"message","channel":"C1","user":"U2","text":"continue","ts":"2.3","thread_ts":"2.0"}""");
        Assert.Null(SlackSocketModeTransport.ParseRequest(untriggeredDoc.RootElement, "UBOT"));

        using var topLevelTriggerDoc = JsonDocument.Parse("""{"type":"message","channel":"C1","user":"U2","text":"!cab run","ts":"3.3"}""");
        Assert.Null(SlackSocketModeTransport.ParseRequest(topLevelTriggerDoc.RootElement, "UBOT"));

        using var triggeredDoc = JsonDocument.Parse("""{"type":"message","channel":"C1","user":"U2","text":"!cab run","ts":"4.3","thread_ts":"4.0"}""");
        var reply = SlackSocketModeTransport.ParseRequest(triggeredDoc.RootElement, "UBOT");
        Assert.NotNull(reply);
        Assert.Equal("4.0", reply.ThreadId);
        Assert.Equal("run", reply.Text);
    }

    [Fact]
    public void Slack_skips_own_user_and_bot_messages()
    {
        using var ownDoc = JsonDocument.Parse("""{"type":"app_mention","channel":"C1","user":"UBOT","text":"<@UBOT> ignore","ts":"1.2"}""");
        Assert.Null(SlackSocketModeTransport.ParseRequest(ownDoc.RootElement, "UBOT"));

        using var botDoc = JsonDocument.Parse("""{"type":"app_mention","channel":"C1","user":"U2","bot_id":"B2","text":"<@UBOT> ignore","ts":"1.3"}""");
        Assert.Null(SlackSocketModeTransport.ParseRequest(botDoc.RootElement, "UBOT"));
    }

    [Fact]
    public void Bounded_dedupe_rejects_recent_ids_and_evicts_oldest()
    {
        var seen = new BoundedSeenSet(2);
        Assert.True(seen.TryAdd("one"));
        Assert.True(seen.TryAdd("two"));
        Assert.False(seen.TryAdd("one"));
        Assert.True(seen.TryAdd("three"));
        Assert.True(seen.TryAdd("one"));
    }

    [Fact]
    public void Mattermost_url_allows_https_or_loopback_http_only()
    {
        Assert.Equal("https://chat.example/", TransportHttp.MattermostBaseUri("https://chat.example").ToString());
        Assert.Equal("http://localhost:8065/", TransportHttp.MattermostBaseUri("http://localhost:8065").ToString());
        Assert.Throws<InvalidOperationException>(() => TransportHttp.MattermostBaseUri("http://chat.example"));
    }

    private static ChatRequest? Mattermost(
        string text,
        string channel = "channel-1",
        string user = "user-1",
        string root = "root-1",
        bool isBot = false,
        string? props = null)
    {
        var propsJson = props ?? "{}";
        var json = $$"""{"id":"post-1","channel_id":"{{channel}}","user_id":"{{user}}","message":"{{text}}","root_id":"{{root}}","props":{{propsJson}}}""";
        return MattermostWebSocketTransport.ParsePost(json, "channel-1", "self", isBot);
    }
}
