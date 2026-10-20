using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentConsentTests
{
    [Fact]
    public void FingerprintChangesWithExecutionSessionAndAttachmentPolicy()
    {
        var key = new SessionKey("slack", "C1", "T1", "U1");
        var settings = new BridgeSettings { WorkspaceRoot = Path.GetTempPath() };
        var initial = FileAttachmentPolicy.GetConsentFingerprint(key, "native-1", settings);
        Assert.NotEqual(initial, FileAttachmentPolicy.GetConsentFingerprint(key, "native-2", settings));
        Assert.NotEqual(initial, FileAttachmentPolicy.GetConsentFingerprint(key, "native-1", settings with { FileAttachmentMaxSizeMb = 11 }));
        Assert.NotEqual(initial, FileAttachmentPolicy.GetConsentFingerprint(key, "native-1", settings with { AgentExecutable = "other-agent" }));
        Assert.NotEqual(initial, FileAttachmentPolicy.GetConsentFingerprint(key with { ChannelId = "C2" }, "native-1", settings));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("always")]
    [InlineData("first")]
    [InlineData("auto")]
    public void NormalizesSupportedModes(string mode) => Assert.Equal(mode, FileAttachmentPolicy.NormalizeMode(mode.ToUpperInvariant()));

    [Fact]
    public void InvalidLimitsFailClosed()
    {
        var settings = new BridgeSettings { WorkspaceRoot = Path.GetTempPath(), FileAttachmentMaxCount = 11 };
        Assert.Throws<ArgumentOutOfRangeException>(() => FileAttachmentPolicy.Validate(settings));
        Assert.Throws<ArgumentException>(() => FileAttachmentPolicy.NormalizeMode("sometimes"));
    }

    [Fact]
    public async Task ConsentIsScopedToSessionAndCanBeRevoked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-consent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AttachmentConsentStore(directory);
            var key = new SessionKey("mattermost", "channel", "thread", "user", "origin");
            const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            Assert.False(await store.IsApprovedAsync(key, "session-1", fingerprint));
            await store.SaveAsync(key, "session-1", fingerprint);
            Assert.True(await store.IsApprovedAsync(key, "session-1", fingerprint));
            Assert.False(await store.IsApprovedAsync(key, "session-2", fingerprint));
            Assert.False(await store.IsApprovedAsync(key with { ThreadId = "other" }, "session-1", fingerprint));
            await store.RevokeAsync(key, "session-1", fingerprint);
            Assert.False(await store.IsApprovedAsync(key, "session-1", fingerprint));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InvalidConsentStoreFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-consent-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "attachment-consents.json"), "{ invalid");
            var store = new AttachmentConsentStore(directory);
            var key = new SessionKey("slack", "C", "T");
            const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            Assert.False(await store.IsApprovedAsync(key, "session", fingerprint));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(key, "session", fingerprint));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
