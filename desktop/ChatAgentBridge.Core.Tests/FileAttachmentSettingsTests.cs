using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentSettingsTests
{
    [Fact]
    public async Task OlderSettingsWithoutAttachmentFieldsReceiveSafeDefaults()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"), "{\"SchemaVersion\":1}");

        var settings = await new SettingsStore(temp.Path).LoadAsync();

        Assert.Equal("always", settings.FileAttachmentApprovalMode);
        Assert.Equal(10, settings.FileAttachmentMaxSizeMb);
        Assert.Equal(3, settings.FileAttachmentMaxCount);
        Assert.Equal(20, settings.FileAttachmentMaxTotalSizeMb);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("always")]
    [InlineData("first")]
    [InlineData("auto")]
    public async Task SettingsStoreRoundTripsEachAttachmentApprovalMode(string mode)
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);
        var expected = new BridgeSettings
        {
            FileAttachmentApprovalMode = mode,
            FileAttachmentMaxSizeMb = 64,
            FileAttachmentMaxCount = 8,
            FileAttachmentMaxTotalSizeMb = 96
        };

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
    }

    [Theory]
    [InlineData(0, 3, 20)]
    [InlineData(101, 3, 20)]
    [InlineData(10, 0, 20)]
    [InlineData(10, 11, 20)]
    [InlineData(10, 3, 0)]
    [InlineData(10, 3, 101)]
    public async Task SettingsStoreRejectsAttachmentLimitsOutsideSupportedRange(int perFile, int count, int total)
    {
        using var temp = new TempDirectory();
        var settings = new BridgeSettings
        {
            FileAttachmentMaxSizeMb = perFile,
            FileAttachmentMaxCount = count,
            FileAttachmentMaxTotalSizeMb = total
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SettingsStore(temp.Path).SaveAsync(settings));
    }

    [Fact]
    public async Task SettingsStoreRejectsUnknownAttachmentApprovalMode()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new BridgeSettings { FileAttachmentApprovalMode = "sometimes" }));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"), "{\"SchemaVersion\":1,\"FileAttachmentApprovalMode\":\"sometimes\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
