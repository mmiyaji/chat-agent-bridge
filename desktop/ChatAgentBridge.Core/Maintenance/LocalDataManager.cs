namespace ChatAgentBridge.Core.Maintenance;

public enum LocalDataKind { ExecutionHistory, Conversations, UsageStatistics, Diagnostics }
public sealed record LocalDataSize(LocalDataKind Kind, long Bytes, int Files);

/// <summary>Manages only fixed application-owned files. Caller must first quiesce all work and connections.</summary>
public sealed class LocalDataManager(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private static readonly string[] ConversationFiles =
        ["sessions.json", "conversation-summaries.json", "chat-conversations.json", "attachment-consents.json", "chat-identities.json"];
    private static readonly System.Text.RegularExpressions.Regex ConversationTemporaryFileName = new(
        "^(?:sessions|conversation-summaries|chat-conversations|attachment-consents|chat-identities)\\.[0-9a-fA-F]{32}\\.tmp\\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    public IReadOnlyList<LocalDataSize> Inspect() => Enum.GetValues<LocalDataKind>().Select(kind =>
    {
        var files = Files(kind).Where(File.Exists).ToArray();
        return new LocalDataSize(kind, files.Sum(path => new FileInfo(path).Length), files.Length);
    }).ToArray();

    public void CheckSettingsPaths()
    {
        EnsureSafe(Path.Combine(_directory, "settings.json"));
        EnsureSafe(Path.Combine(_directory, "settings-before-restore.json"));
        EnsureSafe(Path.Combine(_directory, "settings-original-before-restore.json"));
    }

    public async Task ClearAsync(IReadOnlyCollection<LocalDataKind> selected, CancellationToken ct = default)
    {
        if (selected.Count == 0 || selected.Any(kind => !Enum.IsDefined(kind)))
            throw new ArgumentException("Select known data categories.", nameof(selected));
        // Validate every selected target before migration reads or aggregate writes as well as deletion.
        foreach (var kind in selected) _ = Files(kind).ToArray();
        if (selected.Contains(LocalDataKind.ExecutionHistory))
        {
            EnsureSafe(Path.Combine(_directory, "activity-statistics.json"));
            EnsureSafe(Path.Combine(_directory, "activity-statistics.journal.jsonl"));
        }
        // Initialize statistics before erasing detail, so keeping statistics never loses its migration source.
        if (selected.Contains(LocalDataKind.ExecutionHistory) && !selected.Contains(LocalDataKind.UsageStatistics))
        {
            var history = await new History.ExecutionHistoryStore(_directory).ListAsync(ct);
            await new History.ActivityStatisticsStore(_directory).MigrateAsync(history, ct);
        }
        var files = selected.Where(k => k != LocalDataKind.UsageStatistics).SelectMany(Files).Distinct().ToArray();
        foreach (var file in files) EnsureSafe(file);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSafe(file);
            if (File.Exists(file)) File.Delete(file);
        }
        if (selected.Contains(LocalDataKind.UsageStatistics))
        {
            EnsureSafe(Path.Combine(_directory, "activity-statistics.json"));
            EnsureSafe(Path.Combine(_directory, "activity-statistics.journal.jsonl"));
            await new History.ActivityStatisticsStore(_directory).ClearAsync(ct);
        }
    }

    private IEnumerable<string> Files(LocalDataKind kind)
    {
        EnsureSafe(_directory);
        if (kind == LocalDataKind.Diagnostics)
        {
            var diagnostics = Path.Combine(_directory, "diagnostics");
            EnsureSafe(diagnostics);
            return Directory.Exists(diagnostics)
                ? Directory.EnumerateFiles(diagnostics).Where(path =>
                    System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),
                        "^notification-events-[0-9]+\\.jsonl(?:\\.bak)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    .Select(Checked).ToArray()
                : [];
        }
        if (kind == LocalDataKind.Conversations)
            return ConversationFiles
                .Select(name => Checked(Path.Combine(_directory, name)))
                .Concat(ConversationTemporaryFiles()).ToArray();
        if (kind == LocalDataKind.ExecutionHistory)
            return new[] { "history.json", "audit.json" }.Select(name => Checked(Path.Combine(_directory, name)))
                .Concat(Directory.Exists(_directory)
                    ? Directory.EnumerateFiles(_directory, "audit-*", SearchOption.TopDirectoryOnly)
                        .Where(path => History.AuditStore.OwnedFileName.IsMatch(Path.GetFileName(path))).Select(Checked)
                    : []).ToArray();
        string[] names = kind switch
        {
            LocalDataKind.UsageStatistics => ["activity-statistics.json", "activity-statistics.journal.jsonl"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return names.Select(name => Checked(Path.Combine(_directory, name))).ToArray();
    }

    private IEnumerable<string> ConversationTemporaryFiles()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ConversationTemporaryFileName.IsMatch(Path.GetFileName(path)))
            .Select(Checked).ToArray();
    }

    private string Checked(string path) { EnsureSafe(path); return path; }
    private void EnsureSafe(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(_directory, comparison) && !full.StartsWith(Path.TrimEndingDirectorySeparator(_directory) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Data path escaped its directory.");
        // Inspect ancestors too: a data directory under a junction must not become an erasure target.
        for (var item = new FileInfo(full) as FileSystemInfo; item is not null; item = Directory.GetParent(item.FullName))
        {
            try
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked data locations cannot be managed here.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
