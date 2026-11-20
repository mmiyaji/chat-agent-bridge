using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatScheduleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-schedule-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static ChatSchedule Daily(int minute, DateTimeOffset created, DateTimeOffset? last = null,
        ChatScheduleKind kind = ChatScheduleKind.Daily) =>
        new() { Id = "a", Provider = "mattermost", ChannelId = "c", UserId = "u", Prompt = "p", Kind = kind, MinuteOfDay = minute, CreatedAt = created, LastRunAt = last };

    [Theory]
    [InlineData("add daily 09:00 check the build", ChatScheduleKind.Daily, 540, 0)]
    [InlineData("add weekdays 7:30 triage issues", ChatScheduleKind.Weekdays, 450, 0)]
    [InlineData("add every 2h summarize", ChatScheduleKind.Every, 0, 120)]
    [InlineData("ADD EVERY 45m ping", ChatScheduleKind.Every, 0, 45)]
    public void ParsesValidAdd(string text, ChatScheduleKind kind, int minuteOfDay, int every)
    {
        var command = ChatScheduleCommand.Parse(text);
        Assert.Equal(ChatScheduleCommandKind.Add, command.Kind);
        Assert.Equal(kind, command.Schedule);
        Assert.Equal(minuteOfDay, command.MinuteOfDay);
        Assert.Equal(every, command.EveryMinutes);
    }

    [Theory]
    [InlineData("add daily 25:00 x")]
    [InlineData("add daily 1٢:00 x")]
    [InlineData("add every 5m x")]
    [InlineData("add every 3٠m x")]
    [InlineData("add every 99999h x")]
    [InlineData("add daily 09:00")]
    [InlineData("add monthly 09:00 x")]
    [InlineData("add daily 09:00 !cab schedule add daily 01:00 loop")]
    [InlineData("remove")]
    [InlineData("unknown")]
    public void RejectsInvalidInput(string text) =>
        Assert.Equal(ChatScheduleCommandKind.Invalid, ChatScheduleCommand.Parse(text).Kind);

    [Fact]
    public void EmptyAndListMeanList()
    {
        Assert.Equal(ChatScheduleCommandKind.List, ChatScheduleCommand.Parse("").Kind);
        Assert.Equal(ChatScheduleCommandKind.List, ChatScheduleCommand.Parse("list").Kind);
        Assert.Equal("abc123", ChatScheduleCommand.Parse("remove abc123").Target);
    }

    [Fact]
    public void DailyIsDueOncePerDayAndCatchesUpOnlyOnce()
    {
        var created = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var schedule = Daily(9 * 60, created);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), schedule.NextRun(Utc));
        Assert.False(schedule.IsDue(new DateTimeOffset(2026, 11, 3, 8, 59, 0, TimeSpan.Zero), Utc));
        // Several missed days still produce one due run; after it runs the next is tomorrow.
        var late = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.True(schedule.IsDue(late, Utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), (schedule with { LastRunAt = late }).NextRun(Utc));
    }

    [Fact]
    public void WeekdaysSkipTheWeekend()
    {
        var friday = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);
        var next = Daily(9 * 60, friday, kind: ChatScheduleKind.Weekdays).NextRun(Utc);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("Australia/Lord_Howe", 10, 4, 2, 30, 660)]
    [InlineData("America/Los_Angeles", 3, 8, 3, 0, -420)]
    public void SkippedWallTimeRunsAtFirstValidMinute(string zoneId, int month, int day, int hour, int minute, int utcOffsetMinutes)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var expected = new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromMinutes(utcOffsetMinutes));
        var scheduled = Daily(2 * 60 + 15, expected.AddHours(-8));

        Assert.False(scheduled.IsDue(expected.AddMinutes(-1), zone));
        Assert.True(expected.EqualsExact(scheduled.NextRun(zone)));
        Assert.True(scheduled.IsDue(expected, zone));
        Assert.False((scheduled with { LastRunAt = expected }).IsDue(expected, zone));
    }

    [Fact]
    public void ScheduleMatchesOnlyItsCanonicalConnectionOrigin()
    {
        const string originalServer = "https://chat.example.com/mattermost";
        var origin = Sessions.ChatSessionScope.Create("mattermost", "c", "t", "u", originalServer).Origin;
        var schedule = Daily(0, DateTimeOffset.UtcNow) with { Origin = origin };
        var connection = new BridgeSettings
        {
            MattermostServerUrl = "https://CHAT.example.com:443/mattermost/",
            MattermostChannelId = "c"
        };

        Assert.True(schedule.MatchesConnectionScope("mattermost", connection));
        Assert.False(schedule.MatchesConnectionScope("mattermost", connection with { MattermostServerUrl = "https://chat.example.com/other" }));
        Assert.False(schedule.MatchesConnectionScope("slack", connection));
        Assert.False((schedule with { Origin = null }).MatchesConnectionScope("mattermost", connection));
    }

    [Fact]
    public void MattermostScheduleMatchesConfiguredSharedChannelOrEnabledDirectMessages()
    {
        var connection = new BridgeSettings
        {
            MattermostServerUrl = "https://chat.example.com",
            MattermostChannelId = "shared",
            MattermostAllowDirectMessages = true
        };
        var origin = Sessions.ChatSessionScope.Create("mattermost", "shared", "t", "u", connection.MattermostServerUrl).Origin;
        var shared = Daily(0, DateTimeOffset.UtcNow) with { Origin = origin, ChannelId = "shared" };
        var previousShared = shared with { ChannelId = "previous-shared" };
        var direct = shared with { ChannelId = "personal", IsDirectMessage = true };

        Assert.True(shared.MatchesConnectionScope("mattermost", connection));
        Assert.False(previousShared.MatchesConnectionScope("mattermost", connection));
        Assert.True(direct.MatchesConnectionScope("mattermost", connection));
        var sharedOnly = connection with { MattermostAllowDirectMessages = false };
        Assert.True(shared.MatchesConnectionScope("mattermost", sharedOnly));
        Assert.False(direct.MatchesConnectionScope("mattermost", sharedOnly));
    }

    [Fact]
    public void EveryAddsTheIntervalToTheLastRun()
    {
        var created = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var schedule = new ChatSchedule { Kind = ChatScheduleKind.Every, EveryMinutes = 90, CreatedAt = created };
        Assert.Equal(created.AddMinutes(90), schedule.NextRun(Utc));
        Assert.Equal(created.AddMinutes(100).AddMinutes(90), (schedule with { LastRunAt = created.AddMinutes(100) }).NextRun(Utc));
    }

    [Fact]
    public async Task StoreEnforcesLimitsAndRemovesOnlyTheCallersOwnSchedules()
    {
        var store = new ChatScheduleStore(_directory);
        ChatSchedule Template(string user, string prompt = "p") => new()
        { Provider = "mattermost", ChannelId = "c", UserId = user, Prompt = prompt, Kind = ChatScheduleKind.Every, EveryMinutes = 60 };
        for (var i = 0; i < ChatScheduleStore.MaximumPerUser; i++)
            Assert.Equal(ChatScheduleAddResult.Added, (await store.AddAsync(Template("u1"))).Result);
        Assert.Equal(ChatScheduleAddResult.TooManyForUser, (await store.AddAsync(Template("u1"))).Result);
        var other = (await store.AddAsync(Template("u2", "mine"))).Schedule!;

        var all = await store.ListAsync();
        var u1 = all.Where(item => item.UserId == "u1").ToArray();
        Assert.Null(await store.RemoveAsync(other.Id, u1));
        Assert.NotNull(await store.RemoveAsync("1", u1));
        Assert.Equal(ChatScheduleStore.MaximumPerUser, (await store.ListAsync()).Count);
        Assert.Null(await store.RemoveAsync("99", u1));
    }

    [Fact]
    public async Task NumericIdsDoNotResolveAsListPositions()
    {
        var store = new ChatScheduleStore(_directory);
        var own = Daily(0, DateTimeOffset.UtcNow) with { Id = "123456" };
        var other = own with { Id = "000001", UserId = "other" };
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "chat-schedules.json"),
            System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Schedules = new[] { own, other } }));

        Assert.Null(await store.RemoveAsync(other.Id, [own]));
        Assert.Equal(own, await store.RemoveAsync(own.Id, [own]));
        Assert.Equal(other, Assert.Single(await store.ListAsync()));
    }

    [Fact]
    public async Task ClaimRunPersistsAndAnUnreadableFileReadsAsEmpty()
    {
        var store = new ChatScheduleStore(_directory);
        var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var added = (await store.AddAsync(Daily(9 * 60, at.AddDays(-1)))).Schedule!;
        Assert.True(await store.TryClaimRunAsync(added, at, Utc));
        Assert.Equal(at, (await store.ListAsync()).Single().LastRunAt);
        await File.WriteAllTextAsync(Path.Combine(_directory, "chat-schedules.json"), "{ not json");
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task DeletedScheduleCannotBeClaimedFromAListSnapshot()
    {
        var store = new ChatScheduleStore(_directory);
        var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var added = (await store.AddAsync(Daily(9 * 60, at.AddDays(-1)))).Schedule!;
        var visible = await store.ListAsync();

        Assert.Equal(added, await store.RemoveAsync(added.Id, visible));
        Assert.False(await store.TryClaimRunAsync(Assert.Single(visible), at, Utc));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ConcurrentClaimsSucceedOnceAndAStaleSnapshotCannotClaimTheNextRun()
    {
        var store = new ChatScheduleStore(_directory);
        var otherStore = new ChatScheduleStore(_directory);
        var at = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var added = (await store.AddAsync(Daily(9 * 60, at.AddHours(-1)))).Schedule!;
        Assert.False(await store.TryClaimRunAsync(added, at.AddMinutes(-1), Utc));
        var claims = await Task.WhenAll(store.TryClaimRunAsync(added, at, Utc), otherStore.TryClaimRunAsync(added, at, Utc));

        Assert.Single(claims, claimed => claimed);
        Assert.False(await store.TryClaimRunAsync(added, at.AddDays(1), Utc));
        var current = Assert.Single(await store.ListAsync());
        Assert.True(await store.TryClaimRunAsync(current, at.AddDays(1), Utc));
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
