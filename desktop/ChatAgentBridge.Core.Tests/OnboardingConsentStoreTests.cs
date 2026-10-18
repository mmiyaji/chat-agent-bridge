using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class OnboardingConsentStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripPersistsConsentSeparately()
    {
        using var temp = new TempDirectory();
        var store = new OnboardingConsentStore(temp.Path);
        var expected = new OnboardingConsent(OnboardingConsentStore.CurrentPolicyVersion,
            new DateTimeOffset(2026, 10, 18, 12, 34, 56, TimeSpan.Zero));

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
        Assert.True(File.Exists(Path.Combine(temp.Path, "consent.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "onboarding.json")));
        Assert.True(OnboardingConsentStore.IsCurrent(await store.LoadAsync()));
    }

    [Fact]
    public async Task UnknownPolicyVersionLoadsButIsNotCurrent()
    {
        using var temp = new TempDirectory();
        var store = new OnboardingConsentStore(temp.Path);
        var consent = new OnboardingConsent("older-policy", DateTimeOffset.UtcNow);

        await store.SaveAsync(consent);

        Assert.Equal(consent, await store.LoadAsync());
        Assert.False(OnboardingConsentStore.IsCurrent(consent));
        Assert.False(OnboardingConsentStore.IsCurrent(null));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"PolicyVersion\":\"2026-10-18\",\"AcceptedAtUtc\":\"2026-10-18T12:00:00+09:00\"}")]
    [InlineData("{\"PolicyVersion\":\"2026-10-18\"}")]
    [InlineData("{\"AcceptedAtUtc\":\"2026-10-18T12:00:00Z\"}")]
    public async Task LoadRejectsCorruptConsentWithoutOverwriting(string contents)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "consent.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new OnboardingConsentStore(temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveRejectsMissingVersionOrNonUtcTimestamp()
    {
        using var temp = new TempDirectory();
        var store = new OnboardingConsentStore(temp.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new OnboardingConsent(" ", DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new OnboardingConsent("v1", new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(9)))));
        Assert.Null(await store.LoadAsync());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-consent-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
