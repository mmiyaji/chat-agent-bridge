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

    [Theory]
    [InlineData(AgentProfile.Gemini, "Gemini CLI 0.8.0", true)]
    [InlineData(AgentProfile.Gemini, "0.8.0", false)]
    [InlineData(AgentProfile.GitHubCopilot, "GitHub Copilot CLI 1.2.0", true)]
    [InlineData(AgentProfile.GitHubCopilot, "Other CLI 1.2.0", false)]
    public void ProbeRecognizesProviderIdentityFromSafeVersionOrHelpOutput(string provider, string output, bool expected)
        => Assert.Equal(expected, CliProbeService.RecognizesProvider(provider, output));

    [Fact]
    public void NodeVersionProbePrefixKeepsOnlyTheScriptAndNodeOptionsBeforeIt()
    {
        var settings = new BridgeSettings
        {
            AgentProvider = AgentProfile.Gemini,
            AgentExecutable = "node.exe",
            AgentArguments = "--no-warnings \"C:\\Program Files\\Gemini\\index.js\" --output-format text"
        };

        Assert.Equal(new[] { "--no-warnings", "C:\\Program Files\\Gemini\\index.js" },
            AgentProfile.GetNodeProbePrefix(settings, AgentProfile.Gemini));

        var eval = settings with { AgentArguments = "--eval \"process.exit(0)\"" };
        Assert.Throws<InvalidOperationException>(() => AgentProfile.GetNodeProbePrefix(eval, AgentProfile.Gemini));
        var missingScript = settings with { AgentArguments = "--no-warnings --output-format text" };
        Assert.Throws<InvalidOperationException>(() => AgentProfile.GetNodeProbePrefix(missingScript, AgentProfile.Gemini));
    }

    [Fact]
    public void WindowsNpmPackageResolverUsesVerifiedPackageMetadataWithoutReadingShimCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var bin = Path.Combine(fixture.Root, "npm-bin");
        var package = Path.Combine(bin, "node_modules", "@google", "gemini-cli");
        Directory.CreateDirectory(package);
        var shim = Path.Combine(bin, "gemini.cmd");
        File.WriteAllText(shim, "this text must never be executed");
        File.WriteAllText(Path.Combine(bin, "node.exe"), "fixture");
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@google/gemini-cli\",\"bin\":{\"gemini\":\"dist/index.js\"}}");
        Directory.CreateDirectory(Path.Combine(package, "dist"));
        File.WriteAllText(Path.Combine(package, "dist", "index.js"), "// fixture");

        var result = NodeCliPackageResolver.Resolve(shim, AgentProfile.Gemini);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(Path.Combine(bin, "node.exe")), result.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(package, "dist", "index.js")), result.EntryPoint);
        Assert.Equal("@google/gemini-cli", result.PackageName);
    }

    [Fact]
    public void WindowsNpmPackageResolverDoesNotReplaceAnExplicitCustomExecutableOrTrustEscapingBinPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var custom = Path.Combine(fixture.Root, "custom-agent.exe");
        File.WriteAllText(custom, "fixture");
        Assert.Null(NodeCliPackageResolver.Resolve(custom, AgentProfile.Gemini));
        var customBare = Path.Combine(fixture.Root, "custom-agent");
        File.WriteAllText(customBare + ".cmd", "must not be treated as Gemini");
        Assert.Null(NodeCliPackageResolver.Resolve(customBare, AgentProfile.Gemini));

        var bin = Path.Combine(fixture.Root, "npm-bin");
        var package = Path.Combine(bin, "node_modules", "@google", "gemini-cli");
        Directory.CreateDirectory(package);
        var shim = Path.Combine(bin, "gemini.cmd");
        File.WriteAllText(shim, "must not execute");
        File.WriteAllText(Path.Combine(bin, "node.exe"), "fixture");
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@google/gemini-cli\",\"bin\":{\"gemini\":\"../../../../outside.js\"}}");

        var error = Assert.Throws<InvalidOperationException>(() => NodeCliPackageResolver.Resolve(shim, AgentProfile.Gemini));
        Assert.Contains("entrypoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvedWindowsGeminiPackageSupportsVersionProbeAndShellFreeStdinRun()
    {
        if (!OperatingSystem.IsWindows() || FindNodeExecutable() is null) return;
        using var fixture = new Fixture();
        var bin = Path.Combine(fixture.Root, "npm-bin");
        var package = Path.Combine(bin, "node_modules", "@google", "gemini-cli");
        Directory.CreateDirectory(Path.Combine(package, "dist"));
        var shim = Path.Combine(bin, "gemini.cmd");
        File.WriteAllText(shim, "this command shim must not run");
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@google/gemini-cli\",\"bin\":{\"gemini\":\"dist/index.js\"}}");
        File.WriteAllText(Path.Combine(package, "dist", "index.js"), """
            if (process.argv.includes('--version')) { console.log('1.2.3'); process.exit(0); }
            let input = '';
            process.stdin.setEncoding('utf8');
            process.stdin.on('data', chunk => input += chunk);
            process.stdin.on('end', () => process.stdout.write(`stdin:${input}`));
            """);
        var settings = fixture.Settings(shim, AgentProfile.Gemini) with { AgentArguments = AgentProfile.DefaultGeminiArguments };

        var probe = await new CliProbeService().CheckAsync(settings);
        var run = await new AgentRunner().RunAsync(settings, "prompt with spaces");

        Assert.Equal("1.2.3", probe.Version);
        Assert.True(probe.ProviderRecognized);
        Assert.True(run.Success, run.Error);
        Assert.Equal("stdin:prompt with spaces", run.Output);
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

    private static string? FindNodeExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "node.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
