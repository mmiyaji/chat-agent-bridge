using System.Text.Json;
using ChatAgentBridge_Desktop.Notifications;

namespace ChatAgentBridge.Core.Tests;

public sealed class NotificationDiagnosticsTests
{
    [Fact]
    public void Write_stores_only_hashes_and_exception_metadata()
    {
        var directory = CreateTemporaryDirectory();
        const string token = "secret-notification-token-74d12";
        const string exceptionMessage = "private chat body that must never be logged";
        try
        {
            using (var diagnostics = new NotificationDiagnostics(directory))
                diagnostics.Write("notification_approval", token, "approve", "completed", new InvalidOperationException(exceptionMessage));

            var content = File.ReadAllText(CurrentLog(directory));
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
    public void Write_accepts_fixed_dotted_events_and_normalizes_fixed_outcomes()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using (var diagnostics = new NotificationDiagnostics(directory))
            {
                diagnostics.Write("activation.received", token: new string('t', 129), action: "approve-all", outcome: "Completed");
                diagnostics.Write(new string('z', 500), outcome: new string('x', 500));
            }

            var records = File.ReadAllLines(CurrentLog(directory)).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
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
    public void Rotation_and_process_retention_stay_bounded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var diagnosticsDirectory = Path.Combine(directory, "diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            for (var processId = 700000; processId < 700012; processId++)
            {
                var oldFile = Path.Combine(diagnosticsDirectory, $"notification-events-{processId}.jsonl");
                File.WriteAllText(oldFile, "old\n");
                File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-1 - processId % 365));
                File.WriteAllText(oldFile + ".bak", "old backup\n");
            }
            var unrelated = Path.Combine(diagnosticsDirectory, "notification-events-not-a-pid.jsonl");
            File.WriteAllText(unrelated, "keep\n");
            File.WriteAllBytes(CurrentLog(directory), new byte[128 * 1024 + 1]);
            File.WriteAllBytes(CurrentLog(directory) + ".bak", new byte[128 * 1024 + 1]);

            using (var diagnostics = new NotificationDiagnostics(directory))
                for (var i = 0; i < 1800; i++)
                    diagnostics.Write("rotation_check", outcome: "success");

            var current = CurrentLog(directory);
            var backup = current + ".bak";
            Assert.True(new FileInfo(current).Length <= 128 * 1024);
            Assert.True(new FileInfo(backup).Length <= 128 * 1024);
            var processLogs = Directory.GetFiles(diagnosticsDirectory, "notification-events-*.jsonl")
                .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^notification-events-[0-9]+\\.jsonl$"))
                .ToArray();
            Assert.True(processLogs.Length <= 8);
            Assert.True(File.Exists(unrelated));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Write_does_not_follow_diagnostics_reparse_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(parent, "data");
        var outsideDirectory = Path.Combine(parent, "outside");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(outsideDirectory);
        var diagnosticsLink = Path.Combine(dataDirectory, "diagnostics");
        try
        {
            try { Directory.CreateSymbolicLink(diagnosticsLink, outsideDirectory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }

            using (var diagnostics = new NotificationDiagnostics(dataDirectory))
                diagnostics.Write("notification_approval", token: "secret");

            Assert.Empty(Directory.GetFiles(outsideDirectory));
        }
        finally
        {
            if (Directory.Exists(diagnosticsLink)) Directory.Delete(diagnosticsLink);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Write_swallows_filesystem_failures()
    {
        var parent = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(parent, "data-is-a-file");
        File.WriteAllText(dataDirectory, "block directory creation");
        try
        {
            using var diagnostics = new NotificationDiagnostics(dataDirectory);
            var error = Record.Exception((Action)(() => diagnostics.Write(new string('x', 4096), token: "token", error: new Exception("message"))));
            Assert.Null(error);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static string CurrentLog(string dataDirectory) => Path.Combine(dataDirectory, "diagnostics", $"notification-events-{Environment.ProcessId}.jsonl");

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-notification-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
