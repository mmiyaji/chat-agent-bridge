using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge.Core.Tests;

public sealed class NotificationDiagnosticsTests
{
    [Fact]
    public async Task Write_stores_only_hashes_and_exception_metadata()
    {
        var directory = CreateTemporaryDirectory();
        const string token = "secret-notification-token-74d12";
        const string exceptionMessage = "private chat body that must never be logged";
        try
        {
            using var diagnostics = new NotificationDiagnostics(directory);
            diagnostics.Write("notification_approval", token, "approve", "completed", new InvalidOperationException(exceptionMessage));
            await diagnostics.FlushAsync();

            var content = await File.ReadAllTextAsync(CurrentLog(directory));
            Assert.DoesNotContain(token, content, StringComparison.Ordinal);
            Assert.DoesNotContain(exceptionMessage, content, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, content, StringComparison.OrdinalIgnoreCase);
            var record = JsonDocument.Parse(content).RootElement;
            Assert.Equal("notification_approval", record.GetProperty("event").GetString());
            Assert.Equal("approve", record.GetProperty("action").GetString());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..12], record.GetProperty("tokenHash").GetString());
            Assert.Equal("completed", record.GetProperty("outcome").GetString());
            Assert.Equal(nameof(InvalidOperationException), record.GetProperty("errorType").GetString());
            Assert.True(record.GetProperty("hResult").GetInt32() != 0);
            Assert.True(record.GetProperty("timestampUtc").GetDateTimeOffset().Offset == TimeSpan.Zero);
            Assert.Equal(Environment.ProcessId, record.GetProperty("processId").GetInt32());
            Assert.False(record.TryGetProperty("message", out _));
            Assert.False(record.TryGetProperty("stackTrace", out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Write_accepts_fixed_dotted_events_and_normalizes_fixed_outcomes()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var diagnostics = new NotificationDiagnostics(directory);
            diagnostics.Write("activation.received", token: new string('t', 129), action: "approve-all", outcome: "Completed");
            diagnostics.Write(new string('z', 500), outcome: new string('x', 500));
            await diagnostics.FlushAsync();

            var records = (await File.ReadAllLinesAsync(CurrentLog(directory))).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Equal("activation.received", records[0].GetProperty("event").GetString());
            Assert.Equal("invalid", records[0].GetProperty("action").GetString());
            Assert.Equal("completed", records[0].GetProperty("outcome").GetString());
            Assert.True(records[0].GetProperty("tokenHash").ValueKind == JsonValueKind.Null);
            Assert.Equal("invalid", records[1].GetProperty("event").GetString());
            Assert.Equal("other", records[1].GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Rotation_and_process_retention_stay_bounded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await SavePolicyAsync(directory, new LogRetentionPreferences { DiagnosticsFileKiB = 64, DiagnosticsMaxFiles = 8 });
            var diagnosticsDirectory = Path.Combine(directory, "diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            for (var processId = 700000; processId < 700012; processId++)
            {
                var oldFile = Path.Combine(diagnosticsDirectory, $"notification-events-{processId}.jsonl");
                File.WriteAllText(oldFile, "old\n");
                File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-1));
                File.WriteAllText(oldFile + ".bak", "old backup\n");
            }
            var unrelated = Path.Combine(diagnosticsDirectory, "notification-events-not-a-pid.jsonl");
            File.WriteAllText(unrelated, "keep\n");

            using var diagnostics = new NotificationDiagnostics(directory);
            for (var i = 0; i < 1800; i++)
            {
                diagnostics.Write("rotation_check", outcome: "success");
                if (i % 100 == 99) await diagnostics.FlushAsync();
            }
            await diagnostics.FlushAsync();

            var current = CurrentLog(directory);
            var backup = current + ".bak";
            Assert.True(new FileInfo(current).Length <= 64 * 1024);
            Assert.True(new FileInfo(backup).Length <= 64 * 1024);
            var processLogs = Directory.GetFiles(diagnosticsDirectory, "notification-events-*.jsonl")
                .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^notification-events-[0-9]+\\.jsonl$"))
                .ToArray();
            Assert.True(processLogs.Length <= 8);
            Assert.True(File.Exists(unrelated));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Retention_policy_changes_apply_after_restart_and_prune_age_and_process_groups()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var diagnosticsDirectory = Path.Combine(directory, "diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            var current = CurrentLog(directory);
            File.WriteAllText(current, "{\"timestampUtc\":\"2020-01-01T00:00:00+00:00\"}\n");
            File.WriteAllText(current + ".bak", "{\"timestampUtc\":\"2020-01-01T00:00:00+00:00\"}\n");
            var oldLog = Path.Combine(diagnosticsDirectory, "notification-events-710001.jsonl");
            File.WriteAllText(oldLog, "old log\n");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-20));
            var recentA = Path.Combine(diagnosticsDirectory, "notification-events-710002.jsonl");
            var recentB = Path.Combine(diagnosticsDirectory, "notification-events-710003.jsonl");
            var recentRecord = $$"""{"timestampUtc":"{{DateTimeOffset.UtcNow:O}}"}""" + "\n";
            File.WriteAllText(recentA, recentRecord);
            File.WriteAllText(recentB, recentRecord);
            File.SetLastWriteTimeUtc(recentA, DateTime.UtcNow.AddMinutes(-2));
            File.SetLastWriteTimeUtc(recentB, DateTime.UtcNow.AddMinutes(-1));
            await SavePolicyAsync(directory, new LogRetentionPreferences { DiagnosticsRetentionDays = 7, DiagnosticsMaxFiles = 2 });

            await NotificationDiagnostics.ApplyRetentionAsync(directory, await new LogRetentionPreferencesStore(directory).LoadAsync());

            Assert.True(File.Exists(current));
            Assert.False(File.Exists(current + ".bak"));
            Assert.False(File.Exists(oldLog));
            Assert.False(File.Exists(recentA));
            Assert.True(File.Exists(recentB));
            Assert.True(Directory.GetFiles(diagnosticsDirectory, "notification-events-*.jsonl*").Length <= 2);

            await SavePolicyAsync(directory, new LogRetentionPreferences { DiagnosticsFileKiB = 64, DiagnosticsRetentionDays = 1, DiagnosticsMaxFiles = 1 });
            using (var restarted = new NotificationDiagnostics(directory))
            {
                restarted.Write("after_restart", outcome: "success");
                await restarted.FlushAsync();
            }
            Assert.All(await File.ReadAllLinesAsync(current), line =>
                Assert.True(JsonDocument.Parse(line).RootElement.GetProperty("timestampUtc").GetDateTimeOffset() >= DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Invalid_policy_does_not_write_or_trim_files()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var diagnosticsDirectory = Path.Combine(directory, "diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            var current = CurrentLog(directory);
            await File.WriteAllTextAsync(current, "keep this file\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "log-retention.json"), "{broken");
            using var diagnostics = new NotificationDiagnostics(directory);
            diagnostics.Write("must_not_write");
            await diagnostics.FlushAsync();
            Assert.Equal("keep this file\n", await File.ReadAllTextAsync(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Apply_retention_refuses_reparse_ancestor_without_touching_outside_files()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(parent, "data");
        var outsideDirectory = Path.Combine(parent, "outside");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "notification-events-710004.jsonl");
        await File.WriteAllTextAsync(outsideFile, "preserve\n");
        var diagnosticsLink = Path.Combine(dataDirectory, "diagnostics");
        try
        {
            try { Directory.CreateSymbolicLink(diagnosticsLink, outsideDirectory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }

            await NotificationDiagnostics.ApplyRetentionAsync(dataDirectory, new LogRetentionPreferences { DiagnosticsRetentionDays = 1, DiagnosticsMaxFiles = 1 });
            using (var diagnostics = new NotificationDiagnostics(dataDirectory))
            {
                diagnostics.Write("notification_approval", token: "secret");
                await diagnostics.FlushAsync();
            }

            Assert.Equal("preserve\n", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            if (Directory.Exists(diagnosticsLink)) Directory.Delete(diagnosticsLink);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Write_swallows_filesystem_failures()
    {
        var parent = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(parent, "data-is-a-file");
        await File.WriteAllTextAsync(dataDirectory, "block directory creation");
        try
        {
            using var diagnostics = new NotificationDiagnostics(dataDirectory);
            var error = Record.Exception((Action)(() => diagnostics.Write(new string('x', 4096), token: "token", error: new Exception("message"))));
            Assert.Null(error);
            await diagnostics.FlushAsync();
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Flush_after_dispose_waits_for_worker_and_later_writes_are_ignored()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var diagnostics = new NotificationDiagnostics(directory);
            diagnostics.Write("accepted");
            diagnostics.Dispose();
            diagnostics.Write("ignored");
            await diagnostics.FlushAsync();
            var lines = await File.ReadAllLinesAsync(CurrentLog(directory));
            Assert.Single(lines);
            Assert.Equal("accepted", JsonDocument.Parse(lines[0]).RootElement.GetProperty("event").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task SavePolicyAsync(string directory, LogRetentionPreferences preferences) =>
        await new LogRetentionPreferencesStore(directory).SaveAsync(preferences);

    private static string CurrentLog(string dataDirectory) => Path.Combine(dataDirectory, "diagnostics", $"notification-events-{Environment.ProcessId}.jsonl");

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-notification-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
