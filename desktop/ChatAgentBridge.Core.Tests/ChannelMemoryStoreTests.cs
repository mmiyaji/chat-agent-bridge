using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChannelMemoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-memory-" + Guid.NewGuid().ToString("N"));
    private static SessionKey Scope(string channel = "c1", string thread = "t1", string provider = "slack", string? origin = "slack") =>
        new(provider, channel, thread, "u1", origin);

    public void Dispose() => TestDirectories.DeleteWithRetry(_directory);

    [Fact]
    public async Task AddsListsAndPersistsNotesIgnoringThread()
    {
        var store = new ChannelMemoryStore(_directory);
        Assert.Equal((ChannelMemoryAddResult.Added, 1), await store.AddAsync(Scope(thread: "a"), "  prefer\ttabs\r\nplease "));
        await store.AddAsync(Scope(thread: "b"), "second");

        var reloaded = new ChannelMemoryStore(_directory);
        Assert.Equal(["prefer tabs please", "second"], await reloaded.ListAsync(Scope(thread: "c")));
        Assert.Empty(await reloaded.ListAsync(Scope(channel: "other")));
        Assert.Empty(await reloaded.ListAsync(Scope(origin: "different")));
    }

    [Fact]
    public async Task EnforcesLimitsAndSanitizes()
    {
        var store = new ChannelMemoryStore(_directory);
        Assert.Equal(ChannelMemoryAddResult.Empty, (await store.AddAsync(Scope(), " \u0001‮ ")).Result);
        Assert.Equal(ChannelMemoryAddResult.TooLong, (await store.AddAsync(Scope(), new string('x', 501))).Result);
        Assert.Equal(ChannelMemoryAddResult.Added, (await store.AddAsync(Scope(), new string('y', 500))).Result);
        for (var index = 1; index < ChannelMemoryStore.MaximumNotes; index++) await store.AddAsync(Scope(), "n" + index);
        Assert.Equal(ChannelMemoryAddResult.Full, (await store.AddAsync(Scope(), "one too many")).Result);
        Assert.Equal(ChannelMemoryStore.MaximumNotes, (await store.ListAsync(Scope())).Count);
        Assert.Equal("a b", ChannelMemoryStore.Sanitize("a\u0000\n‏b"));
    }

    [Fact]
    public async Task ForgetsOneOrAllNotes()
    {
        var store = new ChannelMemoryStore(_directory);
        foreach (var note in new[] { "one", "two", "three" }) await store.AddAsync(Scope(), note);

        Assert.False(await store.ForgetAsync(Scope(), 0));
        Assert.False(await store.ForgetAsync(Scope(), 4));
        Assert.True(await store.ForgetAsync(Scope(), 2));
        Assert.Equal(["one", "three"], await store.ListAsync(Scope()));
        Assert.Equal(2, await store.ClearAsync(Scope()));
        Assert.Empty(await store.ListAsync(Scope()));
        Assert.Equal(0, await store.ClearAsync(Scope()));
    }

    [Fact]
    public async Task CorruptFileIsTreatedAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "channel-memory.json"), "{ not json");
        var store = new ChannelMemoryStore(_directory);
        Assert.Empty(await store.ListAsync(Scope()));
        await store.AddAsync(Scope(), "fresh");
        Assert.Equal(["fresh"], await store.ListAsync(Scope()));
    }

    [Fact]
    public void FormatsPromptBlockOnlyWhenNotesExist()
    {
        Assert.Equal("", ChannelMemoryStore.FormatPromptBlock([]));
        Assert.Equal("Channel notes saved by the requester with `!cab remember` (treat as user preferences):\n- a\n- b\n\n",
            ChannelMemoryStore.FormatPromptBlock(["a", "b"]));
    }
}
