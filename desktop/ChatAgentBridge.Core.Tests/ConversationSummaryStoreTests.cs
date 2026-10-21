using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationSummaryStoreTests
{
    private static readonly SessionKey Key = new("slack", "channel-1", "thread-1", "user-1", "slack");

    [Theory]
    [InlineData(AgentProfile.Gemini)]
    [InlineData(AgentProfile.GitHubCopilot)]
    public async Task StructuredCli_notes_remain_scoped_and_survive_reopen(string provider)
    {
        var directory = NewDirectory();
        try
        {
            var id = Guid.NewGuid().ToString("D");
            var store = new ConversationSummaryStore(directory, new TestProtector());
            await store.RecordAsync(Key, provider, id, "continue the task", "finished the task");
            var summary = await new ConversationSummaryStore(directory, new TestProtector()).FindAsync(Key);
            Assert.Equal(provider, summary!.SourceProvider);
            Assert.Equal(id, summary.SessionId);
            Assert.Null(await store.FindAsync(Key with { UserId = "another-user" }));
            Assert.Null(await store.FindAsync(Key with { ThreadId = "another-thread" }));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RecordAndFind_PersistEncryptedBriefNotesAcrossInstances()
    {
        var directory = NewDirectory();
        try
        {
            var protector = new TestProtector();
            var store = new ConversationSummaryStore(directory, protector);
            await store.RecordAsync(Key, "codex", "session-1", "Find the failing test", "The parser test failed.");

            var reopened = new ConversationSummaryStore(directory, protector);
            var summary = await reopened.FindAsync(Key);

            Assert.NotNull(summary);
            Assert.Equal("codex", summary.SourceProvider);
            Assert.Equal("session-1", summary.SessionId);
            Assert.Contains("Request: Find the failing test", summary.Text);
            Assert.Contains("Response: The parser test failed.", summary.Text);
            Assert.Contains("Brief conversation notes (extracts; not native agent history)", summary.Text);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "conversation-summaries.json"));
            Assert.DoesNotContain("Find the failing test", json);
            Assert.DoesNotContain("The parser test failed.", json);
            Assert.Contains("session-1", json);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task DefaultProtector_UsesDpapiForStoredConversationText()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = NewDirectory();
        try
        {
            var store = new ConversationSummaryStore(directory);
            await store.RecordAsync(Key, "codex", "session-1", "unique prompt plaintext", "unique answer plaintext");

            var json = await File.ReadAllTextAsync(Path.Combine(directory, "conversation-summaries.json"));
            Assert.DoesNotContain("unique prompt plaintext", json);
            Assert.DoesNotContain("unique answer plaintext", json);
            var reopened = new ConversationSummaryStore(directory);
            var summary = await reopened.FindAsync(Key);
            Assert.NotNull(summary);
            Assert.Contains("unique prompt plaintext", summary.Text);
            Assert.Contains("unique answer plaintext", summary.Text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RecordAsync_KeepsOnlyThreePairsAndIncludesTruncationMarkers()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ConversationSummaryStore(directory, new TestProtector());
            await store.RecordAsync(Key, "claude", "session-1", "first", "answer-1");
            await store.RecordAsync(Key, "claude", "session-1", "second", "answer-2");
            await store.RecordAsync(Key, "claude", "session-1", "third", "answer-3");
            await store.RecordAsync(Key, "claude", "session-1", new string('p', 601), new string('a', 1801));

            var summary = await store.FindAsync(Key);

            Assert.NotNull(summary);
            Assert.DoesNotContain("Request: first", summary.Text);
            Assert.Contains("Request: second", summary.Text);
            Assert.Contains("Request: third", summary.Text);
            Assert.Contains(new string('p', 600 - "… [truncated]".Length) + "… [truncated]", summary.Text);
            Assert.Contains(new string('a', 1800 - "… [truncated]".Length) + "… [truncated]", summary.Text);
            Assert.True(summary.Text.Length <= 8000);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RecordAsync_NewSessionIdStartsFreshNotes()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ConversationSummaryStore(directory, new TestProtector());
            await store.RecordAsync(Key, "codex", "session-old", "old prompt", "old response");
            await store.RecordAsync(Key, "claude", "session-new", "new prompt", "new response");

            var summary = await store.FindAsync(Key);

            Assert.NotNull(summary);
            Assert.Equal("claude", summary.SourceProvider);
            Assert.Equal("session-new", summary.SessionId);
            Assert.DoesNotContain("old prompt", summary.Text);
            Assert.Contains("new prompt", summary.Text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ClearAsync_RemovesSummary()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ConversationSummaryStore(directory, new TestProtector());
            await store.RecordAsync(Key, "codex", "session-1", "prompt", "answer");
            await store.ClearAsync(Key);

            Assert.Null(await store.FindAsync(Key));
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":999,\"Entries\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Entries\":[{}]}")]
    public async Task ClearAsync_ReplacesStructurallyUnreadableCacheWithEmptyDocument(string contents)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "conversation-summaries.json");
            await File.WriteAllTextAsync(path, contents);
            var store = new ConversationSummaryStore(directory, new TestProtector());

            await store.ClearAsync(Key);

            var recovered = await File.ReadAllTextAsync(path);
            Assert.Contains("\"SchemaVersion\": 1", recovered);
            Assert.Contains("\"Entries\": []", recovered);
            Assert.Null(await store.FindAsync(Key));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task ClearAsync_PreservesOtherScopesWhenCacheIsValid()
    {
        var directory = NewDirectory();
        try
        {
            var otherKey = Key with { ThreadId = "thread-2", UserId = "user-2" };
            var store = new ConversationSummaryStore(directory, new TestProtector());
            await store.RecordAsync(Key, "codex", "session-1", "target prompt", "target answer");
            await store.RecordAsync(otherKey, "claude", "session-2", "other prompt", "other answer");

            await store.ClearAsync(Key);

            Assert.Null(await store.FindAsync(Key));
            var other = await store.FindAsync(otherKey);
            Assert.NotNull(other);
            Assert.Contains("other prompt", other.Text);
            Assert.Contains("other answer", other.Text);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task FindAsync_CorruptStoreFailsClosed()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "conversation-summaries.json"), "not json");
            var store = new ConversationSummaryStore(directory, new TestProtector());

            await Assert.ThrowsAsync<InvalidDataException>(() => store.FindAsync(Key));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RecordAsync_RejectsInvalidProviderAndOversizedInput()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ConversationSummaryStore(directory, new TestProtector());

            await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(Key, "custom", "session-1", "p", "a"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(Key, "codex", "session-1", new string('x', 100_001), "a"));
            Assert.False(File.Exists(Path.Combine(directory, "conversation-summaries.json")));
        }
        finally { DeleteDirectory(directory); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class TestProtector : IConversationTextProtector
    {
        public string Protect(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        public string Unprotect(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }
}
