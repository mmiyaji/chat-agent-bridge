using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Stores a bounded set of attachment approval fingerprints in the app data directory.</summary>
public sealed class AttachmentConsentStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxEntries = 200;
    private const int MaxFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _filePath;

    public AttachmentConsentStore(string? directory = null)
    {
        DirectoryPath = new SettingsStore(directory).DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "attachment-consents.json");
    }

    public string DirectoryPath { get; }

    public async Task<bool> IsApprovedAsync(SessionKey key, string sessionId, string fingerprint, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(key, sessionId, fingerprint);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
                return entries.Any(entry => Matches(entry, key, sessionId, fingerprint));
            }
            catch (InvalidDataException) { return false; }
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(SessionKey key, string sessionId, string fingerprint, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(key, sessionId, fingerprint);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(entry => Matches(entry, key, sessionId, fingerprint));
            entries.Add(new ConsentEntry(key.Provider, key.ChannelId, key.ThreadId, key.UserId, key.Origin, sessionId, fingerprint, DateTimeOffset.UtcNow));
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            await SaveEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task RevokeAsync(SessionKey key, string sessionId, string fingerprint, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(key, sessionId, fingerprint);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
            if (entries.RemoveAll(entry => Matches(entry, key, sessionId, fingerprint)) > 0)
                await SaveEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<List<ConsentEntry>> LoadEntriesAsync(CancellationToken token)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var info = new FileInfo(_filePath);
            if (info.Length is < 1 or > MaxFileBytes) throw new InvalidDataException("Attachment consent file exceeds its size limit.");
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaxFileBytes) throw new InvalidDataException("Attachment consent file exceeds its size limit.");
            var document = await JsonSerializer.DeserializeAsync<ConsentDocument>(stream, JsonOptions, token).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Entries is null || document.Entries.Count > MaxEntries)
                throw new InvalidDataException("Attachment consent file has an unsupported schema or entry count.");
            foreach (var entry in document.Entries)
                if (!IsValidEntry(entry)) throw new InvalidDataException("Attachment consent file contains an invalid entry.");
            return document.Entries;
        }
        catch (JsonException ex) { throw new InvalidDataException("Attachment consent file contains invalid JSON or fields.", ex); }
    }

    private async Task SaveEntriesAsync(List<ConsentEntry> entries, CancellationToken token)
    {
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"attachment-consents.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new ConsentDocument { SchemaVersion = CurrentSchemaVersion, Entries = entries }, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, token).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static bool IsValidEntry(ConsentEntry? entry) => entry is not null &&
        HasText(entry.Provider, 128) && HasText(entry.ChannelId, 512) && HasText(entry.ThreadId, 512) &&
        (entry.UserId is null || entry.UserId.Length <= 512) && (entry.Origin is null || entry.Origin.Length <= 512) &&
        HasText(entry.SessionId, 512) && IsFingerprint(entry.Fingerprint) && entry.ApprovedAtUtc != default && entry.ApprovedAtUtc.Offset == TimeSpan.Zero;

    private static void ValidateIdentity(SessionKey key, string sessionId, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!HasText(key.Provider, 128) || !HasText(key.ChannelId, 512) || !HasText(key.ThreadId, 512) || !HasText(sessionId, 512) ||
            (key.UserId is not null && key.UserId.Length > 512) || (key.Origin is not null && key.Origin.Length > 512))
            throw new ArgumentException("Attachment consent requires a complete conversation and native session identity.");
        if (!IsFingerprint(fingerprint)) throw new ArgumentException("Attachment consent fingerprint must be a SHA-256 digest.", nameof(fingerprint));
    }

    private static bool Matches(ConsentEntry entry, SessionKey key, string sessionId, string fingerprint) =>
        string.Equals(entry.Provider, key.Provider, StringComparison.Ordinal) &&
        string.Equals(entry.ChannelId, key.ChannelId, StringComparison.Ordinal) &&
        string.Equals(entry.ThreadId, key.ThreadId, StringComparison.Ordinal) &&
        string.Equals(entry.UserId, key.UserId, StringComparison.Ordinal) &&
        string.Equals(entry.Origin, key.Origin, StringComparison.Ordinal) &&
        string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(entry.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool HasText(string? value, int maxLength) => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private sealed class ConsentDocument
    {
        public int SchemaVersion { get; set; }
        public List<ConsentEntry>? Entries { get; set; }
    }

    private sealed record ConsentEntry(string Provider, string ChannelId, string ThreadId, string? UserId, string? Origin, string SessionId, string Fingerprint, DateTimeOffset ApprovedAtUtc);
}
