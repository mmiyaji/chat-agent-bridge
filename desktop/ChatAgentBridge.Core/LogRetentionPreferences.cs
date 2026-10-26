using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

/// <summary>Validated retention limits for local application logs.</summary>
public sealed record LogRetentionPreferences
{
    [JsonRequired] public int StatisticsRetentionDays { get; init; } = 1825;
    [JsonRequired] public int AuditRetentionDays { get; init; } = 365;
    [JsonRequired] public int AuditSegmentSizeMb { get; init; } = 4;
    [JsonRequired] public int AuditMaxSegments { get; init; } = 16;
    [JsonRequired] public int HistoryRetentionDays { get; init; } = 30;
    [JsonRequired] public int HistoryMaxRecords { get; init; } = 500;
    [JsonRequired] public int ConversationRetentionDays { get; init; } = 30;
    [JsonRequired] public int DiagnosticsRetentionDays { get; init; } = 7;
    [JsonRequired] public int DiagnosticsFileKiB { get; init; } = 128;
    [JsonRequired] public int DiagnosticsMaxFiles { get; init; } = 4;

    public void Validate()
    {
        Range(StatisticsRetentionDays, 30, 3660, nameof(StatisticsRetentionDays));
        Range(AuditRetentionDays, 1, 3650, nameof(AuditRetentionDays));
        Range(AuditSegmentSizeMb, 1, 16, nameof(AuditSegmentSizeMb));
        Range(AuditMaxSegments, 2, 64, nameof(AuditMaxSegments));
        Range(HistoryRetentionDays, 1, 365, nameof(HistoryRetentionDays));
        Range(HistoryMaxRecords, 50, 500, nameof(HistoryMaxRecords));
        Range(ConversationRetentionDays, 1, 365, nameof(ConversationRetentionDays));
        Range(DiagnosticsRetentionDays, 1, 90, nameof(DiagnosticsRetentionDays));
        Range(DiagnosticsFileKiB, 64, 1024, nameof(DiagnosticsFileKiB));
        Range(DiagnosticsMaxFiles, 1, 8, nameof(DiagnosticsMaxFiles));
    }

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new ArgumentException($"{name} must be between {min} and {max}.", name);
    }
}
