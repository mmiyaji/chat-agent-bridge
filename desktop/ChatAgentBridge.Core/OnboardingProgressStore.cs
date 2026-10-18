using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatAgentBridge.Core;

public sealed record OnboardingProgress(int Step, string Status);

public sealed class OnboardingProgressStore
{
    private const int CurrentSchemaVersion = 3;
    private const int LegacySchemaVersion = 1;
    private const int PreviousSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public OnboardingProgressStore(string? directory = null)
    {
        DirectoryPath = new SettingsStore(directory).DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "onboarding.json");
    }

    public string DirectoryPath { get; }

    public static bool ShouldStart(bool hasSavedSettings, OnboardingProgress? progress) =>
        progress?.Status == "active" || (!hasSavedSettings && progress is null);

    public async Task<OnboardingProgress?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredProgress>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null) throw new InvalidDataException("Onboarding progress file is empty or invalid.");
            if (stored.SchemaVersion is LegacySchemaVersion or PreviousSchemaVersion)
            {
                var migratedStep = stored.SchemaVersion switch
                {
                    LegacySchemaVersion => stored.Step switch
                    {
                        0 => 1, 1 => 3, 2 => 4,
                        _ => throw new InvalidDataException("Onboarding progress schema 1 step must be from 0 through 2.")
                    },
                    PreviousSchemaVersion when stored.Step is >= 0 and <= 3 => stored.Step + 1,
                    PreviousSchemaVersion => throw new InvalidDataException("Onboarding progress schema 2 step must be from 0 through 3."),
                    _ => throw new InvalidDataException()
                };
                Validate(migratedStep, stored.Status, invalidData: true);
                return new OnboardingProgress(migratedStep, stored.Status!);
            }
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Onboarding progress schema version {stored.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            Validate(stored.Step, stored.Status, invalidData: true);
            return new OnboardingProgress(stored.Step, stored.Status!);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Onboarding progress file contains invalid JSON.", ex);
        }
    }

    public async Task SaveAsync(OnboardingProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Validate(progress.Step, progress.Status, invalidData: false);
        Directory.CreateDirectory(DirectoryPath);

        var stored = new StoredProgress
        {
            SchemaVersion = CurrentSchemaVersion,
            Step = progress.Step,
            Status = progress.Status
        };
        var tempPath = Path.Combine(DirectoryPath, $"onboarding.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(tempPath, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void Validate(int step, string? status, bool invalidData)
    {
        var valid = step is >= 0 and <= 4 && status is "active" or "deferred" or "completed";
        if (valid) return;
        var message = "Onboarding progress must have a step from 0 through 4 and a status of active, deferred, or completed.";
        if (invalidData) throw new InvalidDataException(message);
        throw new ArgumentException(message, "progress");
    }

    private sealed class StoredProgress
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("step")]
        public int Step { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}
