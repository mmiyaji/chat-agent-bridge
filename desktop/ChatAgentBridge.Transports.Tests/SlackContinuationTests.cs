using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class SlackContinuationTests
{
    [Fact]
    public async Task Plain_reply_continues_exact_users_persisted_session()
    {
        using var directory = new TemporaryDirectory();
        var ownerKey = ChatSessionScope.Create("slack", "C1", "10.0", "U1");
        await SaveAsync(new SessionStore(directory.Path), ownerKey);

        using var evt = Event("U1", "C1", "10.2", "10.0", "continue this");
        var request = await SlackSocketModeTransport.ParseRequestAsync(evt.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path));

        Assert.NotNull(request);
        Assert.Equal("continue this", request.Text);
        Assert.Equal("10.0", request.ThreadId);
    }

    [Fact]
    public async Task Plain_reply_is_rejected_without_matching_allowed_user_session()
    {
        using var directory = new TemporaryDirectory();
        var store = new SessionStore(directory.Path);
        await SaveAsync(store, ChatSessionScope.Create("slack", "C1", "10.0", "U1"));

        using var otherUser = Event("U2", "C1", "10.3", "10.0", "continue");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(otherUser.RootElement, "UBOT", Settings("U1,U2"), new SessionStore(directory.Path)));

        using var unlistedUser = Event("U3", "C1", "10.4", "10.0", "continue");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(unlistedUser.RootElement, "UBOT", Settings("U1,U2"), new SessionStore(directory.Path)));

        using var unknownThread = Event("U1", "C1", "11.2", "11.0", "continue");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(unknownThread.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));
        using var wrongChannel = Event("U1", "C2", "10.7", "10.0", "continue");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(wrongChannel.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));

        using var rootMessage = JsonDocument.Parse("""{"type":"message","channel":"C1","user":"U1","text":"continue","ts":"12.0"}""");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(rootMessage.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));
        using var subtype = JsonDocument.Parse("""{"type":"message","subtype":"message_changed","channel":"C1","user":"U1","text":"continue","ts":"10.5","thread_ts":"10.0"}""");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(subtype.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));
        using var botMessage = JsonDocument.Parse("""{"type":"message","channel":"C1","user":"U1","bot_id":"B1","text":"continue","ts":"10.6","thread_ts":"10.0"}""");
        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(botMessage.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));
    }

    [Fact]
    public async Task Plain_reply_does_not_adopt_legacy_unowned_session()
    {
        using var directory = new TemporaryDirectory();
        await SaveAsync(new SessionStore(directory.Path), new SessionKey("slack", "C1", "10.0"));
        using var evt = Event("U1", "C1", "10.2", "10.0", "continue");

        Assert.Null(await SlackSocketModeTransport.ParseRequestAsync(evt.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path)));
    }

    [Fact]
    public async Task Explicit_trigger_keeps_working_without_saved_binding()
    {
        using var directory = new TemporaryDirectory();
        using var evt = Event("U1", "C1", "10.2", "10.0", "!cab run this");

        var request = await SlackSocketModeTransport.ParseRequestAsync(evt.RootElement, "UBOT", Settings("U1"), new SessionStore(directory.Path));

        Assert.NotNull(request);
        Assert.Equal("run this", request.Text);
    }

    private static BridgeSettings Settings(string users) => new() { SlackAllowedUserIds = users };

    private static JsonDocument Event(string user, string channel, string ts, string thread, string text) =>
        JsonDocument.Parse($$"""{"type":"message","channel":"{{channel}}","user":"{{user}}","text":"{{text}}","ts":"{{ts}}","thread_ts":"{{thread}}"}""");

    private static Task SaveAsync(SessionStore store, SessionKey key) => store.SaveAsync(new SessionBinding(
        key, Path.GetFullPath(Path.GetTempPath()), "codex", "exec --json -", Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cab-slack-test-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
        }
    }
}
