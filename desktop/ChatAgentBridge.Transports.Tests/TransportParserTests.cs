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
    public void Mattermost_captures_opaque_file_ids_only_for_triggered_posts_and_marks_excess_files()
    {
        var request = Mattermost("!cab inspect", fileIds: "[\"file-1\",\"file-2\"]");
        Assert.NotNull(request);
        Assert.Equal(new[] { "file-1", "file-2" }, request.IncomingFiles.Select(file => file.Id));
        Assert.All(request.IncomingFiles, file => Assert.Equal(string.Empty, file.Name));
        Assert.Null(Mattermost("no trigger", fileIds: "[\"file-1\"]")?.IncomingFiles.FirstOrDefault());

        var tooMany = Mattermost("!cab inspect", fileIds: "[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"]");
        Assert.Equal("too_many_attachments", tooMany?.IncomingFilesError);
        Assert.Equal(5, tooMany!.IncomingFiles.Count);
    }

    [Fact]
    public void Mattermost_accepts_authenticated_username_mentions_for_direct_and_thread_posts()
    {
        var direct = Mattermost("@cc-agent-bot summarize this", root: "", currentUsername: "cc-agent-bot");
        Assert.NotNull(direct);
        Assert.Equal("summarize this", direct.Text);
        Assert.Equal("post-1", direct.ThreadId);

        var thread = Mattermost("@cc-agent-bot  summarize this  ", root: "root-1", currentUsername: "cc-agent-bot");
        Assert.NotNull(thread);
        Assert.Equal("summarize this", thread.Text);
        Assert.Equal("root-1", thread.ThreadId);

        var redundantCommand = Mattermost("@cc-agent-bot !cab summarize this", currentUsername: "cc-agent-bot");
        Assert.NotNull(redundantCommand);
        Assert.Equal("summarize this", redundantCommand.Text);
    }

    [Fact]
    public void Mattermost_mentions_require_the_exact_leading_username_and_a_nonempty_prompt()
    {
        Assert.Null(Mattermost("@cc-agent-bot", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost("@cc-agent-bot   ", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost("@other-bot help", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost("@cc-agent-bot-extra help", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost("Please ask @cc-agent-bot help", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost("@cc-agent-bot !cab", currentUsername: "cc-agent-bot"));
        Assert.Equal("!cabinet run", Mattermost("@cc-agent-bot !cabinet run", currentUsername: "cc-agent-bot")?.Text);
        Assert.Null(Mattermost("@cc-agent-bot help", currentUsername: null));
    }

    [Fact]
    public void Mattermost_mentions_preserve_self_bot_webhook_and_channel_filters()
    {
        const string mention = "@cc-agent-bot run";
        Assert.Null(Mattermost(mention, user: "self", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost(mention, isBot: true, currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost(mention, props: "{\"from_webhook\":true}", currentUsername: "cc-agent-bot"));
        Assert.Null(Mattermost(mention, channel: "other-channel", currentUsername: "cc-agent-bot"));
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
    public void Slack_captures_file_share_metadata_only_after_trigger_filter_and_marks_excess_files()
    {
        using var allowed = JsonDocument.Parse("""{"type":"message","subtype":"file_share","channel":"C1","user":"U2","text":"!cab inspect","ts":"5.2","thread_ts":"5.0","files":[{"id":"F1","name":"notes.txt","size":12,"mimetype":"text/plain"}]}""");
        var request = SlackSocketModeTransport.ParseRequest(allowed.RootElement, "UBOT");
        Assert.Single(request!.IncomingFiles);
        Assert.Equal(new IncomingChatFile("F1", "notes.txt", 12, "text/plain"), request.IncomingFiles[0]);

        using var rejected = JsonDocument.Parse("""{"type":"message","subtype":"file_share","channel":"C1","user":"U2","text":"no trigger","ts":"5.3","thread_ts":"5.0","files":[{"id":"F2","name":"x","size":2}]}""");
        Assert.Null(SlackSocketModeTransport.ParseRequest(rejected.RootElement, "UBOT"));
        using var excess = JsonDocument.Parse("""{"type":"message","subtype":"file_share","channel":"C1","user":"U2","text":"!cab inspect","ts":"5.4","thread_ts":"5.0","files":[{"id":"1","name":"1","size":1},{"id":"2","name":"2","size":1},{"id":"3","name":"3","size":1},{"id":"4","name":"4","size":1},{"id":"5","name":"5","size":1},{"id":"6","name":"6","size":1}]}""");
        var excessRequest = SlackSocketModeTransport.ParseRequest(excess.RootElement, "UBOT");
        Assert.Equal("too_many_attachments", excessRequest?.IncomingFilesError);
        Assert.Equal(5, excessRequest!.IncomingFiles.Count);
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
        string? props = null,
        string? currentUsername = null,
        string? fileIds = null)
    {
        var propsJson = props ?? "{}";
        var filesJson = fileIds is null ? "" : $",\"file_ids\":{fileIds}";
        var json = $$"""{"id":"post-1","channel_id":"{{channel}}","user_id":"{{user}}","message":"{{text}}","root_id":"{{root}}","props":{{propsJson}}{{filesJson}}}""";
        return MattermostWebSocketTransport.ParsePost(json, "channel-1", "self", isBot, currentUsername);
    }
}
