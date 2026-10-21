using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>Writes bounded, privacy-preserving local diagnostics for notification actions.</summary>
public sealed class NotificationDiagnostics : IDisposable
{
    private const long MaximumFileBytes = 128 * 1024;
    private const int RetainedProcessFiles = 8;
    private const string FilePrefix = "notification-events-";
    private static readonly Regex EventPattern = new("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RetainedFilePattern = new("^notification-events-[0-9]+\\.jsonl$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RetainedBackupPattern = new("^notification-events-[0-9]+\\.jsonl\\.bak$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string ExecutablePathHash = HashText(Environment.ProcessPath ?? string.Empty);
    private static readonly string ExecutableVersion = ReadExecutableVersion();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _logPath;
    private readonly string _backupPath;
    private readonly object _writeLock;
    private bool _disposed;

    public NotificationDiagnostics(string dataDirectory)
    {
        try
        {
            var fullDataDirectory = Path.GetFullPath(dataDirectory ?? string.Empty);
            _logPath = Path.Combine(fullDataDirectory, "diagnostics", $"{FilePrefix}{Environment.ProcessId}.jsonl");
        }
        catch { _logPath = string.Empty; }
        _backupPath = _logPath + ".bak";
        _writeLock = Locks.GetOrAdd(_logPath, static _ => new object());
    }

    /// <summary>Appends one sanitized event. Logging errors are intentionally ignored.</summary>
    public void Write(string eventName, string? token = null, string? action = null, string? outcome = null, Exception? error = null)
    {
        try
        {
            if (_disposed) return;
            if (_logPath.Length == 0) return;
            var record = new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                ExecutableVersion,
                ExecutablePathHash,
                SanitizeEvent(eventName),
                SanitizeAction(action),
                HashToken(token),
                SanitizeOutcome(outcome),
                SanitizeErrorType(error),
                error?.HResult);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            var line = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, line, 0, bytes.Length);
            line[^1] = (byte)'\n';

            lock (_writeLock)
            {
                if (_disposed || !EnsureSafeDirectory(Path.GetDirectoryName(_logPath)!)) return;
                if (!IsSafeFile(_logPath) || !IsSafeFile(_backupPath)) return;

                var existingLength = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
                if (existingLength + line.Length > MaximumFileBytes)
                {
                    if (File.Exists(_backupPath)) File.Delete(_backupPath);
                    if (File.Exists(_logPath))
                    {
                        if (existingLength <= MaximumFileBytes) File.Move(_logPath, _backupPath);
                        else File.Delete(_logPath);
                    }
                    existingLength = 0;
                }

                if (line.Length > MaximumFileBytes) return;
                using (var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    stream.Write(line);
                TrimOldProcessFiles(Path.GetDirectoryName(_logPath)!);
            }
        }
        catch
        {
            // Diagnostics must never interfere with notification decisions.
        }
    }

    public void Dispose()
    {
        try { lock (_writeLock) _disposed = true; }
        catch { _disposed = true; }
    }

    private static string SanitizeEvent(string? value) =>
        value is not null && value.Length <= 64 && EventPattern.IsMatch(value) ? value : "invalid";

    private static string? SanitizeAction(string? value) => value switch
    {
        null => null,
        "approve" or "decline" or "open" => value,
        _ => "invalid"
    };

    private static string? SanitizeOutcome(string? value)
    {
        if (value is null) return null;
        if (value.Length > 32) return "other";
        return value.ToLowerInvariant() switch
        {
            "completed" or "opened" or "busy" or "unavailable" or "expired" or "missing" or "invalid_action" or
            "success" or "failed" or "error" or "accepted" or "rejected" or "local" or "legacy" or
            "other_owner" or "native" or "relay" or "relay_only" or "normal" => value.ToLowerInvariant(),
            _ => "other"
        };
    }

    private static string? HashToken(string? token) => string.IsNullOrEmpty(token) || token.Length > 128 ? null : HashText(token)[..12];

    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? SanitizeErrorType(Exception? error)
    {
        if (error is null) return null;
        var type = error.GetType().Name;
        return type.Length <= 64 ? type : type[..64];
    }

    private static string ReadExecutableVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            var version = path is null ? null : FileVersionInfo.GetVersionInfo(path).FileVersion;
            version ??= typeof(NotificationDiagnostics).Assembly.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(version)) return "unknown";
            return version.Length <= 64 ? version : version[..64];
        }
        catch { return "unknown"; }
    }

    private static bool EnsureSafeDirectory(string directoryPath)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return false;
        var current = root;
        if (Directory.Exists(current) && !IsSafeDirectory(current)) return false;
        foreach (var component in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current))
            {
                if (File.Exists(current)) return false;
                Directory.CreateDirectory(current);
            }
            if (!IsSafeDirectory(current)) return false;
        }
        return true;
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }

    private static bool IsSafeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return !Directory.Exists(path);
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch { return false; }
    }

    private static void TrimOldProcessFiles(string directory)
    {
        // This exact pattern excludes backups and unrelated files. Keep at most eight process logs.
        var files = Directory.EnumerateFiles(directory, FilePrefix + "*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(path => RetainedFilePattern.IsMatch(Path.GetFileName(path)) && IsSafeFile(path))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(RetainedProcessFiles)
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedPids = files.Select(path => Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..]).ToHashSet(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            if (RetainedFilePattern.IsMatch(Path.GetFileName(path)) && !files.Contains(path) && IsSafeFile(path))
                File.Delete(path);
        }
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.jsonl.bak", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!RetainedBackupPattern.IsMatch(name) || !IsSafeFile(path)) continue;
            var processId = name[FilePrefix.Length..^".jsonl.bak".Length];
            if (!retainedPids.Contains(processId)) File.Delete(path);
        }
    }

    private sealed record DiagnosticRecord(
        DateTimeOffset TimestampUtc,
        int ProcessId,
        string ExecutableVersion,
        string ExePathHash,
        string Event,
        string? Action,
        string? TokenHash,
        string? Outcome,
        string? ErrorType,
        int? HResult);
}
