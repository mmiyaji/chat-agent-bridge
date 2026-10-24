using System.Text.Json;
using System.Text.Json.Serialization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.History;

/// <summary>Metadata used to restore an inbox row. This snapshot deliberately contains no conversation content.</summary>
public sealed record InboxConversationSnapshot(SessionKey Key, string WorkspaceRoot, DateTimeOffset UpdatedAt,
    string State, int RequestCount, string? DisplayName = null, string? UserName = null,
    string? ChannelName = null, string? SiteName = null, bool IsDirectMessage = false);

/// <summary>Valid snapshot rows selected for persistence and the number rejected during selection.</summary>
public sealed record InboxConversationSaveSelection(IReadOnlyList<InboxConversationSnapshot> Entries, int RejectedCount);

/// <summary>
/// Persists bounded inbox display metadata only. Listing normalizes pending and running rows to
/// interrupted so a restored item is never presented as an actionable in-flight request.
/// </summary>
public sealed class InboxConversationStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxEntries = 500;
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> AllowedStates = new(StringComparer.Ordinal)
        { "pending", "running", "completed", "cancelled", "failed", "interrupted", "idle" };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _filePath;

    public InboxConversationStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(DirectoryPath, "chat-conversations.json");
    }

    public string DirectoryPath { get; }

    /// <summary>Returns whether a snapshot passes the same validation used by save and load.</summary>
    public static bool IsValidSnapshot(InboxConversationSnapshot? snapshot)
    {
        try
        {
            ValidateSnapshot(snapshot);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Selects valid rows, keeping only the first valid row for each session key.</summary>
    public static InboxConversationSaveSelection SelectValidSnapshots(IReadOnlyList<InboxConversationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var entries = new List<InboxConversationSnapshot>(snapshots.Count);
        var uniqueKeys = new HashSet<SessionKey>();
        var rejectedCount = 0;
        foreach (var snapshot in snapshots)
        {
            if (!IsValidSnapshot(snapshot) || !uniqueKeys.Add(snapshot!.Key))
            {
                rejectedCount++;
                continue;
            }
            entries.Add(snapshot!);
        }
        return new InboxConversationSaveSelection(entries.ToArray(), rejectedCount);
    }

    /// <summary>Atomically replaces the complete metadata snapshot, retaining the newest 500 rows.</summary>
    public async Task SaveAsync(IReadOnlyList<InboxConversationSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = snapshots.ToArray();
        var uniqueKeys = new HashSet<SessionKey>();
        foreach (var snapshot in entries)
        {
            ValidateSnapshot(snapshot);
            if (!uniqueKeys.Add(snapshot.Key))
                throw new ArgumentException("Inbox conversation snapshots contain duplicate keys.", nameof(snapshots));
        }

        var document = new StoredDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = entries.OrderByDescending(item => item.UpdatedAt).Take(MaxEntries).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaxDocumentBytes)
            throw new ArgumentException("Inbox conversation metadata exceeds the storage limit.", nameof(snapshots));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"chat-conversations.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    /// <summary>Loads metadata and marks previously pending or running rows interrupted.</summary>
    public async Task<IReadOnlyList<InboxConversationSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<InboxConversationSnapshot>();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxDocumentBytes)
                    throw new InvalidDataException("Inbox conversation store exceeds its size limit.");
                buffer.Write(chunk, 0, read);
            }

            var document = JsonSerializer.Deserialize<StoredDocument>(buffer.ToArray(), JsonOptions);
            if (document?.Entries is null || document.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException("Inbox conversation store is empty, invalid, or unsupported.");
            if (document.Entries.Count > MaxEntries)
                throw new InvalidDataException("Inbox conversation store exceeds its entry limit.");
            var uniqueKeys = new HashSet<SessionKey>();
            foreach (var snapshot in document.Entries)
            {
                ValidateSnapshot(snapshot);
                if (!uniqueKeys.Add(snapshot.Key))
                    throw new InvalidDataException("Inbox conversation store contains duplicate keys.");
            }

            return document.Entries.Select(snapshot => snapshot.State is "pending" or "running"
                ? snapshot with { State = "interrupted" }
                : snapshot).OrderByDescending(snapshot => snapshot.UpdatedAt).ToArray();
        }
        catch (JsonException ex) { throw new InvalidDataException("Inbox conversation store contains invalid or unsupported JSON.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Inbox conversation store contains invalid metadata.", ex); }
    }

    private static void ValidateSnapshot(InboxConversationSnapshot? snapshot)
    {
        if (snapshot is null) throw new ArgumentException("Inbox conversation snapshot is empty.");
        ValidateKey(snapshot.Key);
        if (string.IsNullOrWhiteSpace(snapshot.WorkspaceRoot) || snapshot.WorkspaceRoot.Length > 2048 ||
            snapshot.WorkspaceRoot.Any(char.IsControl) || !Path.IsPathFullyQualified(snapshot.WorkspaceRoot))
            throw new ArgumentException("Inbox conversation workspace root is invalid.");
        if (snapshot.UpdatedAt == default || !AllowedStates.Contains(snapshot.State))
            throw new ArgumentException("Inbox conversation timestamp or state is invalid.");
        if (snapshot.RequestCount < 0) throw new ArgumentException("Inbox conversation request count is invalid.");
        ValidateDisplayName(snapshot.DisplayName, "display name");
        ValidateDisplayName(snapshot.UserName, "user name");
        ValidateDisplayName(snapshot.ChannelName, "channel name");
        ValidateDisplayName(snapshot.SiteName, "site name");
    }

    private static void ValidateKey(SessionKey? key)
    {
        if (key is null || key.Provider is not ("slack" or "mattermost" or "discord"))
            throw new ArgumentException("Inbox conversation provider is invalid.");
        ValidateIdentity(key.ChannelId, "channel id");
        ValidateIdentity(key.ThreadId, "thread id");
        ValidateIdentity(key.UserId, "user id");
        if (key.Provider == "slack")
        {
            if (key.Origin != "slack") throw new ArgumentException("Slack inbox origin must be 'slack'.");
        }
        else if (key.Provider == "discord")
        {
            if (key.Origin != "discord") throw new ArgumentException("Discord inbox origin must be 'discord'.");
        }
        else if (key.Origin is null || key.Origin.Length != 64 || key.Origin.Any(c => !IsLowerHex(c)))
            throw new ArgumentException("Mattermost inbox origin must be a lowercase SHA-256 hex digest.");
    }

    private static void ValidateIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException($"Inbox conversation {name} is invalid.");
    }

    private static void ValidateDisplayName(string? value, string name)
    {
        if (value is { Length: > 256 } || value?.Any(char.IsControl) == true)
            throw new ArgumentException($"Inbox conversation {name} is invalid.");
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private sealed class StoredDocument
    {
        public int? SchemaVersion { get; set; }
        public List<InboxConversationSnapshot>? Entries { get; set; }
    }
}
