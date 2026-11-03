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
    [InlineData("add every 5m x")]
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
    public async Task MarkRunPersistsAndAnUnreadableFileReadsAsEmpty()
    {
        var store = new ChatScheduleStore(_directory);
        var added = (await store.AddAsync(new ChatSchedule { Provider = "p", ChannelId = "c", UserId = "u", Prompt = "x", Kind = ChatScheduleKind.Every, EveryMinutes = 60 })).Schedule!;
        var at = DateTimeOffset.UtcNow;
        await store.MarkRunAsync(added.Id, at);
        Assert.Equal(at, (await store.ListAsync()).Single().LastRunAt);
        await File.WriteAllTextAsync(Path.Combine(_directory, "chat-schedules.json"), "{ not json");
        Assert.Empty(await store.ListAsync());
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
