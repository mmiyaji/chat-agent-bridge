using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Remembers files discovered during successful runs, separately for each conversation and workspace.</summary>
public sealed class AttachmentConversationStore
{
    private const int MaxEntries = 500;
    private const int MaxFilesPerConversation = 100;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _filePath;

    public AttachmentConversationStore(string? directory = null) =>
        _filePath = Path.Combine(new SettingsStore(directory).DirectoryPath, "attachment-conversations.json");

    /// <summary>Records only files returned by a complete discovery scan.</summary>
    public async Task RecordDiscoveredAsync(SessionKey key, string workspace,
        IReadOnlyList<FileAttachmentCandidate> candidates, bool scanComplete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!scanComplete || candidates.Count == 0) return;
        var canonicalWorkspace = CanonicalPath(workspace);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            // Timestamp-only legacy entries cannot grant access and must not evict usable records.
            entries.RemoveAll(entry => entry is null || entry.Workspace is null);
            var existing = entries.FirstOrDefault(entry => Matches(entry, key, canonicalWorkspace));
            if (existing is null)
            {
                existing = new Entry(key.Provider, key.ChannelId, key.ThreadId, key.UserId, key.Origin, canonicalWorkspace, []);
                entries.Add(existing);
            }

            var files = existing.Files?.ToList() ?? [];
            foreach (var candidate in candidates.Take(MaxFilesPerConversation))
            {
                if (!IsValidCandidate(candidate, canonicalWorkspace)) continue;
                var index = files.FindIndex(file => PathEquals(file.FullPath, candidate.FullPath));
                if (index >= 0) files.RemoveAt(index);
                files.Add(candidate);
            }
            if (files.Count > MaxFilesPerConversation) files.RemoveRange(0, files.Count - MaxFilesPerConversation);
            var updated = existing with { Files = files };
            entries.Remove(existing);
            entries.Add(updated);
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<FileAttachmentCandidate>> GetDiscoveredAsync(SessionKey key, string workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var canonicalWorkspace = CanonicalPath(workspace);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(candidate => Matches(candidate, key, canonicalWorkspace));
            var files = entry?.Files?.Where(file => IsValidCandidate(file, canonicalWorkspace)).ToArray() ?? [];
            // Preserve active conversations even when they only reuse previously discovered files.
            if (entry is not null && files.Length > 0 && !ReferenceEquals(entry, entries[^1]))
            {
                entries.Remove(entry);
                entries.Add(entry);
                await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
            }
            return files;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Removes all workspace records for exactly one conversation.</summary>
    public async Task ClearAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken, failOnInvalid: true).ConfigureAwait(false);
            if (entries.RemoveAll(entry => MatchesConversation(entry, key)) > 0)
                await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<List<Entry>> LoadAsync(CancellationToken cancellationToken, bool failOnInvalid = false)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<Entry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException ex) { if (failOnInvalid) throw new InvalidDataException("Attachment conversation data is unreadable.", ex); return []; }
        catch (NotSupportedException ex) { if (failOnInvalid) throw new InvalidDataException("Attachment conversation data is unreadable.", ex); return []; }
    }

    private async Task SaveAsync(List<Entry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entries), cancellationToken).ConfigureAwait(false);
            await AtomicFile.ReplaceAsync(temp, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static bool Matches(Entry entry, SessionKey key, string workspace) =>
        entry.Workspace is not null && PathEquals(entry.Workspace, workspace) &&
        MatchesConversation(entry, key);

    private static bool MatchesConversation(Entry entry, SessionKey key) =>
        entry.Provider == key.Provider && entry.ChannelId == key.ChannelId && entry.ThreadId == key.ThreadId &&
        entry.UserId == key.UserId && entry.Origin == key.Origin;

    private static bool IsValidCandidate(FileAttachmentCandidate? candidate, string workspace)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.FullPath) || string.IsNullOrWhiteSpace(candidate.RelativePath) ||
            candidate.Length < 0 || candidate.Sha256 is not { Length: 64 } hash || hash.Any(ch => !Uri.IsHexDigit(ch))) return false;
        try
        {
            var full = Path.GetFullPath(candidate.FullPath);
            var relative = Path.GetRelativePath(workspace, full);
            return PathEquals(full, candidate.FullPath) &&
                !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                string.Equals(relative, candidate.RelativePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(CanonicalPath(a), CanonicalPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // StartUtc-only records from older versions deserialize with no workspace or file ledger and grant no eligibility.
    private sealed record Entry(string Provider, string ChannelId, string ThreadId, string? UserId, string? Origin,
        string? Workspace, IReadOnlyList<FileAttachmentCandidate>? Files, DateTime? StartUtc = null);
}
