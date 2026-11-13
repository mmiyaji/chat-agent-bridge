using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

/// <summary>Remembers when each conversation was first seen, for the "conversation" attachment scope.</summary>
public sealed class AttachmentConversationStore
{
    private const int MaxEntries = 500;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _filePath;

    public AttachmentConversationStore(string? directory = null) =>
        _filePath = Path.Combine(new SettingsStore(directory).DirectoryPath, "attachment-conversations.json");

    public async Task<DateTime> GetOrAddStartAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var existing = entries.FirstOrDefault(entry => Matches(entry, key));
            if (existing is not null) return existing.StartUtc;
            var start = DateTime.UtcNow;
            entries.Add(new Entry(key.Provider, key.ChannelId, key.ThreadId, key.UserId, key.Origin, start));
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entries), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _filePath, overwrite: true);
            return start;
        }
        finally { Gate.Release(); }
    }

    private async Task<List<Entry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<Entry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private static bool Matches(Entry entry, SessionKey key) =>
        entry.Provider == key.Provider && entry.ChannelId == key.ChannelId && entry.ThreadId == key.ThreadId &&
        entry.UserId == key.UserId && entry.Origin == key.Origin;

    private sealed record Entry(string Provider, string ChannelId, string ThreadId, string? UserId, string? Origin, DateTime StartUtc);
}
