using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostDirectMessagePolicyTests
{
    [Fact]
    public void Verified_direct_messages_bypass_shared_thread_scope_but_keep_user_allowlist()
    {
        var settings = new BridgeSettings
        {
            MattermostAllowDirectMessages = true,
            MattermostAllowedThreadIds = "shared-thread",
            MattermostAllowedUserIds = "allowed-user"
        };

        Assert.True(ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", "allowed-user", "dm-channel", "dm-root",
            isDirectMessage: true));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", "blocked-user", "dm-channel", "dm-root",
            isDirectMessage: true));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", "allowed-user", "shared-channel", "dm-root"));

        var disabled = settings with { MattermostAllowDirectMessages = false };
        Assert.False(ChatAccessPolicy.IsAllowedScope(disabled, "mattermost", "dm-channel", "dm-root", isDirectMessage: true));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(disabled, "mattermost", "allowed-user", "dm-channel", "dm-root",
            isDirectMessage: true));
    }

    [Fact]
    public async Task Verified_plain_direct_message_is_accepted_outside_the_shared_thread_allowlist()
    {
        using var directory = new TemporaryDirectory();
        var settings = new BridgeSettings
        {
            MattermostServerUrl = "https://chat.example.test",
            MattermostAllowDirectMessages = true,
            MattermostAllowedThreadIds = "shared-thread",
            MattermostAllowedUserIds = "allowed-user"
        };
        var json = """{"id":"post-1","channel_id":"dm-channel","user_id":"allowed-user","message":"plain DM request","root_id":"post-1","props":{}}""";

        var candidate = await MattermostWebSocketTransport.ResolvePostCandidateAsync(json, settings, "bot-user", null,
            new SessionStore(directory.Path), new MattermostContinuationTracker(), isDirectMessage: true);

        Assert.NotNull(candidate);
        Assert.True(candidate.IsExplicit);
        Assert.True(candidate.Request.IsDirectMessage);
    }

    [Fact]
    public void Access_approval_uses_dm_setting_without_applying_shared_thread_allowlist()
    {
        var settings = new BridgeSettings
        {
            MattermostServerUrl = "https://chat.example.test",
            MattermostAllowDirectMessages = true,
            MattermostAllowedThreadIds = "shared-thread"
        };

        Assert.True(ChatAccessPolicy.CanApproveAccessRequest(settings, settings, "mattermost", "dm-channel", "dm-root",
            guildId: null, isDirectMessage: true));
        Assert.False(ChatAccessPolicy.CanApproveAccessRequest(settings with { MattermostAllowDirectMessages = false },
            settings with { MattermostAllowDirectMessages = false }, "mattermost", "dm-channel", "dm-root",
            guildId: null, isDirectMessage: true));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-mm-dm-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
