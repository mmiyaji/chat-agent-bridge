using System.Text.Json;

namespace ChatAgentBridge.Core;

public sealed record OnboardingConsent(string PolicyVersion, DateTimeOffset AcceptedAtUtc);

public sealed class OnboardingConsentStore
{
    public const string CurrentPolicyVersion = "2026-10-20.1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public OnboardingConsentStore(string? directory = null)
    {
        DirectoryPath = new SettingsStore(directory).DirectoryPath;
        _filePath = Path.Combine(DirectoryPath, "consent.json");
    }

    public string DirectoryPath { get; }

    public static bool IsCurrent(OnboardingConsent? consent) =>
        consent is not null && string.Equals(consent.PolicyVersion, CurrentPolicyVersion, StringComparison.Ordinal);

    public async Task<OnboardingConsent?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var consent = await JsonSerializer.DeserializeAsync<OnboardingConsent>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (consent is null || string.IsNullOrWhiteSpace(consent.PolicyVersion) || consent.AcceptedAtUtc == default || consent.AcceptedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Onboarding consent must include a policy version and a UTC acceptance timestamp.");
            return consent;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Onboarding consent file contains invalid JSON or fields.", ex);
        }
    }

    public async Task SaveAsync(OnboardingConsent consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (string.IsNullOrWhiteSpace(consent.PolicyVersion) || consent.AcceptedAtUtc == default || consent.AcceptedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Onboarding consent must include a policy version and a UTC acceptance timestamp.", nameof(consent));
        Directory.CreateDirectory(DirectoryPath);
        var tempPath = Path.Combine(DirectoryPath, $"consent.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, consent, JsonOptions, cancellationToken).ConfigureAwait(false);
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
