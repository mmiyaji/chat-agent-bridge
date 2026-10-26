using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Maintenance;

namespace ChatAgentBridge.Core.Tests;

public sealed class AuditStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-audit-test-" + Guid.NewGuid().ToString("N"));
    private string Manifest => Path.Combine(_directory, "audit-manifest.json");
    private string Legacy => Path.Combine(_directory, "audit.json");
    private string Segment(int number) => Path.Combine(_directory, $"audit-segment-{number:D20}.jsonl");
    private static AuditEvent Event(DateTimeOffset? time = null, string channel = "") => new(time ?? DateTimeOffset.UtcNow, "received", null, "local", channel, "");
    private static string Line(AuditEvent record) => JsonSerializer.Serialize(record) + "\n";

    [Fact]
    public async Task CompactAppendPreservesPriorBytesAndHistoryBeyondUiLimit()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        await store.AppendAsync(Event());
        var path = Assert.Single(Directory.GetFiles(_directory, "audit-segment-*.jsonl"));
        var first = await File.ReadAllBytesAsync(path);
        await store.AppendManyAsync(Enumerable.Range(1, 510).Select(index => Event(DateTimeOffset.UtcNow.AddSeconds(-index))));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan(0, first.Length).SequenceEqual(first));
        Assert.Equal(511, File.ReadLines(path).Count());
        Assert.Equal(500, (await store.ListAsync()).Count);
        Assert.False(File.Exists(Legacy));
    }

    [Fact]
    public async Task IncompleteActiveTailIsTruncatedBeforeAppendButCommittedCorruptionIsPreserved()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        var first = Event();
        await store.AppendAsync(first);
        await File.AppendAllTextAsync(Segment(1), "{\"Timestamp\":");
        Assert.Equal(first, Assert.Single(await new AuditStore(_directory).ListAsync()));
        Assert.Equal(Line(first), await File.ReadAllTextAsync(Segment(1)));
        await store.AppendAsync(Event(channel: "after-recovery"));
        Assert.Equal(2, (await store.ListAsync()).Count);
        await File.AppendAllTextAsync(Segment(1), "{}\n{\"unfinished\"");
        var corrupt = await File.ReadAllBytesAsync(Segment(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendAsync(Event()));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(Segment(1)));
    }

    [Fact]
    public async Task ClosedPartialLineIsAnErrorAndNoTailIsModifiedOnOtherCorruption()
    {
        Directory.CreateDirectory(_directory);
        await new AuditStore(_directory).ListAsync();
        await File.WriteAllTextAsync(Segment(1), Line(Event()) + "partial");
        await File.WriteAllTextAsync(Segment(2), Line(Event()) + "active-tail");
        var closed = await File.ReadAllBytesAsync(Segment(1));
        var active = await File.ReadAllBytesAsync(Segment(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).ListAsync());
        Assert.Equal(closed, await File.ReadAllBytesAsync(Segment(1)));
        Assert.Equal(active, await File.ReadAllBytesAsync(Segment(2)));
    }

    [Fact]
    public async Task LegacyMigrationResumesAfterSegmentOrMarkerCommitWithoutDuplicates()
    {
        Directory.CreateDirectory(_directory);
        var records = new[] { Event(), Event(channel: "second") };
        var source = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Records = records });
        await File.WriteAllBytesAsync(Legacy, source);
        // Crash after the complete deterministic segment was durably renamed, before the marker.
        await File.WriteAllTextAsync(Segment(1), string.Concat(records.Select(Line)), new UTF8Encoding(false));
        Assert.Equal(2, (await new AuditStore(_directory).ListAsync()).Count);
        Assert.False(File.Exists(Legacy));
        Assert.True(File.Exists(Manifest));
        // Crash after committing the marker, before removing the legacy source.
        await File.WriteAllBytesAsync(Legacy, source);
        Assert.Equal(2, (await new AuditStore(_directory).ListAsync()).Count);
        Assert.False(File.Exists(Legacy));
        Assert.Equal(2, File.ReadLines(Segment(1)).Count());
    }

    [Theory]
    [InlineData("truncated-tail")]
    [InlineData("missing-segment")]
    [InlineData("extra-segment")]
    [InlineData("changed-complete-line")]
    public async Task CommittedMigrationRetainsLegacyUntilEveryImportedByteIsVerified(string damage)
    {
        Directory.CreateDirectory(_directory);
        var records = new[] { Event(), Event(channel: "only-intact-copy-is-legacy") };
        var source = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Records = records });
        var imported = Encoding.UTF8.GetBytes(string.Concat(records.Select(Line)));
        await File.WriteAllBytesAsync(Legacy, source);
        await File.WriteAllBytesAsync(Segment(1), imported);
        await File.WriteAllTextAsync(Manifest, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, LegacyHash = Convert.ToHexString(SHA256.HashData(source)), MigrationSegments = 1
        }));
        // Simulate a committed marker whose source deletion did not happen, then damage the destination.
        if (damage == "truncated-tail") await File.WriteAllBytesAsync(Segment(1), imported[..^5]);
        else if (damage == "missing-segment") File.Delete(Segment(1));
        else if (damage == "extra-segment") await File.WriteAllTextAsync(Segment(2), Line(Event()));
        else await File.WriteAllTextAsync(Segment(1), Line(records[0]) + Line(Event(channel: "changed")));
        var evidence = Directory.GetFiles(_directory, "audit-*").ToDictionary(path => path, File.ReadAllBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).AppendAsync(Event()));

        Assert.Equal(source, await File.ReadAllBytesAsync(Legacy));
        foreach (var (path, bytes) in evidence) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidLegacyAndUncommittedMigrationMismatchPreserveEveryByte()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Legacy, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).ListAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(Legacy));
        Assert.False(File.Exists(Manifest));
        await File.WriteAllTextAsync(Legacy, JsonSerializer.Serialize(new { SchemaVersion = 1, Records = new[] { Event() } }));
        await File.WriteAllTextAsync(Segment(1), Line(Event(channel: "different")));
        var source = await File.ReadAllBytesAsync(Legacy);
        var segment = await File.ReadAllBytesAsync(Segment(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).ListAsync());
        Assert.Equal(source, await File.ReadAllBytesAsync(Legacy));
        Assert.Equal(segment, await File.ReadAllBytesAsync(Segment(1)));
        Assert.False(File.Exists(Manifest));
    }

    [Fact]
    public async Task RotationBoundsSegmentSizeAndCountAndReloadsPolicy()
    {
        Directory.CreateDirectory(_directory);
        var preferences = new LogRetentionPreferencesStore(_directory);
        await preferences.SaveAsync(new() { AuditSegmentSizeMb = 1, AuditMaxSegments = 4 });
        var store = new AuditStore(_directory);
        // Escaped Unicode gives large bounded metadata entries without test-only production controls.
        var channel = new string('\u3042', 256);
        await store.AppendManyAsync(Enumerable.Range(0, 1900).Select(index => Event(DateTimeOffset.UtcNow.AddTicks(index), channel)));
        var segments = Directory.GetFiles(_directory, "audit-segment-*.jsonl");
        Assert.InRange(segments.Length, 3, 4);
        Assert.All(segments, path => Assert.InRange(new FileInfo(path).Length, 1, 1024 * 1024));
        await preferences.SaveAsync(new() { AuditSegmentSizeMb = 1, AuditMaxSegments = 2 });
        await store.ApplyRetentionAsync();
        Assert.Equal(2, Directory.GetFiles(_directory, "audit-segment-*.jsonl").Length);
        Assert.Equal(500, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task RetentionUsesNewestTimestampInEachSegmentAndKeepsActive()
    {
        Directory.CreateDirectory(_directory);
        var preferences = new LogRetentionPreferencesStore(_directory);
        await preferences.SaveAsync(new() { AuditRetentionDays = 1 });
        var store = new AuditStore(_directory);
        await store.ListAsync();
        await File.WriteAllTextAsync(Segment(1), Line(Event(DateTimeOffset.UtcNow.AddDays(-3))));
        await File.WriteAllTextAsync(Segment(2), Line(Event(DateTimeOffset.UtcNow.AddDays(-3))) + Line(Event()));
        await File.WriteAllTextAsync(Segment(3), Line(Event(DateTimeOffset.UtcNow.AddDays(-4))));
        await store.ApplyRetentionAsync();
        Assert.False(File.Exists(Segment(1)));
        Assert.True(File.Exists(Segment(2)));
        Assert.False(File.Exists(Segment(3)));
        Assert.True(File.Exists(Segment(4)));
        await File.WriteAllTextAsync(Path.Combine(_directory, "log-retention.json"), "broken preferences");
        var files = Directory.GetFiles(_directory, "audit-*").ToDictionary(path => path, File.ReadAllBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyRetentionAsync());
        foreach (var (path, bytes) in files) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MaintenanceExpiresAgedActiveRecordsAndRollsMixedSegmentsMonthly()
    {
        Directory.CreateDirectory(_directory);
        var preferences = new LogRetentionPreferencesStore(_directory);
        await preferences.SaveAsync(new() { AuditRetentionDays = 365 });
        var store = new AuditStore(_directory);
        await store.AppendManyAsync([Event(DateTimeOffset.UtcNow.AddDays(-400)), Event(DateTimeOffset.UtcNow.AddDays(-399))]);
        await store.ApplyRetentionAsync();
        Assert.Empty(await store.ListAsync());
        Assert.False(File.Exists(Segment(1)));
        Assert.True(File.Exists(Segment(2)));

        var old = Event(DateTimeOffset.UtcNow.AddDays(-31));
        var recent = Event();
        await store.AppendManyAsync([old, recent]);
        await store.ApplyRetentionAsync();
        Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.True(File.Exists(Segment(2)));
        Assert.True(File.Exists(Segment(3)));
        await store.AppendAsync(Event(channel: "new-month"));
        Assert.Single(File.ReadLines(Segment(3)));
        Assert.Equal(3, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task CachedListAvoidsReadingUnchangedSegmentsAndIncludesNewAppends()
    {
        Directory.CreateDirectory(_directory);
        var store = new AuditStore(_directory);
        await store.ListAsync();
        await File.WriteAllTextAsync(Segment(1), Line(Event(DateTimeOffset.UtcNow.AddMinutes(-10))));
        await File.WriteAllTextAsync(Segment(2), Line(Event(DateTimeOffset.UtcNow.AddMinutes(-9))));
        Assert.Equal(2, (await store.ListAsync()).Count);
        // Exclusive read sharing exposes accidental content rereads on Windows.
        await using var closed = new FileStream(Segment(1), FileMode.Open, FileAccess.Read, FileShare.None);
        await store.AppendManyAsync(Enumerable.Range(0, 510).Select(index => Event(DateTimeOffset.UtcNow.AddSeconds(index))));
        var listed = await new AuditStore(_directory).ListAsync();
        Assert.Equal(500, listed.Count);
        Assert.True(listed[0].Timestamp > listed[^1].Timestamp);
        Assert.DoesNotContain(listed, record => record.Timestamp < DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ReducedSegmentSizeEnforcesTotalByteCapacity()
    {
        Directory.CreateDirectory(_directory);
        var preferences = new LogRetentionPreferencesStore(_directory);
        await preferences.SaveAsync(new() { AuditSegmentSizeMb = 4, AuditMaxSegments = 4 });
        var store = new AuditStore(_directory);
        await store.AppendManyAsync(Enumerable.Range(0, 1600).Select(index => Event(DateTimeOffset.UtcNow.AddTicks(index), new string('\u3042', 256))));
        Assert.True(new FileInfo(Segment(1)).Length > 2 * 1024 * 1024);
        await preferences.SaveAsync(new() { AuditSegmentSizeMb = 1, AuditMaxSegments = 2 });
        await store.ApplyRetentionAsync();
        Assert.False(File.Exists(Segment(1)));
        Assert.InRange(Directory.GetFiles(_directory, "audit-segment-*.jsonl").Sum(path => new FileInfo(path).Length), 0, 2 * 1024 * 1024);
        await store.AppendAsync(Event());
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task ClearAndInspectIncludeOnlyExactOwnedAuditNames()
    {
        Directory.CreateDirectory(_directory);
        await new AuditStore(_directory).AppendAsync(Event());
        var knownTemp = Path.Combine(_directory, $"audit-segment-{9:D20}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(knownTemp, "orphan");
        var unrelated = Path.Combine(_directory, "audit-segment-1.jsonl");
        await File.WriteAllTextAsync(unrelated, "keep");
        var manager = new LocalDataManager(_directory);
        Assert.Equal(3, Assert.Single(manager.Inspect(), size => size.Kind == LocalDataKind.ExecutionHistory).Files);
        await manager.ClearAsync([LocalDataKind.ExecutionHistory]);
        Assert.False(File.Exists(knownTemp));
        Assert.False(File.Exists(Manifest));
        Assert.False(File.Exists(Segment(1)));
        Assert.True(File.Exists(unrelated));
        Assert.Empty(await new AuditStore(_directory).ListAsync());
    }

    [Fact]
    public async Task LinkedAuditSegmentAndLinkedAncestorAreRefused()
    {
        Directory.CreateDirectory(_directory);
        await new AuditStore(_directory).ListAsync();
        var outside = Path.Combine(Path.GetTempPath(), "cab-audit-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "data.jsonl");
        await File.WriteAllTextAsync(target, Line(Event()));
        var alias = Path.Combine(_directory, "alias");
        try
        {
            try { File.CreateSymbolicLink(Segment(1), target); Directory.CreateSymbolicLink(alias, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException) { return; }
            var before = await File.ReadAllBytesAsync(target);
            await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).AppendAsync(Event()));
            await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(_directory).ApplyRetentionAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => new AuditStore(Path.Combine(alias, "child")).AppendAsync(Event()));
            await Assert.ThrowsAsync<InvalidDataException>(() => new LocalDataManager(_directory).ClearAsync([LocalDataKind.ExecutionHistory]));
            Assert.Equal(before, await File.ReadAllBytesAsync(target));
        }
        finally
        {
            if (File.Exists(Segment(1))) File.Delete(Segment(1));
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(outside, true);
        }
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_directory);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("cab-audit-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
