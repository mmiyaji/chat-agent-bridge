using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core.Migrations;

/// <summary>Runs registered migrations serially and durably records only validated completions.</summary>
public sealed class DataMigrationCoordinator
{
    private const int SchemaVersion = 1;
    private const int MaxLedgerBytes = 64 * 1024;
    private const int MaxLedgerEntries = 64;
    private static readonly Regex SafeId = new("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _directory;
    private readonly string _ledgerPath;
    private readonly IReadOnlyList<IDataMigration> _steps;

    public DataMigrationCoordinator(string directory, IReadOnlyList<IDataMigration> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(steps);
        _directory = Path.GetFullPath(directory);
        _ledgerPath = Path.Combine(_directory, "data-migrations.json");
        if (steps.Count > MaxLedgerEntries) throw new ArgumentException("At most 64 data migrations can be registered.", nameof(steps));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (step.Id is null || !SafeId.IsMatch(step.Id)) throw new ArgumentException("Migration IDs must use lowercase letters, digits, dots, and hyphens.", nameof(steps));
            if (step.Version <= 0) throw new ArgumentException("Migration versions must be positive.", nameof(steps));
            if (!ids.Add(step.Id)) throw new ArgumentException("Migration IDs must be unique.", nameof(steps));
        }
        _steps = steps.ToArray();
    }

    /// <summary>Runs migrations on a worker thread so filesystem work never blocks a UI caller.</summary>
    public Task RunAsync(IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task RunCoreAsync(IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafePath();
            var ledger = await ReadLedgerAsync(cancellationToken).ConfigureAwait(false);
            var completed = ledger.Completed!;
            // Keep completed definitions registered. Unknown IDs indicate a newer
            // data layout which this application cannot safely consume.
            if (completed.Any(entry => !_steps.Any(step => step.Id == entry.Id)))
                throw new InvalidDataException("This application does not recognize a completed data migration.");
            var previousVersions = new Dictionary<string, CompletedMigration?>(StringComparer.Ordinal);
            foreach (var step in _steps)
            {
                var previous = completed.SingleOrDefault(entry => entry.Id == step.Id);
                if (previous is not null && previous.Version > step.Version)
                    throw new InvalidDataException($"Migration '{step.Id}' was completed at a newer version and cannot be downgraded.");
                previousVersions.Add(step.Id, previous);
            }
            if (completed.Count + previousVersions.Count(pair => pair.Value is null) > MaxLedgerEntries)
                throw new InvalidDataException("Registered migrations and existing ledger entries exceed the supported ledger capacity.");

            for (var index = 0; index < _steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = _steps[index];
                var previous = previousVersions[step.Id];
                // Check each step only after preceding steps have completed. Later steps may
                // depend on the files or state produced earlier in this same run.
                var required = await step.IsRequiredAsync(_directory, cancellationToken).ConfigureAwait(false);
                var needsCommit = required || previous is null || previous.Version < step.Version;
                if (!needsCommit) continue;

                if (required)
                {
                    progress?.Report(new MigrationProgress(step.Id, index + 1, _steps.Count, MigrationPhase.Running));
                    await step.ExecuteAsync(_directory, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new MigrationProgress(step.Id, index + 1, _steps.Count, MigrationPhase.Validating));
                }
                // An unrecorded step is validated even if its source has already disappeared:
                // this recovers a crash between ExecuteAsync and the ledger commit.
                await step.ValidateAsync(_directory, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                completed.RemoveAll(entry => entry.Id == step.Id);
                completed.Add(new CompletedMigration
                {
                    Id = step.Id,
                    Version = step.Version,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
                await WriteLedgerAsync(ledger, cancellationToken).ConfigureAwait(false);
                if (required) progress?.Report(new MigrationProgress(step.Id, index + 1, _steps.Count, MigrationPhase.Completed));
            }
        }
        finally { gate.Release(); }
    }

    private async Task<LedgerDocument> ReadLedgerAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureNoLinks(_ledgerPath, includeTarget: true);
            await using var stream = new FileStream(_ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxLedgerBytes) throw new InvalidDataException("Migration ledger exceeds the supported size.");
                buffer.Write(chunk, 0, read);
            }
            var ledger = JsonSerializer.Deserialize<LedgerDocument>(buffer.ToArray(), JsonOptions);
            var completed = ledger?.Completed;
            if (ledger is null || ledger.SchemaVersion != SchemaVersion || completed is null || completed.Count > MaxLedgerEntries)
                throw new InvalidDataException("Migration ledger is incomplete, invalid, or uses an unsupported schema.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in completed)
            {
                if (entry is null || entry.Id is null || !SafeId.IsMatch(entry.Id) || entry.Version <= 0 || entry.CompletedAtUtc == default ||
                    entry.CompletedAtUtc.Offset != TimeSpan.Zero || !ids.Add(entry.Id))
                    throw new InvalidDataException("Migration ledger contains an invalid or duplicate completion entry.");
            }
            return ledger;
        }
        catch (FileNotFoundException) { return new LedgerDocument { SchemaVersion = SchemaVersion, Completed = [] }; }
        catch (DirectoryNotFoundException) { return new LedgerDocument { SchemaVersion = SchemaVersion, Completed = [] }; }
        catch (JsonException ex) { throw new InvalidDataException("Migration ledger contains invalid or incomplete JSON.", ex); }
    }

    private async Task WriteLedgerAsync(LedgerDocument ledger, CancellationToken cancellationToken)
    {
        EnsureSafePath();
        Directory.CreateDirectory(_directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
        if (bytes.Length > MaxLedgerBytes) throw new InvalidDataException("Migration ledger exceeds the supported size.");
        var tempPath = Path.Combine(_directory, $"data-migrations.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(tempPath, includeTarget: true);
            await AtomicFile.ReplaceAsync(tempPath, _ledgerPath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private void EnsureSafePath() => EnsureNoLinks(_directory, includeTarget: false);

    private static void EnsureNoLinks(string path, bool includeTarget)
    {
        var current = Path.GetFullPath(path);
        var first = true;
        var ancestorFound = false;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                ancestorFound = true;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Migration data paths cannot contain symbolic links or reparse points.");
                if ((!first || !includeTarget) && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Migration data path is not a directory.");
                first = false;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
            catch (FileNotFoundException) { first = false; current = Path.GetDirectoryName(current) ?? string.Empty; }
            catch (DirectoryNotFoundException) { first = false; current = Path.GetDirectoryName(current) ?? string.Empty; }
            if (string.IsNullOrEmpty(current)) break;
        }
        if (!ancestorFound) throw new IOException("Migration data path has no accessible ancestor.");
    }

    private sealed class LedgerDocument
    {
        [JsonRequired] public int? SchemaVersion { get; set; }
        [JsonRequired] public List<CompletedMigration>? Completed { get; set; }
    }

    private sealed class CompletedMigration
    {
        [JsonRequired] public string? Id { get; set; }
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public DateTimeOffset CompletedAtUtc { get; set; }
    }
}
