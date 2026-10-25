using System.Text.Json;

namespace ChatAgentBridge.Core;

/// <summary>Remembers which one-time feature tips the user has already dismissed.</summary>
public sealed class FeatureTipsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public FeatureTipsStore(SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        DirectoryPath = settingsStore.DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "tips.json");
    }

    public string DirectoryPath { get; }

    public async Task<bool> HasSeenAsync(string tip, CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false)).Contains(tip);

    public async Task MarkSeenAsync(string tip, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tip);
        var seen = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!seen.Add(tip)) return;
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"tips.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredTips(seen.Order(StringComparer.Ordinal).ToArray()), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new(StringComparer.Ordinal);
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredTips>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return new((stored?.Seen ?? []).Where(tip => !string.IsNullOrWhiteSpace(tip)), StringComparer.Ordinal);
        }
        // A damaged file only means a tip may show once more.
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private sealed record StoredTips(string[]? Seen);
}
