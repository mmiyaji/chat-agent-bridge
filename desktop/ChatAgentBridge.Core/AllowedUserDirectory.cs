using System.Text.Json;
using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core;

/// <summary>
/// Display details for an allowed chat user. The allowed-user lists in settings remain the only source of
/// access; this record only explains who an ID is and how it was added. Origin, ChannelId and ThreadId are
/// where the user was last seen, used to look up a name or picture.
/// </summary>
public sealed record AllowedUserRecord(
    string Provider,
    string UserId,
    string? DisplayName,
    DateTimeOffset AddedAt,
    string Method,
    string? Actor,
    string? Origin = null,
    string? ChannelId = null,
    string? ThreadId = null,
    DateTimeOffset? LastRequestAt = null);

/// <summary>Stores <see cref="AllowedUserRecord"/> entries in allowed-users.json in the data folder.</summary>
public sealed class AllowedUserDirectory
{
    public const string ChatRequestMethod = "chat_request";
    public const string SettingsMethod = "settings";
    private const int MaxRecords = 300;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AllowedUserDirectory(string? directory = null)
    {
        _path = Path.Combine(Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath), "allowed-users.json");
    }

    public async Task<IReadOnlyList<AllowedUserRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>Display-only names remembered in user records, audit events, and inbox metadata.</summary>
    public async Task<IReadOnlyDictionary<(string Provider, string UserId), string>> LoadDisplayNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<(string Provider, string UserId), string>();
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records.OrderByDescending(record => record.LastRequestAt ?? record.AddedAt))
            Remember(record.Provider, record.UserId, record.DisplayName);

        var directory = Path.GetDirectoryName(_path)!;
        try
        {
            var events = await new AuditStore(directory).ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in events.OrderByDescending(item => item.Timestamp))
                if (item.Access is { } user) Remember(item.Source, user.UserId, user.DisplayName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Optional audit metadata must not hide names already saved with allowed users.
        }
        try
        {
            var conversations = await new InboxConversationStore(directory).ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var conversation in conversations.OrderByDescending(item => item.UpdatedAt))
                if (conversation.Key.UserId is { } userId)
                    Remember(conversation.Key.Provider, userId, conversation.UserName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Inbox metadata is another optional source of display names.
        }
        return names;

        void Remember(string provider, string userId, string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && name != userId)
                names.TryAdd((provider, userId), name);
        }
    }

    /// <summary>Adds or refreshes a record. Existing details are kept when the new record does not know them.</summary>
    public Task RecordAddedAsync(AllowedUserRecord record, CancellationToken cancellationToken = default) =>
        UpdateAsync(records =>
        {
            Validate(record);
            var index = records.FindIndex(item => Same(item, record.Provider, record.UserId));
            if (index < 0)
            {
                records.Add(record);
                return true;
            }
            var existing = records[index];
            records[index] = record with
            {
                DisplayName = record.DisplayName ?? existing.DisplayName,
                Origin = record.Origin ?? existing.Origin,
                ChannelId = record.ChannelId ?? existing.ChannelId,
                ThreadId = record.ThreadId ?? existing.ThreadId,
                LastRequestAt = record.LastRequestAt ?? existing.LastRequestAt
            };
            return true;
        }, cancellationToken);

    public Task RecordRemovedAsync(string provider, string userId, CancellationToken cancellationToken = default) =>
        UpdateAsync(records => records.RemoveAll(item => Same(item, provider, userId)) > 0, cancellationToken);

    /// <summary>Remembers when a user last sent a request. Unknown users are ignored.</summary>
    public Task RecordRequestAsync(string provider, string userId, DateTimeOffset at, string? displayName = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(records =>
        {
            var index = records.FindIndex(item => Same(item, provider, userId));
            if (index < 0) return false;
            records[index] = records[index] with
            {
                LastRequestAt = at,
                DisplayName = displayName ?? records[index].DisplayName
            };
            return true;
        }, cancellationToken);

    private async Task UpdateAsync(Func<List<AllowedUserRecord>, bool> change, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (!change(records)) return;
            if (records.Count > MaxRecords) records = records.OrderByDescending(item => item.AddedAt).Take(MaxRecords).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken).ConfigureAwait(false);
                await AtomicFile.ReplaceAsync(temporary, _path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { Gate.Release(); }
    }

    private async Task<IReadOnlyList<AllowedUserRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var records = await JsonSerializer.DeserializeAsync<List<AllowedUserRecord>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return records?.Where(IsValid).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Display details are optional. A damaged file shows IDs without names until the next update rewrites it.
            return [];
        }
    }

    private static bool Same(AllowedUserRecord record, string provider, string userId) =>
        string.Equals(record.Provider, provider, StringComparison.Ordinal) && string.Equals(record.UserId, userId, StringComparison.Ordinal);

    private static bool IsValid(AllowedUserRecord? record)
    {
        if (record is null) return false;
        try { Validate(record); return true; }
        catch (ArgumentException) { return false; }
    }

    private static void Validate(AllowedUserRecord record)
    {
        if (record.Provider is not ("slack" or "mattermost" or "discord"))
            throw new ArgumentException("Unsupported chat provider.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.UserId) || record.UserId.Length > 128)
            throw new ArgumentException("A user ID is required.", nameof(record));
        if (record.Method is not (ChatRequestMethod or SettingsMethod))
            throw new ArgumentException("Unsupported method.", nameof(record));
        if (record.DisplayName is { Length: > 256 } || record.Actor is { Length: > 256 } || record.Origin is { Length: > 512 } ||
            record.ChannelId is { Length: > 256 } || record.ThreadId is { Length: > 256 })
            throw new ArgumentException("A field is too long.", nameof(record));
    }
}
