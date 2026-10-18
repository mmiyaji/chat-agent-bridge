using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class OnboardingProgressStoreTests
{
    [Fact]
    public void ShouldStartDistinguishesNewAndExistingInstallations()
    {
        Assert.True(OnboardingProgressStore.ShouldStart(false, null));
        Assert.False(OnboardingProgressStore.ShouldStart(true, null));
    }

    [Fact]
    public void ShouldStartResumesOnlyActiveProgress()
    {
        Assert.True(OnboardingProgressStore.ShouldStart(true, new OnboardingProgress(1, "active")));
        Assert.False(OnboardingProgressStore.ShouldStart(false, new OnboardingProgress(1, "deferred")));
        Assert.False(OnboardingProgressStore.ShouldStart(false, new OnboardingProgress(2, "completed")));
    }

    [Fact]
    public async Task SaveAndLoadRoundTripPersistsProgressSeparately()
    {
        using var temp = new TempDirectory();
        var store = new OnboardingProgressStore(temp.Path);
        var expected = new OnboardingProgress(3, "active");

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
        Assert.True(File.Exists(Path.Combine(temp.Path, "onboarding.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "credentials.json")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":3,\"step\":0,\"status\":\"active\"}")]
    [InlineData("{\"schemaVersion\":1,\"step\":3,\"status\":\"active\"}")]
    [InlineData("{\"schemaVersion\":1,\"step\":0,\"status\":\"unknown\"}")]
    public async Task LoadRejectsCorruptOrUnsupportedProgressWithoutOverwriting(string contents)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "onboarding.json");
        await File.WriteAllTextAsync(path, contents);
        var store = new OnboardingProgressStore(temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(-1, "active")]
    [InlineData(4, "active")]
    [InlineData(0, "unknown")]
    public async Task SaveRejectsInvalidProgress(int step, string status)
    {
        using var temp = new TempDirectory();
        var store = new OnboardingProgressStore(temp.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new OnboardingProgress(step, status)));
        Assert.Null(await store.LoadAsync());
    }

    [Theory]
    [InlineData(0, 0, "active")]
    [InlineData(1, 2, "deferred")]
    [InlineData(2, 3, "completed")]
    public async Task LoadsSchema1ProgressWithNewStepMappingAndSavesAsSchema2(int oldStep, int newStep, string status)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "onboarding.json");
        await File.WriteAllTextAsync(path, $"{{\"schemaVersion\":1,\"step\":{oldStep},\"status\":\"{status}\"}}");
        var store = new OnboardingProgressStore(temp.Path);
        var expected = new OnboardingProgress(newStep, status);

        Assert.Equal(expected, await store.LoadAsync());

        await store.SaveAsync(expected);

        using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(expected, await store.LoadAsync());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-onboarding-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
