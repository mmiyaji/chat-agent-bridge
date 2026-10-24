using System.Text.Json;

namespace ChatAgentBridge.Core.Appearance;

/// <summary>User-adjusted pane widths in device-independent pixels; null keeps the built-in layout.</summary>
public sealed record LayoutPreferences(double? SidebarWidth = null, double? SettingsNavigationWidth = null,
    double? InboxListWidth = null)
{
    // Narrow sidebars keep the icons and trim item labels with an ellipsis.
    public const double MinSidebarWidth = 120, MaxSidebarWidth = 400;
    public const double MinSettingsNavigationWidth = 160, MaxSettingsNavigationWidth = 320;
    // The upper bound only rejects corrupt values; the page also keeps room for the detail pane.
    public const double MinInboxListWidth = 280, MaxInboxListWidth = 1600;

    /// <summary>Clamps each width into its range and drops values that are not finite numbers.</summary>
    public LayoutPreferences Normalized() => new(
        Clamp(SidebarWidth, MinSidebarWidth, MaxSidebarWidth),
        Clamp(SettingsNavigationWidth, MinSettingsNavigationWidth, MaxSettingsNavigationWidth),
        Clamp(InboxListWidth, MinInboxListWidth, MaxInboxListWidth));

    private static double? Clamp(double? value, double min, double max) =>
        value is { } width && double.IsFinite(width) ? Math.Round(Math.Clamp(width, min, max)) : null;
}

public sealed class LayoutPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public LayoutPreferencesStore(SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        DirectoryPath = settingsStore.DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "layout.json");
    }

    public string DirectoryPath { get; }

    /// <summary>Returns the saved widths, or defaults when the file is missing or unreadable.</summary>
    public async Task<LayoutPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new LayoutPreferences();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 64 * 1024) return new LayoutPreferences();
            var stored = await JsonSerializer.DeserializeAsync<LayoutPreferences>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return stored?.Normalized() ?? new LayoutPreferences();
        }
        catch (JsonException) { return new LayoutPreferences(); }
        catch (InvalidDataException) { return new LayoutPreferences(); }
    }

    public async Task SaveAsync(LayoutPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalized();
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"layout.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }
}
