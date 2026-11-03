using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ChatAgentBridge.Core.History;

public enum AuditExportFormat { Csv, Jsonl }

/// <summary>Writes the allowlisted audit metadata for review outside the app. No prompts or output exist in the source data.</summary>
public static class AuditExporter
{
    private static readonly string[] Columns = ["timestamp", "kind", "executionId", "source", "channelId", "threadId"];

    /// <returns>The number of events written.</returns>
    public static async Task<int> ExportAsync(AuditStore store, TextWriter writer, AuditExportFormat format,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        var count = 0;
        if (format == AuditExportFormat.Csv) await writer.WriteLineAsync(string.Join(',', Columns)).ConfigureAwait(false);
        await store.ReadAllAsync(async record =>
        {
            if (from is { } start && record.Timestamp < start) return;
            if (to is { } end && record.Timestamp > end) return;
            await writer.WriteLineAsync(format == AuditExportFormat.Csv ? Csv(record) : Json(record)).ConfigureAwait(false);
            count++;
        }, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    internal static string Csv(AuditEvent record) => string.Join(',',
        Field(record.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        Field(record.Kind), Field(record.ExecutionId?.ToString("D") ?? ""),
        Field(record.Source), Field(record.ChannelId), Field(record.ThreadId));

    internal static string Json(AuditEvent record) => JsonSerializer.Serialize(record);

    // Quote every field and neutralize spreadsheet formulas, since channel names are chosen by other people.
    private static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
