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
}
