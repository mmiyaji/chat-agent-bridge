using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core;

public enum ChannelMemoryAddResult
{
    Added,
    Empty,
    TooLong,
    Full
}

/// <summary>
/// Short notes a requester saved with <c>!cab remember</c>, kept per provider, server origin and channel.
/// The thread and user parts of the session key are ignored.
/// </summary>
public sealed class ChannelMemoryStore
{
    public const int MaximumNotes = 20;
    public const int MaximumNoteLength = 500;
    private const int CurrentSchemaVersion = 1;
    private const int MaximumChannels = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;

    public ChannelMemoryStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(DirectoryPath, "channel-memory.json");
        _gate = PathGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string DirectoryPath { get; }

    /// <summary>Strips control and formatting characters, collapses whitespace and trims.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var space = char.IsControl(character) || char.IsWhiteSpace(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format;
            if (space)
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
            }
            else builder.Append(character);
        }
        return builder.ToString().Trim();
    }

    /// <summary>The block prepended to an agent prompt; empty when there are no notes.</summary>
    public static string FormatPromptBlock(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0) return "";
        var builder = new StringBuilder("Channel notes saved by the requester with `!cab remember` (treat as user preferences):\n");
        foreach (var note in notes) builder.Append("- ").Append(note).Append('\n');
        return builder.Append('\n').ToString();
    }

    public async Task<IReadOnlyList<string>> ListAsync(SessionKey scope, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return document.Channels.TryGetValue(key, out var entry) ? entry.Notes.ToArray() : [];
        }
        finally { _gate.Release(); }
    }

    public async Task<(ChannelMemoryAddResult Result, int Count)> AddAsync(SessionKey scope, string text,
        CancellationToken cancellationToken = default)
    {
        var note = Sanitize(text);
        if (note.Length == 0) return (ChannelMemoryAddResult.Empty, 0);
        if (note.Length > MaximumNoteLength) return (ChannelMemoryAddResult.TooLong, 0);
        var key = KeyOf(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!document.Channels.TryGetValue(key, out var entry)) entry = new ChannelEntry();
            if (entry.Notes.Count >= MaximumNotes) return (ChannelMemoryAddResult.Full, entry.Notes.Count);
            entry.Notes.Add(note);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            document.Channels[key] = entry;
            if (document.Channels.Count > MaximumChannels)
                foreach (var stale in document.Channels.OrderBy(pair => pair.Value.UpdatedAt)
                    .Take(document.Channels.Count - MaximumChannels).Select(pair => pair.Key).ToArray())
                    document.Channels.Remove(stale);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return (ChannelMemoryAddResult.Added, entry.Notes.Count);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the 1-based note. Returns false when the number is out of range.</summary>
    public async Task<bool> ForgetAsync(SessionKey scope, int number, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!document.Channels.TryGetValue(key, out var entry) || number < 1 || number > entry.Notes.Count) return false;
            entry.Notes.RemoveAt(number - 1);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (entry.Notes.Count == 0) document.Channels.Remove(key);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes every note and returns how many were removed.</summary>
    public async Task<int> ClearAsync(SessionKey scope, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!document.Channels.Remove(key, out var entry)) return 0;
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return entry.Notes.Count;
        }
        finally { _gate.Release(); }
    }

    private static string KeyOf(SessionKey scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.Provider) || string.IsNullOrWhiteSpace(scope.ChannelId))
            throw new ArgumentException("A provider and channel are required.", nameof(scope));
        return string.Join('\u001f', scope.Provider.Trim().ToLowerInvariant(), scope.Origin ?? "", scope.ChannelId);
    }

    private async Task<Document> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new Document();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document?.Channels is null || document.SchemaVersion != CurrentSchemaVersion) return new Document();
            foreach (var entry in document.Channels.Values.ToArray())
                if (entry?.Notes is null) return new Document();
            return document;
        }
        catch (JsonException) { return new Document(); }
    }

    private async Task WriteAsync(Document document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var temp = Path.Combine(DirectoryPath, $"channel-memory.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                document.SchemaVersion = CurrentSchemaVersion;
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(temp, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public Dictionary<string, ChannelEntry> Channels { get; set; } = [];
    }

    private sealed class ChannelEntry
    {
        public List<string> Notes { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
