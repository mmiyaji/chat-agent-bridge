using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatAgentBridge.Core.Updates;

public sealed class PortableUpdateStageStoreTests
{
    [Fact]
    public async Task ReadyStageSurvivesRestartAndRevalidatesArchiveAndExtractedFiles()
    {
        using var fixture = await StageFixture.CreateAsync();

        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);
        var restored = await PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release);

        Assert.NotNull(restored);
        Assert.Equal(fixture.Work, restored.WorkDirectory);
        Assert.Equal(fixture.Release.Version, restored.Release.Version);
        Assert.Equal(fixture.Staged.Files, restored.Staged.Files);
    }

    [Fact]
    public async Task RejectsTamperedStatePathsAndExtractedFiles()
    {
        using var fixture = await StageFixture.CreateAsync();
        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);
        var stateFile = Path.Combine(fixture.Work, PortableUpdateStageStore.StateFileName);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(stateFile))!;
        state["WorkDirectory"] = Path.Combine(fixture.Root, "outside", Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(stateFile, state.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release));

        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);
        state = JsonNode.Parse(await File.ReadAllTextAsync(stateFile))!;
        state["Files"]!["../escape.bin"] = new string('a', 64);
        await File.WriteAllTextAsync(stateFile, state.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release));

        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);
        await File.WriteAllTextAsync(Path.Combine(fixture.Staged.DirectoryPath, "ChatAgentBridge.Desktop.dll"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release));
    }

    [Fact]
    public async Task RejectsWrongArchitectureStaleCandidateAndTamperedArchive()
    {
        using var fixture = await StageFixture.CreateAsync();
        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);

        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "arm64", true, fixture.Release));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            fixture.Release.Version, "x64", true, fixture.Release));
        await File.AppendAllTextAsync(Path.Combine(fixture.Work, PortableUpdateStageStore.ArchiveFileName), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release));
    }

    [Fact]
    public async Task RejectsReparsePointInPersistedStage()
    {
        using var fixture = await StageFixture.CreateAsync();
        await PortableUpdateStageStore.SaveAsync(fixture.Work, fixture.Install, fixture.Data, "x64", fixture.Release, fixture.Staged);
        var stageDirectory = Path.Combine(fixture.Work, "stage");
        Directory.Delete(stageDirectory, recursive: true);
        try { Directory.CreateSymbolicLink(stageDirectory, fixture.Root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Directory.CreateDirectory(stageDirectory);
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateStageStore.LoadAsync(fixture.Work, fixture.Install, fixture.Data,
            "0.1.0-preview.5", "x64", true, fixture.Release));
    }

    [Fact]
    public async Task CleansOnlyOwnedCompletedWorkDirectory()
    {
        using var fixture = await StageFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Work, "update-result.json"), "{\"success\":true}");

        Assert.True(PortableUpdateStageStore.IsCompleted(fixture.Work));
        PortableUpdateStageStore.DeleteOwnedWorkDirectory(fixture.Install, fixture.Data, fixture.Work);
        Assert.False(Directory.Exists(fixture.Work));
        Assert.Throws<InvalidDataException>(() => PortableUpdateStageStore.DeleteOwnedWorkDirectory(fixture.Install, fixture.Data, fixture.Root));
    }

    private sealed class StageFixture : IDisposable
    {
        public string Root { get; }
        public string Install { get; }
        public string Data { get; }
        public string Work { get; }
        public PortableRelease Release { get; }
        public StagedPortableRelease Staged { get; }

        private StageFixture(string root, string install, string data, string work, PortableRelease release, StagedPortableRelease staged)
        { Root = root; Install = install; Data = data; Work = work; Release = release; Staged = staged; }

        public static async Task<StageFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "cab-stage-store-" + Guid.NewGuid().ToString("N"));
            var install = Path.Combine(root, "install");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(data);
            var work = Path.Combine(PortableUpdateStageStore.GetWorkRoot(install, data), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, PortableUpdateStageStore.ArchiveFileName);
            BuildArchive(zip);
            var name = "ChatAgentBridge.Portable-0.1.0-preview.6-win-x64.zip";
            var release = new PortableRelease("0.1.0-preview.6", "v0.1.0-preview.6", name,
                new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/download/v0.1.0-preview.6/" + name),
                await HashFileAsync(zip), new FileInfo(zip).Length,
                new Uri("https://github.com/mmiyaji/chat-agent-bridge/releases/tag/v0.1.0-preview.6"));
            var staged = await PortableArchiveStager.ExtractAsync(zip, Path.Combine(work, "stage"), release, default);
            return new StageFixture(root, install, data, work, release, staged);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path.GetDirectoryName(Work)!))
            {
                try { PortableUpdateStageStore.DeleteOwnedWorkDirectory(Install, Data, Work); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
                var workRoot = Path.GetDirectoryName(Work)!;
                if (Directory.Exists(workRoot) && !Directory.EnumerateFileSystemEntries(workRoot).Any()) Directory.Delete(workRoot);
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static void BuildArchive(string path)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ChatAgentBridge.Desktop.exe"] = MakePe(),
                ["ChatAgentBridge.Desktop.dll"] = [1, 2, 3],
                ["ChatAgentBridge.Desktop.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{}"),
                ["ChatAgentBridge.Updater.exe"] = MakePe(),
                ["portable-release.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"releaseVersion\":\"0.1.0-preview.6\",\"architecture\":\"x64\"}")
            };
            files["portable-files.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 1, files = files.Keys.Append("portable-files.json").Append("SHA256SUMS.txt").ToArray() }));
            files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\n", files.Select(pair => $"{Convert.ToHexStringLower(SHA256.HashData(pair.Value))} *{pair.Key}")) + "\n");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (relative, bytes) in files)
            {
                using var output = archive.CreateEntry("ChatAgentBridge.Portable/" + relative).Open();
                output.Write(bytes);
            }
        }

        private static byte[] MakePe()
        {
            var bytes = new byte[128];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(64).CopyTo(bytes, 0x3c);
            BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 64);
            BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68);
            return bytes;
        }

        private static async Task<string> HashFileAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }
    }
}
