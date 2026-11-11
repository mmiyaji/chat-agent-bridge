namespace ChatAgentBridge.Core.Tests;

public sealed class AllowedUserDirectoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-allowed-users-" + Guid.NewGuid().ToString("N"));

    public AllowedUserDirectoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task RecordsAreKeptPerProviderAndUserAndKeepKnownDetails()
    {
        var directory = new AllowedUserDirectory(_directory);
        var added = new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
        await directory.RecordAddedAsync(new AllowedUserRecord("slack", "U1", "Alice", added,
            AllowedUserDirectory.ChatRequestMethod, "pc-user", null, "C1", "1.0"));
        await directory.RecordAddedAsync(new AllowedUserRecord("discord", "U1", null, added,
            AllowedUserDirectory.SettingsMethod, "pc-user"));
        // A later settings save that does not know the name must not erase it.
        await directory.RecordAddedAsync(new AllowedUserRecord("slack", "U1", null, added.AddHours(1),
            AllowedUserDirectory.SettingsMethod, "pc-user"));
        await directory.RecordRequestAsync("slack", "U1", added.AddHours(2));
        await directory.RecordRequestAsync("slack", "U-unknown", added.AddHours(2));

        var records = await new AllowedUserDirectory(_directory).LoadAsync();
        Assert.Equal(2, records.Count);
        var slack = Assert.Single(records, record => record.Provider == "slack");
        Assert.Equal("Alice", slack.DisplayName);
        Assert.Equal("C1", slack.ChannelId);
        Assert.Equal(AllowedUserDirectory.SettingsMethod, slack.Method);
        Assert.Equal(added.AddHours(2), slack.LastRequestAt);

        await directory.RecordRemovedAsync("slack", "U1");
        Assert.Equal("discord", Assert.Single(await directory.LoadAsync()).Provider);
    }

    [Fact]
    public async Task DamagedOrInvalidEntriesAreIgnored()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "allowed-users.json"), "{ not json");
        Assert.Empty(await new AllowedUserDirectory(_directory).LoadAsync());

        await File.WriteAllTextAsync(Path.Combine(_directory, "allowed-users.json"),
            """[{"Provider":"teams","UserId":"x","AddedAt":"2026-11-07T00:00:00+00:00","Method":"settings"},{"Provider":"slack","UserId":"U2","AddedAt":"2026-11-07T00:00:00+00:00","Method":"settings"}]""");
        Assert.Equal("U2", Assert.Single(await new AllowedUserDirectory(_directory).LoadAsync()).UserId);
        await Assert.ThrowsAsync<ArgumentException>(() => new AllowedUserDirectory(_directory).RecordAddedAsync(
            new AllowedUserRecord("slack", "U3", null, DateTimeOffset.UtcNow, "unknown", null)));
    }

    [Fact]
    public async Task DisplayNamesMergeSavedAuditAndInboxMetadataByProviderAndUserId()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = new AllowedUserDirectory(_directory);
        await directory.RecordAddedAsync(new AllowedUserRecord("slack", "U_SHARED", "Saved Slack name", now,
            AllowedUserDirectory.SettingsMethod, "local"));
        await directory.RecordAddedAsync(new AllowedUserRecord("mattermost", "U_SHARED", null, now,
            AllowedUserDirectory.SettingsMethod, "local"));
        await directory.RecordAddedAsync(new AllowedUserRecord("discord", "U_NO_NAME", null, now,
            AllowedUserDirectory.SettingsMethod, "local"));

        var audit = new ChatAgentBridge.Core.History.AuditStore(_directory);
        await audit.AppendAsync(new ChatAgentBridge.Core.History.AuditEvent(now.AddMinutes(1), "allowed_user_added", null, "slack", "", "")
        {
            Access = new ChatAgentBridge.Core.History.AuditAccessSubject("U_SHARED", "Audit Slack name", "local", ChatAgentBridge.Core.History.AuditAccessSubject.SettingsMethod)
        });
        await audit.AppendAsync(new ChatAgentBridge.Core.History.AuditEvent(now.AddMinutes(2), "allowed_user_added", null, "mattermost", "", "")
        {
            Access = new ChatAgentBridge.Core.History.AuditAccessSubject("U_SHARED", "Mattermost audit name", "local", ChatAgentBridge.Core.History.AuditAccessSubject.SettingsMethod)
        });
        await audit.AppendAsync(new ChatAgentBridge.Core.History.AuditEvent(now.AddMinutes(3), "allowed_user_added", null, "discord", "", "")
        {
            Access = new ChatAgentBridge.Core.History.AuditAccessSubject("U_NO_NAME", null, "local", ChatAgentBridge.Core.History.AuditAccessSubject.SettingsMethod)
        });

        await new ChatAgentBridge.Core.History.InboxConversationStore(_directory).SaveAsync(
        [
            new(ChatAgentBridge.Core.Sessions.ChatSessionScope.Create("mattermost", "C1", "T1", "U_SHARED", "https://chat.example"),
                _directory, now.AddMinutes(4), "idle", 1, UserName: "Mattermost inbox name"),
            new(new ChatAgentBridge.Core.Sessions.SessionKey("slack", "C2", "T2", "U_AUDIT_ONLY", "slack"),
                _directory, now.AddMinutes(4), "idle", 1, UserName: "Inbox only name"),
            new(new ChatAgentBridge.Core.Sessions.SessionKey("slack", "C3", "T3", "U_SHARED", "slack"),
                _directory, now.AddMinutes(5), "idle", 1, UserName: "Inbox Slack name")
        ]);

        var names = await new AllowedUserDirectory(_directory).LoadDisplayNamesAsync();

        Assert.Equal("Saved Slack name", names[("slack", "U_SHARED")]);
        Assert.Equal("Mattermost audit name", names[("mattermost", "U_SHARED")]);
        Assert.Equal("Inbox only name", names[("slack", "U_AUDIT_ONLY")]);
        Assert.False(names.ContainsKey(("discord", "U_NO_NAME")));
    }

    [Fact]
    public async Task InvalidOptionalAuditAndInboxFilesDoNotDiscardSavedDisplayNames()
    {
        await new AllowedUserDirectory(_directory).RecordAddedAsync(new AllowedUserRecord("slack", "U1", "Saved name",
            DateTimeOffset.UtcNow, AllowedUserDirectory.SettingsMethod, "local"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "audit.json"), "{ not json");
        await File.WriteAllTextAsync(Path.Combine(_directory, "chat-conversations.json"), "{ not json");

        var names = await new AllowedUserDirectory(_directory).LoadDisplayNamesAsync();

        Assert.Equal("Saved name", names[("slack", "U1")]);
    }
}
