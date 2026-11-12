using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChatAgentBridge.Core;

public enum ChatScheduleAddResult { Added, TooManyForUser, TooManyTotal }

/// <summary>Persists recurring prompts in <c>chat-schedules.json</c>. A damaged or unknown-version file reads as empty.</summary>
public sealed class ChatScheduleStore
{
    public const int MaximumPerUser = 5;
    public const int MaximumTotal = 50;
    private const int CurrentSchemaVersion = 1;
    private const int ScheduleIdLength = 6;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;

    public ChatScheduleStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(DirectoryPath, "chat-schedules.json");
        _gate = PathGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string DirectoryPath { get; }

    public async Task<IReadOnlyList<ChatSchedule>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAsync(cancellationToken).ConfigureAwait(false)).Schedules.ToArray(); }
        finally { _gate.Release(); }
    }

    public static bool SameOwner(ChatSchedule schedule, string provider, string? origin, string channelId, string userId) =>
        schedule.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(schedule.Origin ?? "", origin ?? "", StringComparison.Ordinal) &&
        schedule.ChannelId == channelId && schedule.UserId == userId;

    public async Task<(ChatScheduleAddResult Result, ChatSchedule? Schedule)> AddAsync(ChatSchedule template,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (document.Schedules.Count >= MaximumTotal) return (ChatScheduleAddResult.TooManyTotal, null);
            if (document.Schedules.Count(item => item.Provider.Equals(template.Provider, StringComparison.OrdinalIgnoreCase) &&
                    item.UserId == template.UserId) >= MaximumPerUser)
                return (ChatScheduleAddResult.TooManyForUser, null);
            string id;
            do { id = RandomNumberGenerator.GetHexString(ScheduleIdLength).ToLowerInvariant(); }
            while (document.Schedules.Any(item => item.Id == id));
            var schedule = template with { Id = id, CreatedAt = template.CreatedAt == default ? DateTimeOffset.UtcNow : template.CreatedAt };
            document.Schedules.Add(schedule);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return (ChatScheduleAddResult.Added, schedule);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes by id, or by 1-based position among <paramref name="visible"/> (the caller's own list).</summary>
    public async Task<ChatSchedule?> RemoveAsync(string target, IReadOnlyList<ChatSchedule> visible,
        CancellationToken cancellationToken = default)
    {
        var id = target;
        // An all-digit generated ID is still an ID, even when its numeric value is a valid list position.
        if (target.Length != ScheduleIdLength && int.TryParse(target, out var number))
            id = number >= 1 && number <= visible.Count ? visible[number - 1].Id : "";
        if (id.Length == 0 || visible.All(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var removed = document.Schedules.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed is null) return null;
            document.Schedules.Remove(removed);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records a run, so a schedule fires once per due time even after a long pause.</summary>
    public async Task MarkRunAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = document.Schedules.FindIndex(item => item.Id == id);
            if (index < 0) return;
            document.Schedules[index] = document.Schedules[index] with { LastRunAt = at };
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<Document> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new Document();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return document?.Schedules is null || document.SchemaVersion != CurrentSchemaVersion ? new Document() : document;
        }
        catch (JsonException) { return new Document(); }
    }

    private async Task WriteAsync(Document document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var temp = Path.Combine(DirectoryPath, $"chat-schedules.{Guid.NewGuid():N}.tmp");
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
        public List<ChatSchedule> Schedules { get; set; } = [];
    }
}
