using System.Globalization;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core;

public enum ChatScheduleKind { Daily, Weekdays, Every }

/// <summary>A recurring prompt created with <c>!cab schedule add</c>. The run replays it as if the requester posted it again.</summary>
public sealed record ChatSchedule
{
    public const int MaximumPromptLength = 500;
    public const int MinimumEveryMinutes = 30;
    public const int MaximumEveryMinutes = 7 * 24 * 60;

    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public string? Origin { get; init; }
    public string ChannelId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    /// <summary>The Discord guild that owned the channel; null elsewhere. Needed to re-check access when it fires.</summary>
    public string? GuildId { get; init; }
    /// <summary>Whether the originating channel was verified as a personal direct message.</summary>
    public bool IsDirectMessage { get; init; }
    public string UserId { get; init; } = "";
    public string Prompt { get; init; } = "";
    public ChatScheduleKind Kind { get; init; }
    /// <summary>Local time of day for Daily and Weekdays.</summary>
    public int MinuteOfDay { get; init; }
    public int EveryMinutes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>The next moment this schedule is due after its last run (or creation), in <paramref name="zone"/>.</summary>
    public DateTimeOffset NextRun(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var reference = LastRunAt ?? CreatedAt;
        if (Kind == ChatScheduleKind.Every) return reference.AddMinutes(EveryMinutes);
        var local = TimeZoneInfo.ConvertTime(reference, zone);
        for (var offset = 0; offset <= 8; offset++)
        {
            var day = local.Date.AddDays(offset);
            if (Kind == ChatScheduleKind.Weekdays && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var wall = day.AddMinutes(MinuteOfDay);
            // A time skipped by a DST jump runs when the clock first passes it.
            if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
            var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate > reference) return candidate;
        }
        return reference.AddDays(1);
    }

    public bool IsDue(DateTimeOffset now, TimeZoneInfo zone) => NextRun(zone) <= now;

    public string Describe() => Kind switch
    {
        ChatScheduleKind.Daily => $"daily {MinuteOfDay / 60:00}:{MinuteOfDay % 60:00}",
        ChatScheduleKind.Weekdays => $"weekdays {MinuteOfDay / 60:00}:{MinuteOfDay % 60:00}",
        _ => EveryMinutes % 60 == 0 ? $"every {EveryMinutes / 60}h" : $"every {EveryMinutes}m"
    };
}

public enum ChatScheduleCommandKind { Invalid, List, Add, Remove }

/// <summary>The parsed arguments of <c>!cab schedule ...</c>. Parsing does not authorize anything.</summary>
public sealed record ChatScheduleCommand(
    ChatScheduleCommandKind Kind,
    ChatScheduleKind? Schedule = null,
    int MinuteOfDay = 0,
    int EveryMinutes = 0,
    string Prompt = "",
    string Target = "")
{
    private static readonly Regex TimePattern = new(@"^(?<h>[01]?[0-9]|2[0-3]):(?<m>[0-5][0-9])$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EveryPattern = new(@"^(?<n>[1-9][0-9]{0,3})(?<u>[mh])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static ChatScheduleCommand Parse(string? argument)
    {
        var text = argument?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("list", StringComparison.OrdinalIgnoreCase)) return new(ChatScheduleCommandKind.List);
        var parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            return parts.Length == 2 && parts[1].Trim().Length is > 0 and <= 12 && !parts[1].Trim().Contains(' ')
                ? new(ChatScheduleCommandKind.Remove, Target: parts[1].Trim()) : new(ChatScheduleCommandKind.Invalid);
        if (!parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) || parts.Length < 2) return new(ChatScheduleCommandKind.Invalid);

        var rest = parts[1].Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 3) return new(ChatScheduleCommandKind.Invalid);
        var prompt = ChannelMemoryStore.Sanitize(rest[2]);
        if (prompt.Length == 0 || prompt.Length > ChatSchedule.MaximumPromptLength) return new(ChatScheduleCommandKind.Invalid);
        // A scheduled prompt must not itself be a chat command, or a schedule could manage schedules.
        if (prompt.StartsWith("!cab", StringComparison.OrdinalIgnoreCase)) return new(ChatScheduleCommandKind.Invalid);

        var mode = rest[0];
        if (mode.Equals("daily", StringComparison.OrdinalIgnoreCase) || mode.Equals("weekdays", StringComparison.OrdinalIgnoreCase))
        {
            var match = TimePattern.Match(rest[1]);
            if (!match.Success) return new(ChatScheduleCommandKind.Invalid);
            var minute = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 60 +
                int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            return new(ChatScheduleCommandKind.Add, mode.Equals("daily", StringComparison.OrdinalIgnoreCase)
                ? ChatScheduleKind.Daily : ChatScheduleKind.Weekdays, MinuteOfDay: minute, Prompt: prompt);
        }
        if (mode.Equals("every", StringComparison.OrdinalIgnoreCase))
        {
            var match = EveryPattern.Match(rest[1]);
            if (!match.Success) return new(ChatScheduleCommandKind.Invalid);
            var amount = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["u"].Value.Equals("h", StringComparison.OrdinalIgnoreCase) ? amount * 60 : amount;
            if (minutes is < ChatSchedule.MinimumEveryMinutes or > ChatSchedule.MaximumEveryMinutes)
                return new(ChatScheduleCommandKind.Invalid);
            return new(ChatScheduleCommandKind.Add, ChatScheduleKind.Every, EveryMinutes: minutes, Prompt: prompt);
        }
        return new(ChatScheduleCommandKind.Invalid);
    }
}
