using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class CliProbeServiceTests
{
    [Fact]
    public async Task ProbeRunsOnlyVersionForCodexAndClaudeFixtures()
    {
        using var fixture = new Fixture();
        var codex = fixture.CreateAgent("codex");
        var claude = fixture.CreateAgent("claude");
        var service = new CliProbeService();

        var codexResult = await service.CheckAsync(fixture.Settings(codex));
        var claudeResult = await service.CheckAsync(fixture.Settings(claude, AgentProfile.Claude));

        Assert.Contains("codex", codexResult.Version, StringComparison.OrdinalIgnoreCase);
        Assert.True(codexResult.ProviderRecognized);
        Assert.Contains("Claude Code", claudeResult.Version);
        Assert.True(claudeResult.ProviderRecognized);
    }

    [Fact]
    public async Task ProbeRejectsWrongProviderAndMissingExecutable()
    {
        using var fixture = new Fixture();
        var customFixture = fixture.CreateAgent("ChatAgentBridge.TestAgent");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CliProbeService().CheckAsync(fixture.Settings(customFixture, AgentProfile.Claude)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new CliProbeService().CheckAsync(fixture.Settings(Path.Combine(fixture.Root, "missing.exe"))));
    }

    [Fact]
    public async Task ProbeResolvesRelativeExecutableFromWorkspace()
    {
        using var fixture = new Fixture();
        fixture.CreateAgent("codex", insideWorkspace: true);

        var result = await new CliProbeService().CheckAsync(fixture.Settings("bin/codex.exe"));

        Assert.True(result.ProviderRecognized);
        Assert.Equal(Path.Combine(fixture.Workspace, "bin", "codex.exe"), result.Executable);
    }

    [Fact]
    public async Task ProbeTimesOutAndPropagatesCancellation()
    {
        using var fixture = new Fixture();
        var codex = fixture.CreateAgent("codex");
        await Assert.ThrowsAsync<TimeoutException>(() => new CliProbeService(TimeSpan.FromTicks(1)).CheckAsync(fixture.Settings(codex)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CliProbeService().CheckAsync(fixture.Settings(codex), cancellation.Token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-cli-probe-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Workspace { get; }

        public Fixture()
        {
            Root = Path.Combine(_directory, "agents");
            Workspace = Path.Combine(_directory, "workspace");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Workspace);
        }

        public string CreateAgent(string name, bool insideWorkspace = false)
        {
            var source = FindTestAgentOutput();
            var destination = insideWorkspace ? Path.Combine(Workspace, "bin") : Root;
            Directory.CreateDirectory(destination);
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var sourcePath = Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix);
                if (File.Exists(sourcePath)) File.Copy(sourcePath, Path.Combine(destination, "ChatAgentBridge.TestAgent" + suffix), overwrite: true);
            }
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), Path.Combine(destination, name + ".exe"), overwrite: true);
            var executable = Path.Combine(destination, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!File.Exists(executable)) throw new FileNotFoundException("TestAgent apphost was not built.", executable);
            return executable;
        }

        public BridgeSettings Settings(string executable, string provider = AgentProfile.Codex) => new()
        {
            AgentExecutable = executable,
            AgentProvider = provider,
            WorkspaceRoot = Workspace
        };

        private static string FindTestAgentOutput()
        {
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0");
                if (File.Exists(Path.Combine(candidate, "ChatAgentBridge.TestAgent.exe"))) return candidate;
                candidate = Path.Combine(directory.FullName, "desktop", "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0");
                if (File.Exists(Path.Combine(candidate, "ChatAgentBridge.TestAgent.exe"))) return candidate;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the built TestAgent fixture.");
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
