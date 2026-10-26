using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class InboxConversationStoreTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 24, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task SaveAndList_RoundTripsOnlyMetadataAndRestoresActiveStatesAsInterrupted()
    {
        var directory = NewDirectory();
        try
        {
            var store = new InboxConversationStore(directory);
            var snapshots = new[]
            {
                Snapshot(state: "pending", requestCount: 2, isDirectMessage: true,
                    userName: "alice", channelName: "Bridge chat", siteName: "Test workspace"),
                Snapshot(thread: "running-thread", state: "running", displayName: "Bot"),
                Snapshot(thread: "done-thread", state: "completed", requestCount: 4, siteName: "Mattermost")
            };

            await store.SaveAsync(snapshots);
            var restored = await new InboxConversationStore(directory).ListAsync();

            Assert.Equal(new[] { "interrupted", "interrupted", "completed" }, restored.Select(item => item.State));
            Assert.Equal(snapshots[0] with { State = "interrupted" }, restored[0]);
            Assert.Equal(snapshots[1] with { State = "interrupted" }, restored[1]);
            Assert.Equal(snapshots[2], restored[2]);
            Assert.Equal(Path.GetFullPath(directory), store.DirectoryPath);

            var json = await File.ReadAllTextAsync(Path.Combine(directory, "chat-conversations.json"));
            Assert.Contains("\"SchemaVersion\": 1", json);
            Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("output", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SaveAndList_PreservesDistinctOwnerAndMattermostOriginScopes()
    {
        var directory = NewDirectory();
        try
        {
            var mattermost = Snapshot(new SessionKey("mattermost", "channel", "thread", "owner", new string('a', 64)), "completed");
            var otherServer = mattermost with { Key = mattermost.Key with { Origin = new string('b', 64) } };
            var otherOwner = mattermost with { Key = mattermost.Key with { UserId = "another-owner" } };

            await new InboxConversationStore(directory).SaveAsync([mattermost, otherServer, otherOwner]);
            var restored = await new InboxConversationStore(directory).ListAsync();

            Assert.Equal(3, restored.Count);
            Assert.Equal(3, restored.Select(item => item.Key).Distinct().Count());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SaveAndList_PreservesDiscordIdentityMetadataAndRejectsUnknownProviders()
    {
        var directory = NewDirectory();
        try
        {
            var key = new SessionKey("discord", "323456789012345678", "423456789012345678", "123456789012345678", "discord");
            var row = Snapshot(key, state: "completed");
            await new InboxConversationStore(directory).SaveAsync([row]);
            Assert.Equal(row, Assert.Single(await new InboxConversationStore(directory).ListAsync()));
            Assert.False(InboxConversationStore.IsValidSnapshot(row with { Key = key with { Provider = "random" } }));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SaveAsync_RetainsNewestFiveHundredRows()
    {
        var directory = NewDirectory();
        try
        {
            var rows = Enumerable.Range(0, 520)
                .Select(index => Snapshot(thread: $"thread-{index}", updatedAt: Timestamp.AddMinutes(index)))
                .ToArray();

            await new InboxConversationStore(directory).SaveAsync(rows);
            var restored = await new InboxConversationStore(directory).ListAsync();

            Assert.Equal(500, restored.Count);
            Assert.Equal("thread-519", restored[0].Key.ThreadId);
            Assert.DoesNotContain(restored, item => item.Key.ThreadId == "thread-0");
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RetentionPrioritizesOldActiveRowsAndMaintenanceKeepsTheirPersistedState()
    {
        var directory = NewDirectory();
        try
        {
            await new ChatAgentBridge.Core.LogRetentionPreferencesStore(directory).SaveAsync(
                new ChatAgentBridge.Core.LogRetentionPreferences { ConversationRetentionDays = 1 });
            var oldActive = Snapshot(thread: "old-active", state: "running", updatedAt: DateTimeOffset.UtcNow.AddDays(-365));
            var recentClosed = Enumerable.Range(0, 500)
                .Select(index => Snapshot(thread: $"recent-{index}", state: "completed", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-index)))
                .ToArray();
            var store = new InboxConversationStore(directory);
            await store.SaveAsync([oldActive, .. recentClosed]);
            await store.ApplyRetentionAsync();

            var restored = await store.ListAsync();
            Assert.Contains(restored, row => row.Key.ThreadId == "old-active" && row.State == "interrupted");
            Assert.Equal(500, restored.Count);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "chat-conversations.json"));
            Assert.Contains("\"State\": \"running\"", json);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SaveAsync_RejectsOversizedAndInvalidCountsWithoutChangingGoodFile()
    {
        var directory = NewDirectory();
        try
        {
            var store = new InboxConversationStore(directory);
            await store.SaveAsync([Snapshot(state: "completed")]);
            var path = Path.Combine(directory, "chat-conversations.json");
            var original = await File.ReadAllBytesAsync(path);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([Snapshot(thread: "replace")], canceled.Token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([Snapshot(requestCount: -1)]));

            var longRoot = Path.GetPathRoot(directory)! + string.Concat(Enumerable.Repeat("界", 2040));
            var oversized = Enumerable.Range(0, 500)
                .Select(index => Snapshot(thread: $"large-{index}", workspaceRoot: longRoot))
                .ToArray();
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(oversized));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void SelectValidSnapshots_KeepsFirstValidPerKeyAndCountsInvalidRows()
    {
        var valid = Snapshot(state: "completed", requestCount: 7, displayName: "Kept", userName: "alice",
            channelName: "Bridge chat", siteName: "Workspace", isDirectMessage: true);
        var duplicate = valid with { DisplayName = "Discarded" };
        var invalidWorkspace = valid with { WorkspaceRoot = "relative/path" };
        var invalidCount = Snapshot(thread: "bad-count", requestCount: -1);
        var invalidIdentity = Snapshot(thread: "bad-identity") with
            { Key = new SessionKey("slack", "", "bad-identity", "user", "slack") };

        var selection = InboxConversationStore.SelectValidSnapshots(
            [valid, invalidWorkspace, invalidCount, invalidIdentity, duplicate]);

        Assert.Equal(4, selection.RejectedCount);
        var selected = Assert.Single(selection.Entries);
        Assert.Equal(valid, selected);
        Assert.True(InboxConversationStore.IsValidSnapshot(valid));
        Assert.False(InboxConversationStore.IsValidSnapshot(invalidWorkspace));
    }

    [Fact]
    public async Task SelectedValidRowsCanBeSavedAndRestoredDespiteOneRejectedRow()
    {
        var directory = NewDirectory();
        try
        {
            var selection = InboxConversationStore.SelectValidSnapshots([
                Snapshot(thread: "valid-pending", state: "pending"),
                Snapshot(thread: "bad-relative", workspaceRoot: "relative-folder"),
                Snapshot(thread: "valid-completed", state: "completed")]);
            Assert.Equal(1, selection.RejectedCount);
            await new InboxConversationStore(directory).SaveAsync(selection.Entries);
            var restored = await new InboxConversationStore(directory).ListAsync();
            Assert.Equal(2, restored.Count);
            Assert.Contains(restored, row => row.Key.ThreadId == "valid-pending" && row.State == "interrupted");
            Assert.Contains(restored, row => row.Key.ThreadId == "valid-completed" && row.State == "completed");
            Assert.DoesNotContain(restored, row => row.Key.ThreadId == "bad-relative");
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task SelectValidSnapshots_AllInvalidReturnsEmptyAndLeavesExistingFileUntouched()
    {
        var directory = NewDirectory();
        try
        {
            var store = new InboxConversationStore(directory);
            await store.SaveAsync([Snapshot(state: "completed")]);
            var path = Path.Combine(directory, "chat-conversations.json");
            var original = await File.ReadAllBytesAsync(path);

            var selection = InboxConversationStore.SelectValidSnapshots(
                [Snapshot(workspaceRoot: "relative"), Snapshot(requestCount: -1)]);

            Assert.Empty(selection.Entries);
            Assert.Equal(2, selection.RejectedCount);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":2,\"Entries\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Entries\":[],\"Unexpected\":true}")]
    [InlineData("""{"SchemaVersion":1,"Entries":[{"Key":{"Provider":"slack","ChannelId":"c","ThreadId":"t","UserId":"u","Origin":"slack","Unexpected":1},"WorkspaceRoot":"C:\\","UpdatedAt":"2026-10-24T01:02:03+00:00","State":"idle","RequestCount":0}]}""")]
    [InlineData("""{"SchemaVersion":1,"Entries":[{"Key":{"Provider":"slack","ChannelId":"c","ThreadId":"t","UserId":"u","Origin":"slack"},"WorkspaceRoot":"C:\\","UpdatedAt":"2026-10-24T01:02:03+00:00","State":"idle","RequestCount":0,"DisplayName":"bad\u0001name"}]}""")]
    public async Task ListAsync_FailsClosedOnCorruptSchemaUnknownFieldsOrInvalidMetadata(string contents)
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "chat-conversations.json"), contents);
            await Assert.ThrowsAsync<InvalidDataException>(() => new InboxConversationStore(directory).ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ListAsync_RejectsOversizedFileBeforeDeserializing()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "chat-conversations.json"), new byte[2 * 1024 * 1024 + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => new InboxConversationStore(directory).ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    private static InboxConversationSnapshot Snapshot(SessionKey? key = null, string thread = "thread", string state = "running",
        int requestCount = 1, string? displayName = null, string? userName = null, string? channelName = null,
        string? siteName = null, bool isDirectMessage = false, DateTimeOffset? updatedAt = null, string? workspaceRoot = null)
        => new(key ?? new SessionKey("slack", "channel", thread, "user", "slack"), workspaceRoot ?? Path.GetFullPath(Path.GetTempPath()),
            updatedAt ?? Timestamp, state, requestCount, displayName, userName, channelName, siteName, isDirectMessage);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-inbox-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
