using System.Collections.Concurrent;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Maintenance;

/// <summary>Serializes settings changes and scheduled cleanup without stopping chat execution.</summary>
public sealed class LogRetentionMaintenance(string? directory = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _directory = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);

    public Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default) => RunAsync(null, ct);
    public Task<IReadOnlyList<string>> SaveAndApplyAsync(LogRetentionPreferences preferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        return RunAsync(preferences, ct);
    }

    private Task<IReadOnlyList<string>> RunAsync(LogRetentionPreferences? updated, CancellationToken ct) => Task.Run(async () =>
    {
        var gate = Gates.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Validate fixed application-owned targets before settings changes or cleanup.
            _ = new LocalDataManager(_directory).Inspect();
            EnsureSafePreferencesPath();
            var store = new LogRetentionPreferencesStore(_directory);
            if (updated is not null) await store.SaveAsync(updated, ct).ConfigureAwait(false);
            else _ = await store.LoadAsync(ct).ConfigureAwait(false);
            List<string> failures = [];
            async Task Apply(string category, Func<Task> action)
            {
                try { ct.ThrowIfCancellationRequested(); await action().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { failures.Add(category); }
            }
            // Aggregate retained details before reducing history, then prune statistics independently.
            await Apply("history", () => new ExecutionHistoryStore(_directory).ApplyRetentionAsync(ct));
            await Apply("statistics", () => new ActivityStatisticsStore(_directory).ApplyRetentionAsync(ct));
            await Apply("conversations", () => new InboxConversationStore(_directory).ApplyRetentionAsync(ct));
            if (File.Exists(Path.Combine(_directory, "audit.json")) || File.Exists(Path.Combine(_directory, "audit-manifest.json")) ||
                Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "audit-segment-*.jsonl").Any())
                await Apply("audit", () => new AuditStore(_directory).ApplyRetentionAsync(ct));
            return (IReadOnlyList<string>)failures.ToArray();
        }
        finally { gate.Release(); }
    }, ct);

    private void EnsureSafePreferencesPath()
    {
        for (var current = Path.Combine(_directory, "log-retention.json"); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked retention settings are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public sealed class LogRetentionApplyException : IOException
{
    public LogRetentionApplyException() : base("Preferences were saved, but some retention work could not finish.") { }
}
