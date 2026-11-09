using System.Text.Json;

namespace ChatAgentBridge.Core;

public sealed record UpdateNoticeState(int SchemaVersion, string LastObservedVersion,
    string? PendingVersion, string? DismissedVersion);

/// <summary>Persists whether an update notice has been dismissed for the current app version.</summary>
public sealed class UpdateNoticeStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly string _filePath;

    public UpdateNoticeStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _filePath = Path.Combine(_directory, "update-notice.json");
    }

    public async Task<bool> ShouldShowAsync(string version, bool hasExistingSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        bool shouldShow;
        if (state is null)
        {
            // Existing settings imply an upgrade from a build that did not track this notice.
            shouldShow = hasExistingSettings;
            state = new UpdateNoticeState(CurrentSchemaVersion, version, shouldShow ? version : null, null);
        }
        else if (!string.Equals(state.LastObservedVersion, version, StringComparison.Ordinal))
        {
            shouldShow = true;
            state = state with { LastObservedVersion = version, PendingVersion = version };
        }
        else
        {
            shouldShow = string.Equals(state.PendingVersion, version, StringComparison.Ordinal) &&
                !string.Equals(state.DismissedVersion, version, StringComparison.Ordinal);
        }

        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return shouldShow;
    }

    public async Task DismissAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false) ??
            new UpdateNoticeState(CurrentSchemaVersion, version, version, null);
        await SaveAsync(state with { LastObservedVersion = version, PendingVersion = null, DismissedVersion = version }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UpdateNoticeState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<UpdateNoticeState>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return state is { SchemaVersion: CurrentSchemaVersion } && !string.IsNullOrWhiteSpace(state.LastObservedVersion)
                ? state : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task SaveAsync(UpdateNoticeState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = Path.Combine(_directory, $"update-notice.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
