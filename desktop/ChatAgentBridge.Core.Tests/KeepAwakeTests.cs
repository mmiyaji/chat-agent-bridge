using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class KeepAwakeTests
{
    [Fact]
    public async Task PreferencesDefaultToActiveWorkAndPersistIndependently()
    {
        using var directory = new TemporaryDirectory();
        var store = new KeepAwakePreferencesStore(directory.Path);
        Assert.Equal(KeepAwakeMode.Requests, (await store.LoadAsync()).Mode);
        Assert.True((await store.LoadAsync()).AcOnly);
        var value = new KeepAwakePreferences { Mode = KeepAwakeMode.Receiver };
        await store.SaveAsync(value);
        Assert.Equal(value, await new KeepAwakePreferencesStore(directory.Path).LoadAsync());
    }

    [Fact]
    public async Task MalformedPreferenceFileFailsClosedAndCanBeReplaced()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "keep-awake.json");
        await File.WriteAllTextAsync(path, "{");
        var store = new KeepAwakePreferencesStore(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(KeepAwakeMode.Requests, new KeepAwakePreferences().Mode);
        await store.SaveAsync(new());
        Assert.Equal(KeepAwakeMode.Requests, (await store.LoadAsync()).Mode);
    }

    [Theory]
    [InlineData(KeepAwakeMode.Off, false, false, false, true, false)]
    [InlineData(KeepAwakeMode.Requests, false, false, false, true, false)]
    [InlineData(KeepAwakeMode.Requests, false, true, false, true, true)]
    [InlineData(KeepAwakeMode.Requests, false, false, true, true, true)]
    [InlineData(KeepAwakeMode.Receiver, true, false, false, true, true)]
    [InlineData(KeepAwakeMode.Receiver, false, true, false, true, true)]
    [InlineData(KeepAwakeMode.Receiver, false, false, true, true, true)]
    [InlineData(KeepAwakeMode.Receiver, true, true, true, false, false)]
    public void PolicyRequiresEnabledModeAndHonorsAcOnly(string mode, bool receiver, bool request, bool manual, bool ac, bool expected)
    {
        var preferences = new KeepAwakePreferences { Mode = mode, AcOnly = true };
        Assert.Equal(expected, KeepAwakePolicy.ShouldStayAwake(preferences, receiver, request, manual, ac));
    }

    [Fact]
    public void RequestControllerDoesNotRefcountAndReappliesAfterResume()
    {
        var fake = new FakeRequest();
        var controller = new KeepAwakeRequestController(fake);
        controller.Update(true);
        controller.Update(true);
        Assert.Equal(new[] { true }, fake.Transitions);
        controller.Resume();
        controller.Update(true);
        Assert.Equal(1, fake.Resets);
        Assert.Equal(new[] { true, true }, fake.Transitions);
        controller.Update(false);
        controller.Update(false);
        Assert.Equal(new[] { true, true, false }, fake.Transitions);
        controller.Dispose();
        Assert.Equal(1, fake.Disposals);
    }

    [Fact]
    public void RequestControllerClearsActiveLeaseOnShutdown()
    {
        var fake = new FakeRequest();
        var controller = new KeepAwakeRequestController(fake);
        controller.Update(true);
        controller.Dispose();
        Assert.Equal(new[] { true, false }, fake.Transitions);
    }

    private sealed class FakeRequest : IKeepAwakeRequest
    {
        public List<bool> Transitions { get; } = [];
        public int Resets { get; private set; }
        public int Disposals { get; private set; }
        public void SetActive(bool active) => Transitions.Add(active);
        public void ResetAfterResume() => Resets++;
        public void Dispose() => Disposals++;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cab-keep-awake-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("cab-keep-awake-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
