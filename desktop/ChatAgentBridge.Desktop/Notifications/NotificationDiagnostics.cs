using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ChatAgentBridge.Core;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>Writes bounded, privacy-preserving local diagnostics for notification actions.</summary>
public sealed class NotificationDiagnostics : IDisposable
{
    private const string FilePrefix = "notification-events-";
    private const int QueueCapacity = 256;
    private static readonly Regex EventPattern = new("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LogNamePattern = new("^notification-events-([0-9]+)\\.jsonl$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BackupNamePattern = new("^notification-events-([0-9]+)\\.jsonl\\.bak$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string ExecutablePathHash = HashText(Environment.ProcessPath ?? string.Empty);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> DirectoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkerPolicyState> WorkerPolicyStates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _dataDirectory;
    private readonly string _diagnosticsDirectory;
    private readonly string _logPath;
    private readonly string _backupPath;
    private readonly Channel<QueueItem> _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly object _lifecycleLock = new();
    private readonly Task _worker;
    private bool _disposed;

    public NotificationDiagnostics(string dataDirectory)
    {
        try
        {
            _dataDirectory = Path.GetFullPath(dataDirectory ?? string.Empty);
            _diagnosticsDirectory = Path.Combine(_dataDirectory, "diagnostics");
            _logPath = Path.Combine(_diagnosticsDirectory, $"{FilePrefix}{Environment.ProcessId}.jsonl");
            _backupPath = _logPath + ".bak";
        }
        catch
        {
            _dataDirectory = _diagnosticsDirectory = _logPath = _backupPath = string.Empty;
        }
        _worker = Task.Run(WorkerAsync);
    }

    /// <summary>Sanitizes and enqueues one event without waiting for disk or policy I/O.</summary>
    public void Write(string eventName, string? token = null, string? action = null, string? outcome = null, Exception? error = null)
    {
        if (_logPath.Length == 0) return;
        try
        {
            var item = new EventItem(
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                SanitizeEvent(eventName),
                SanitizeAction(action),
                HashToken(token),
                SanitizeOutcome(outcome),
                SanitizeErrorType(error),
                error?.HResult);

            lock (_lifecycleLock)
            {
                if (!_disposed) _queue.Writer.TryWrite(item);
            }
        }
        catch { /* Diagnostics never interfere with notification decisions. */ }
    }

    /// <summary>Waits until all events accepted before this call have been processed.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask enqueue;
        bool disposed;
        lock (_lifecycleLock)
        {
            disposed = _disposed;
            if (disposed)
            {
                enqueue = ValueTask.CompletedTask;
            }
            else
            {
                enqueue = _queue.Writer.WriteAsync(new BarrierItem(barrier), cancellationToken);
            }
        }

        var channelClosed = false;
        try { await enqueue.ConfigureAwait(false); }
        catch (ChannelClosedException) { channelClosed = true; }
        if (disposed || channelClosed) await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        else await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a validated policy to exact notification log files in a data directory.</summary>
    public static Task ApplyRetentionAsync(string dataDirectory, LogRetentionPreferences preferences, CancellationToken cancellationToken = default) =>
        Task.Run(() => ApplyRetentionCoreAsync(dataDirectory, preferences, cancellationToken), cancellationToken);

    private static async Task ApplyRetentionCoreAsync(string dataDirectory, LogRetentionPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        var fullDataDirectory = Path.GetFullPath(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)));
        var diagnosticsDirectory = Path.Combine(fullDataDirectory, "diagnostics");
        var gate = DirectoryLocks.GetOrAdd(diagnosticsDirectory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(diagnosticsDirectory) || !HasSafeAncestors(diagnosticsDirectory)) return;
            ApplyPolicyToFiles(diagnosticsDirectory, preferences, DateTimeOffset.UtcNow, Environment.ProcessId);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Best effort maintenance; unsafe or unavailable paths are skipped. */ }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            var executableVersion = ReadExecutableVersion();
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item is BarrierItem barrier)
                {
                    barrier.Completion.TrySetResult();
                    continue;
                }

                if (item is not EventItem logEvent || _logPath.Length == 0) continue;
                try
                {
                    var preferences = await new LogRetentionPreferencesStore(_dataDirectory).LoadAsync().ConfigureAwait(false);
                    preferences.Validate();
                    var gate = DirectoryLocks.GetOrAdd(_diagnosticsDirectory, static _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!EnsureSafeDirectory(_diagnosticsDirectory)) continue;
                        AppendUnderPolicy(SerializeLine(logEvent, executableVersion), preferences);
                        var now = DateTimeOffset.UtcNow;
                        var state = WorkerPolicyStates.GetOrAdd(_diagnosticsDirectory, static _ => new WorkerPolicyState());
                        if (state.ShouldApply(preferences, now))
                            ApplyPolicyToFiles(_diagnosticsDirectory, preferences, now, Environment.ProcessId);
                    }
                    finally { gate.Release(); }
                }
                catch
                {
                    // Includes invalid/corrupt/unavailable policy: never write or prune without policy.
                }
            }
        }
        catch { /* Shutdown remains best effort. */ }
        finally
        {
            while (_queue.Reader.TryRead(out var item))
                if (item is BarrierItem barrier) barrier.Completion.TrySetResult();
        }
    }

    private void AppendUnderPolicy(byte[] line, LogRetentionPreferences preferences)
    {
        var maximumBytes = checked((long)preferences.DiagnosticsFileKiB * 1024);
        if (line.LongLength > maximumBytes || !IsSafeFile(_logPath) || !IsSafeFile(_backupPath)) return;

        var existingLength = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
        if (existingLength + line.LongLength > maximumBytes)
        {
            if (File.Exists(_backupPath)) File.Delete(_backupPath);
            if (File.Exists(_logPath))
            {
                if (existingLength <= maximumBytes) File.Move(_logPath, _backupPath);
                else File.Delete(_logPath);
            }
        }

        using (var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write(line);
    }

    private static void ApplyPolicyToFiles(string directory, LogRetentionPreferences preferences, DateTimeOffset now, int activeProcessId)
    {
        if (!HasSafeAncestors(directory)) return;
        var cutoff = now.ToUniversalTime().AddDays(-preferences.DiagnosticsRetentionDays).UtcDateTime;
        var groups = EnumerateGroups(directory);

        foreach (var group in groups)
        {
            var active = group.ProcessId == activeProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!active && group.Paths.All(path => File.GetLastWriteTimeUtc(path) < cutoff))
            {
                DeleteGroup(group);
                continue;
            }

            foreach (var path in group.Paths)
            {
                if (!IsSafeFile(path)) continue;
                var isBackup = BackupNamePattern.IsMatch(Path.GetFileName(path));
                if (File.GetLastWriteTimeUtc(path) < cutoff && (!active || isBackup))
                {
                    TryDelete(path);
                    continue;
                }
                PruneLinesOlderThan(path, cutoff, preserveEmpty: active && !isBackup);
            }
        }

        groups = EnumerateGroups(directory);
        var retained = groups
            .OrderByDescending(group => group.ProcessId == activeProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ThenByDescending(group => group.LastWriteUtc)
            .Take(preferences.DiagnosticsMaxFiles)
            .Select(group => group.ProcessId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
            if (!retained.Contains(group.ProcessId)) DeleteGroup(group);
    }

    private static List<LogGroup> EnumerateGroups(string directory)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var match = LogNamePattern.Match(name);
            if (!match.Success) match = BackupNamePattern.Match(name);
            if (!match.Success || !IsSafeFile(path)) continue;
            var pid = match.Groups[1].Value;
            if (!groups.TryGetValue(pid, out var paths)) groups[pid] = paths = [];
            paths.Add(path);
        }
        return groups.Select(pair => new LogGroup(pair.Key, pair.Value,
                pair.Value.Max(path => File.GetLastWriteTimeUtc(path))))
            .ToList();
    }

    private static void DeleteGroup(LogGroup group)
    {
        foreach (var path in group.Paths)
            if (IsSafeFile(path)) TryDelete(path);
    }

    private static void PruneLinesOlderThan(string path, DateTime cutoffUtc, bool preserveEmpty)
    {
        if (!IsSafeFile(path)) return;
        try
        {
            var originalWriteTime = File.GetLastWriteTimeUtc(path);
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var kept = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("timestampUtc", out var timestamp) &&
                        timestamp.TryGetDateTimeOffset(out var value) && value.UtcDateTime >= cutoffUtc)
                        kept.Add(line);
                }
                catch { /* Invalid records cannot establish a valid retention timestamp. */ }
            }
            var replacement = path + ".retention-tmp";
            if (!preserveEmpty && kept.Count == 0)
            {
                TryDelete(path);
                return;
            }
            if (!IsSafeFile(replacement)) return;
            using (var stream = new FileStream(replacement, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                foreach (var line in kept) writer.WriteLine(line);
            File.Move(replacement, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, originalWriteTime);
        }
        catch { /* Best effort; a failure never broadens deletion. */ }
    }

    private static void TryDelete(string path)
    {
        try { if (IsSafeFile(path)) File.Delete(path); }
        catch { }
    }

    private static bool EnsureSafeDirectory(string directoryPath)
    {
        try
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
        catch { return false; }
    }

    private static bool HasSafeAncestors(string directoryPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;
            var current = root;
            if (!Directory.Exists(current) || !IsSafeDirectory(current)) return false;
            foreach (var component in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current) || !IsSafeDirectory(current)) return false;
            }
            return true;
        }
        catch { return false; }
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

    private static byte[] SerializeLine(EventItem item, string executableVersion)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DiagnosticRecord(
            item.TimestampUtc, item.ProcessId, executableVersion, ExecutablePathHash,
            item.Event, item.Action, item.TokenHash, item.Outcome, item.ErrorType, item.HResult), JsonOptions);
        var line = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, line, 0, bytes.Length);
        line[^1] = (byte)'\n';
        return line;
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

    private abstract record QueueItem;
    private sealed record EventItem(DateTimeOffset TimestampUtc, int ProcessId, string Event, string? Action,
        string? TokenHash, string? Outcome, string? ErrorType, int? HResult) : QueueItem;
    private sealed record BarrierItem(TaskCompletionSource Completion) : QueueItem;
    private sealed record LogGroup(string ProcessId, List<string> Paths, DateTime LastWriteUtc);

    private sealed class WorkerPolicyState
    {
        private LogRetentionPreferences? _lastPolicy;
        private DateTimeOffset _lastAppliedUtc;

        public bool ShouldApply(LogRetentionPreferences policy, DateTimeOffset now)
        {
            if (_lastPolicy == policy && now - _lastAppliedUtc < TimeSpan.FromMinutes(5)) return false;
            _lastPolicy = policy;
            _lastAppliedUtc = now;
            return true;
        }
    }
}
