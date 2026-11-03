using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Tests;

public sealed class AuditExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-audit-export-" + Guid.NewGuid().ToString("N"));

    private static AuditEvent Event(DateTimeOffset time, string kind = "received", string channel = "c1") =>
        new(time, kind, null, "mattermost", channel, "t1");

    [Fact]
    public async Task CsvExportsEveryRetainedEventOldestFirstBeyondTheUiLimit()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        await store.AppendManyAsync(Enumerable.Range(0, 520).Select(i => Event(start.AddSeconds(i))));
        using var writer = new StringWriter();
        var count = await AuditExporter.ExportAsync(store, writer, AuditExportFormat.Csv);
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(520, count);
        Assert.Equal(521, lines.Length);
        Assert.StartsWith("timestamp,kind", lines[0]);
        Assert.True(string.CompareOrdinal(lines[1], lines[^1]) < 0);
    }

    [Fact]
    public async Task RangeFilterAndFormulaNeutralization()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        var now = DateTimeOffset.UtcNow;
        await store.AppendManyAsync([Event(now.AddDays(-3), channel: "old"), Event(now.AddMinutes(-1), channel: "=HYPERLINK(\"x\")")]);
        using var writer = new StringWriter();
        var count = await AuditExporter.ExportAsync(store, writer, AuditExportFormat.Csv, from: now.AddDays(-1));
        Assert.Equal(1, count);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", writer.ToString());
        Assert.DoesNotContain("old", writer.ToString());
    }

    [Fact]
    public async Task JsonlWritesOneObjectPerLine()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        await store.AppendAsync(Event(DateTimeOffset.UtcNow));
        using var writer = new StringWriter();
        await AuditExporter.ExportAsync(store, writer, AuditExportFormat.Jsonl);
        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("{", line);
        Assert.Contains("\"Kind\":\"received\"", line);
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
