using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class AttachmentConversationDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-attachment-deletion-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Removing_a_conversation_revokes_its_attachments_and_consents_only()
    {
        var data = Path.Combine(_root, "data");
        var workspace1 = Path.Combine(_root, "workspace1");
        var workspace2 = Path.Combine(_root, "workspace2");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace1);
        Directory.CreateDirectory(workspace2);
        var removed = new SessionKey("mattermost", "channel", "thread", "user", new string('c', 64));
        var other = new SessionKey("mattermost", "channel", "thread", "other-user", new string('c', 64));
        var records = new AttachmentConversationStore(data);
        var consents = new AttachmentConsentStore(data);
        var fingerprint = new string('a', 64);
        static FileAttachmentCandidate Candidate(string root) => new(Path.Combine(root, "report.txt"), "report.txt", 3, DateTime.UtcNow, new string('b', 64));
        await records.RecordDiscoveredAsync(removed, workspace1, [Candidate(workspace1)], true);
        await records.RecordDiscoveredAsync(removed, workspace2, [Candidate(workspace2)], true);
        await records.RecordDiscoveredAsync(other, workspace1, [Candidate(workspace1)], true);
        await consents.SaveAsync(removed, "session-1", fingerprint);
        await consents.SaveAsync(removed, "session-2", fingerprint);
        await consents.SaveAsync(other, "session-1", fingerprint);

        await new SessionStore(data).RemoveAsync(removed);

        Assert.Empty(await records.GetDiscoveredAsync(removed, workspace1));
        Assert.Empty(await records.GetDiscoveredAsync(removed, workspace2));
        Assert.Single(await records.GetDiscoveredAsync(other, workspace1));
        Assert.False(await consents.IsApprovedAsync(removed, "session-1", fingerprint));
        Assert.False(await consents.IsApprovedAsync(removed, "session-2", fingerprint));
        Assert.True(await consents.IsApprovedAsync(other, "session-1", fingerprint));
    }

    [Fact]
    public async Task Rerecorded_files_are_kept_when_the_per_conversation_limit_is_reached()
    {
        var data = Path.Combine(_root, "data");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var key = new SessionKey("mattermost", "channel", "thread", "user", "https://example.test");
        var store = new AttachmentConversationStore(data);
        FileAttachmentCandidate Make(int n) => new(Path.Combine(workspace, $"file-{n}.txt"), $"file-{n}.txt", 3, DateTime.UtcNow, new string('b', 64));
        await store.RecordDiscoveredAsync(key, workspace, Enumerable.Range(0, 100).Select(Make).ToArray(), true);
        await store.RecordDiscoveredAsync(key, workspace, [Make(0)], true);
        await store.RecordDiscoveredAsync(key, workspace, [Make(100)], true);

        var names = (await store.GetDiscoveredAsync(key, workspace)).Select(file => file.RelativePath).ToArray();
        Assert.Equal(100, names.Length);
        Assert.Contains("file-0.txt", names);
        Assert.DoesNotContain("file-1.txt", names);
        Assert.Contains("file-100.txt", names);
    }

    [Fact]
    public async Task Updated_conversation_survives_eviction_and_legacy_entries_do_not_consume_capacity()
    {
        var data = Path.Combine(_root, "data");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        var entries = Enumerable.Range(0, 500).Select(i => new
        {
            Provider = "mattermost", ChannelId = "channel", ThreadId = $"thread-{i}", UserId = "user",
            Origin = "https://example.test", Workspace = workspace, Files = Array.Empty<FileAttachmentCandidate>()
        }).Cast<object>().ToList();
        entries.Add(new { Provider = "mattermost", ChannelId = "channel", ThreadId = "legacy", UserId = "user", Origin = "https://example.test", StartUtc = DateTime.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(data, "attachment-conversations.json"), System.Text.Json.JsonSerializer.Serialize(entries));
        var store = new AttachmentConversationStore(data);
        SessionKey Key(int n) => new("mattermost", "channel", $"thread-{n}", "user", "https://example.test");
        FileAttachmentCandidate Make(int n) => new(Path.Combine(workspace, $"file-{n}.txt"), $"file-{n}.txt", 3, DateTime.UtcNow, new string('b', 64));
        await store.RecordDiscoveredAsync(Key(0), workspace, [Make(0)], true);
        await store.RecordDiscoveredAsync(Key(500), workspace, [Make(500)], true);

        Assert.Single(await store.GetDiscoveredAsync(Key(0), workspace));
        Assert.Empty(await store.GetDiscoveredAsync(Key(1), workspace));
        Assert.Single(await store.GetDiscoveredAsync(Key(500), workspace));
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(data, "attachment-conversations.json")));
        Assert.Equal(500, json.RootElement.GetArrayLength());
        Assert.DoesNotContain(json.RootElement.EnumerateArray(), entry => entry.GetProperty("ThreadId").GetString() == "legacy");
        Assert.DoesNotContain(json.RootElement.EnumerateArray(), entry => entry.GetProperty("ThreadId").GetString() == "thread-1");
    }

    [Fact]
    public async Task Reading_an_existing_conversation_preserves_it_at_the_capacity_limit()
    {
        var data = Path.Combine(_root, "data");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        FileAttachmentCandidate Make(int n) => new(Path.Combine(workspace, $"file-{n}.txt"), $"file-{n}.txt", 3, DateTime.UtcNow, new string('b', 64));
        var entries = Enumerable.Range(0, 500).Select(i => new
        {
            Provider = "mattermost", ChannelId = "channel", ThreadId = $"thread-{i}", UserId = "user",
            Origin = "https://example.test", Workspace = workspace, Files = new[] { Make(i) }
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(data, "attachment-conversations.json"), System.Text.Json.JsonSerializer.Serialize(entries));
        var store = new AttachmentConversationStore(data);
        SessionKey Key(int n) => new("mattermost", "channel", $"thread-{n}", "user", "https://example.test");

        Assert.Single(await store.GetDiscoveredAsync(Key(0), workspace));
        await store.RecordDiscoveredAsync(Key(500), workspace, [Make(500)], true);

        Assert.Single(await store.GetDiscoveredAsync(Key(0), workspace));
        Assert.Empty(await store.GetDiscoveredAsync(Key(1), workspace));
        Assert.Single(await store.GetDiscoveredAsync(Key(500), workspace));
    }

    [Fact]
    public async Task Removing_a_conversation_fails_when_its_ledger_cannot_be_read()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "attachment-conversations.json");
        await File.WriteAllTextAsync(path, "{bad-json");
        var key = new SessionKey("mattermost", "channel", "thread", "user", new string('c', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SessionStore(data).RemoveAsync(key));
        Assert.Equal("{bad-json", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("cab-attachment-deletion-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
