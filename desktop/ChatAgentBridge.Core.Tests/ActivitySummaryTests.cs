using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class ActivitySummaryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Build_EmptyHistoryKeepsZeroDaysAndNoPeakOrModel()
    {
        var summary = ActivitySummary.Build([], new DateOnly(2026, 10, 18), 365, Utc);
        Assert.Equal(365, summary.Days.Count);
        Assert.All(summary.Days, day => Assert.Equal(0, day.Count));
        Assert.Equal(0, summary.TotalExecutions);
        Assert.Equal(0, summary.ActiveDays);
        Assert.Equal(0, summary.LongestStreak);
        Assert.Null(summary.PeakDay);
        Assert.Empty(summary.Models);
    }

    [Fact]
    public void Build_ZeroFillsChronologicalRangeAndUsesMondayFirstWeekdays()
    {
        var summary = ActivitySummary.Build(
            [Record("2024-01-01T12:00:00+00:00"), Record("2024-01-03T12:00:00+00:00")],
            new DateOnly(2024, 1, 4), 4, Utc);

        Assert.Equal(new DateOnly(2024, 1, 1), summary.StartDay);
        Assert.Equal(new[] { 1, 0, 1, 0 }, summary.Days.Select(day => day.Count));
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }, summary.Weekdays.Select(day => day.DayOfWeek));
        Assert.Equal(new[] { 1, 0, 1, 0, 0, 0, 0 }, summary.Weekdays.Select(day => day.Count));
        Assert.Equal(2, summary.TotalExecutions);
        Assert.Equal(2, summary.ActiveDays);
    }

    [Fact]
    public void Build_UsesReportedModelBeforeRequestedAliasesOrDefaults()
    {
        var summary = ActivitySummary.Build(
            [Record("2026-10-18T12:00:00Z", model: "default", usageModel: "reported-model"),
             Record("2026-10-18T13:00:00Z", model: "alias", usageModel: "reported-model"),
             Record("2026-10-18T14:00:00Z", model: "requested-model")],
            new DateOnly(2026, 10, 18), 30, Utc);
        Assert.Equal(new[] { new ModelActivity("reported-model", 2), new ModelActivity("requested-model", 1) }, summary.Models);
    }

    [Fact]
    public void Build_ConvertsInstantsToLocalDatesAcrossMidnightAndDstChange()
    {
        var zone = CreateEasternLikeZone();
        var summary = ActivitySummary.Build(
            [Record("2024-03-10T04:30:00+00:00"), Record("2024-03-10T07:30:00+00:00")],
            new DateOnly(2024, 3, 10), 2, zone);

        Assert.Equal(new[] { new DateOnly(2024, 3, 9), new DateOnly(2024, 3, 10) }, summary.Days.Select(day => day.Date));
        Assert.Equal(new[] { 1, 1 }, summary.Days.Select(day => day.Count));
    }

    [Fact]
    public void Build_DeduplicatesIdsAndCountsEveryKnownStatusSeparately()
    {
        var duplicateId = Guid.NewGuid();
        var records = new[]
        {
            Record("2024-04-01T08:00:00+00:00", "completed", id: duplicateId),
            Record("2024-04-01T08:00:00+00:00", "failed", id: duplicateId),
            Record("2024-04-01T09:00:00+00:00", "failed"),
            Record("2024-04-02T09:00:00+00:00", "cancelled"),
            Record("2024-04-02T10:00:00+00:00", "running"),
            Record("2024-04-02T11:00:00+00:00", "interrupted")
        };

        var summary = ActivitySummary.Build(records, new DateOnly(2024, 4, 2), 2, Utc);

        Assert.Equal(5, summary.TotalExecutions);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(1, summary.Running);
        Assert.Equal(new[] { 2, 3 }, summary.Days.Select(day => day.Count));
    }

    [Fact]
    public void Build_PicksLatestPeakTieAndUsesModelFallbackAndTopFive()
    {
        var records = new[]
        {
            Record("2024-05-01T00:00:00+00:00", model: "zeta"),
            Record("2024-05-02T00:00:00+00:00", model: "alpha"),
            Record("2024-05-02T01:00:00+00:00", model: "", usageModel: "fallback"),
            Record("2024-05-03T00:00:00+00:00", model: "alpha"),
            Record("2024-05-03T01:00:00+00:00", model: null, usageModel: "fallback"),
            Record("2024-05-04T00:00:00+00:00", model: null, usageModel: null),
            Record("2024-05-05T00:00:00+00:00", model: "beta"),
            Record("2024-05-06T00:00:00+00:00", model: "gamma")
        };

        var summary = ActivitySummary.Build(records, new DateOnly(2024, 5, 6), 6, Utc);

        Assert.Equal(new DateOnly(2024, 5, 3), summary.PeakDay!.Date);
        Assert.Equal(new string?[] { "alpha", "fallback", null, "beta", "gamma" }, summary.Models.Select(model => model.Model));
        Assert.Equal(new[] { 2, 2, 1, 1, 1 }, summary.Models.Select(model => model.Count));
        Assert.Equal(5, summary.Models.Count);
    }

    [Fact]
    public void Build_ExcludesOutOfRangeAndFutureAndBoundsStreakToRequestedRange()
    {
        var summary = ActivitySummary.Build(
            [Record("2023-12-31T23:00:00+00:00"), Record("2024-01-01T08:00:00+00:00"),
             Record("2024-01-02T08:00:00+00:00"), Record("2024-01-03T08:00:00+00:00"),
             Record("2024-01-04T08:00:00+00:00")],
            new DateOnly(2024, 1, 3), 2, Utc);

        Assert.Equal(new DateOnly(2024, 1, 2), summary.StartDay);
        Assert.Equal(new DateOnly(2024, 1, 3), summary.EndDay);
        Assert.Equal(2, summary.TotalExecutions);
        Assert.Equal(2, summary.LongestStreak);
    }

    [Fact]
    public void Build_HandlesYearBoundaryAndLeapDay()
    {
        var yearBoundary = ActivitySummary.Build(
            [Record("2023-12-31T23:00:00+00:00"), Record("2024-01-01T12:00:00+00:00")],
            new DateOnly(2024, 1, 1), 2, Utc);
        Assert.Equal(new[] { new DateOnly(2023, 12, 31), new DateOnly(2024, 1, 1) },
            yearBoundary.Days.Select(day => day.Date));
        Assert.Equal(2, yearBoundary.LongestStreak);

        var summary = ActivitySummary.Build(
            [Record("2024-02-28T23:00:00+00:00"), Record("2024-02-29T12:00:00+00:00"), Record("2024-03-01T00:00:00+00:00")],
            new DateOnly(2024, 3, 1), 3, Utc);

        Assert.Equal(new[] { new DateOnly(2024, 2, 28), new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 1) },
            summary.Days.Select(day => day.Date));
        Assert.Equal(3, summary.LongestStreak);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public void Build_RejectsDayCountsOutsideSupportedBounds(int dayCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActivitySummary.Build([], new DateOnly(2024, 1, 1), dayCount, Utc));
    }

    private static ExecutionRecord Record(string startedAt, string status = "completed", string? model = null,
        string? usageModel = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        StartedAt = DateTimeOffset.Parse(startedAt),
        Status = status,
        AgentModel = model,
        Usage = usageModel is null ? null : new AgentUsage { Model = usageModel }
    };

    private static TimeZoneInfo CreateEasternLikeZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone("ActivitySummaryTest/Eastern", TimeSpan.FromHours(-5),
            "ActivitySummaryTest Eastern", "Standard", "Daylight", [rule]);
    }
}
