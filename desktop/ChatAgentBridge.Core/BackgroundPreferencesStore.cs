using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

public enum WindowCloseBehavior
{
    AskEveryTime,
    MoveToSystemTray,
    ExitApp
}

public sealed record BackgroundPreferences
{
    public BackgroundPreferences(WindowCloseBehavior closeBehavior = WindowCloseBehavior.AskEveryTime) =>
        CloseBehavior = Enum.IsDefined(closeBehavior) ? closeBehavior : WindowCloseBehavior.AskEveryTime;

    // Retain source compatibility with callers that used the original bool record.
    public BackgroundPreferences(bool CloseToTray) : this(CloseToTray
        ? WindowCloseBehavior.MoveToSystemTray
        : WindowCloseBehavior.AskEveryTime) { }

    [JsonConverter(typeof(JsonStringEnumConverter<WindowCloseBehavior>))]
    public WindowCloseBehavior CloseBehavior { get; init; }

    // Legacy view only. New files persist a single canonical behavior field.
    [JsonIgnore]
    public bool CloseToTray => CloseBehavior == WindowCloseBehavior.MoveToSystemTray;
}

/// <summary>Local window behavior, independent of agent credentials and chat settings.</summary>
public sealed class BackgroundPreferencesStore(SettingsStore settingsStore)
{
    private readonly string _path = Path.Combine(settingsStore.DirectoryPath, "background.json");

    public async Task<BackgroundPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return new();
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new();

            // Prefer the canonical field whenever present. Invalid canonical data
            // falls back safely to asking instead of reviving a stale legacy value.
            if (TryGetProperty(document.RootElement, "CloseBehavior", out var behavior))
            {
                if (behavior.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<WindowCloseBehavior>(behavior.GetString(), ignoreCase: true, out var parsed) &&
                    Enum.IsDefined(parsed) &&
                    string.Equals(behavior.GetString(), Enum.GetName(parsed), StringComparison.OrdinalIgnoreCase))
                    return new(parsed);
                return new();
            }

            // Existing versions wrote only { "CloseToTray": bool }.
            if (TryGetProperty(document.RootElement, "CloseToTray", out var closeToTray) &&
                closeToTray.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return new(closeToTray.GetBoolean());
            return new();
        }
        catch (JsonException) { return new(); }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public async Task SaveAsync(BackgroundPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(settingsStore.DirectoryPath);
        var temp = Path.Combine(settingsStore.DirectoryPath, $"background.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(temp, _path, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
