namespace ChatAgentBridge.Core.History;

/// <summary>A date and the number of unique executions started on that local calendar date.</summary>
public sealed record ActivityDay(DateOnly Date, int Count);

/// <summary>A weekday and the number of executions started on that weekday.</summary>
public sealed record WeekdayActivity(DayOfWeek DayOfWeek, int Count);

/// <summary>A model label and the number of executions associated with it.</summary>
public sealed record ModelActivity(string? Model, int Count);

/// <summary>
/// Activity for a bounded local-date range. Total and daily counts include executions with every status,
/// including failures, cancellations, and executions that are still running.
/// </summary>
public sealed record ActivitySummary(
    DateOnly StartDay,
    DateOnly EndDay,
    int TotalExecutions,
    int ActiveDays,
    int LongestStreak,
    ActivityDay? PeakDay,
    IReadOnlyList<ActivityDay> Days,
    IReadOnlyList<WeekdayActivity> Weekdays,
    IReadOnlyList<ModelActivity> Models,
    int Completed,
    int Failed,
    int Cancelled,
    int Running)
{
    /// <summary>Aggregates unique execution IDs by their start date in the supplied time zone.</summary>
    public static ActivitySummary Build(
        IEnumerable<ExecutionRecord> records,
        DateOnly endDay,
        int dayCount,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (dayCount is < 1 or > 366)
            throw new ArgumentOutOfRangeException(nameof(dayCount), "Day count must be between 1 and 366.");

        var startDay = endDay.AddDays(-(dayCount - 1));
        var counts = new int[dayCount];
        var weekdayCounts = new int[7];
        var modelCounts = new Dictionary<ModelKey, int>();
        var seenIds = new HashSet<Guid>();
        var completed = 0;
        var failed = 0;
        var cancelled = 0;
        var running = 0;

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.StartedAt, timeZone).DateTime);
            if (localDate < startDay || localDate > endDay)
                continue;
            if (!seenIds.Add(record.Id))
                continue;

            var index = localDate.DayNumber - startDay.DayNumber;
            counts[index]++;
            weekdayCounts[((int)localDate.DayOfWeek + 6) % 7]++;
            switch (record.Status)
            {
                case "completed": completed++; break;
                case "failed": failed++; break;
                case "cancelled": cancelled++; break;
                case "running": running++; break;
            }

            var model = NonEmpty(record.Usage?.Model) ?? NonEmpty(record.AgentModel);
            var key = new ModelKey(model);
            modelCounts.TryGetValue(key, out var modelCount);
            modelCounts[key] = modelCount + 1;
        }

        var days = new ActivityDay[dayCount];
        var activeDays = 0;
        var longestStreak = 0;
        var currentStreak = 0;
        ActivityDay? peakDay = null;
        var total = 0;
        for (var i = 0; i < dayCount; i++)
        {
            var date = startDay.AddDays(i);
            var count = counts[i];
            days[i] = new ActivityDay(date, count);
            total += count;
            if (count > 0)
            {
                activeDays++;
                currentStreak++;
                longestStreak = Math.Max(longestStreak, currentStreak);
                // Iteration is chronological, so replacing on a tie keeps the latest peak day.
                if (peakDay is null || count >= peakDay.Count)
                    peakDay = days[i];
            }
            else
            {
                currentStreak = 0;
            }
        }

        var weekdays = Enumerable.Range(0, 7)
            .Select(i => new WeekdayActivity((DayOfWeek)((i + 1) % 7), weekdayCounts[i]))
            .ToArray();
        var models = modelCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Value, NullableOrdinalComparer.Instance)
            .Take(5)
            .Select(pair => new ModelActivity(pair.Key.Value, pair.Value))
            .ToArray();

        return new ActivitySummary(startDay, endDay, total, activeDays, longestStreak, peakDay,
            Array.AsReadOnly(days), Array.AsReadOnly(weekdays), Array.AsReadOnly(models),
            completed, failed, cancelled, running);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct ModelKey(string? Value);

    private sealed class NullableOrdinalComparer : IComparer<string?>, IEqualityComparer<string?>
    {
        public static NullableOrdinalComparer Instance { get; } = new();
        public int Compare(string? x, string? y) => StringComparer.Ordinal.Compare(x, y);
        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);
        public int GetHashCode(string? obj) => obj is null ? 0 : StringComparer.Ordinal.GetHashCode(obj);
    }
}
