using ChatAgentBridge.Core;
using System.Globalization;
using System.Diagnostics;

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
        var missing = Path.Combine(fixture.Root, "missing.exe");
        var missingError = await Assert.ThrowsAsync<FileNotFoundException>(() => new CliProbeService().CheckAsync(fixture.Settings(missing)));
        Assert.Contains(missing, missingError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeExplainsKnownMissingDotNetRuntimeWithoutReturningRawStderr()
    {
        using var fixture = new Fixture();
        var codex = fixture.CreateAgent("cab-probe-runtime-error");
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CliProbeService().CheckAsync(fixture.Settings(codex)));

            Assert.Contains(".NET runtime", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("install", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("You must install or update .NET", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public async Task ProbeReportsVersionExitCodeAndGuidanceWithoutLeakingArbitraryStderr()
    {
        using var fixture = new Fixture();
        var codex = fixture.CreateAgent("cab-probe-version-error");
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CliProbeService().CheckAsync(fixture.Settings(codex)));

            Assert.Contains("73", error.Message, StringComparison.Ordinal);
            Assert.Contains("--version", error.Message, StringComparison.Ordinal);
            Assert.Contains("permissions", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture secret stderr", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("en", "choose a workspace", "absolute path", "does not exist", "Trusted Folders", "invalid")]
    [InlineData("ja", "作業フォルダーを選択", "絶対パス", "見つかりません", "信頼するフォルダー", "無効")]
    public async Task ProbeReportsActionableLocalizedWorkspaceErrorsBeforeExecutableLookup(
        string cultureName, string requiredText, string absoluteText, string missingText, string trustedText, string invalidText)
    {
        using var fixture = new Fixture();
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            var service = new CliProbeService();
            var missingExecutable = Path.Combine(fixture.Root, "must-not-be-looked-up.exe");

            var required = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CheckAsync(fixture.Settings(missingExecutable) with { WorkspaceRoot = " " }));
            Assert.Contains(requiredText, required.Message, StringComparison.OrdinalIgnoreCase);

            const string relative = "relative-workspace";
            var absolute = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CheckAsync(fixture.Settings(missingExecutable) with { WorkspaceRoot = relative }));
            Assert.Contains(relative, absolute.Message, StringComparison.Ordinal);
            Assert.Contains(absoluteText, absolute.Message, StringComparison.OrdinalIgnoreCase);

            var malformedPath = fixture.Workspace + "\0invalid";
            var malformed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CheckAsync(fixture.Settings(missingExecutable) with { WorkspaceRoot = malformedPath }));
            Assert.Contains(invalidText, malformed.Message, StringComparison.OrdinalIgnoreCase);

            var nonexistentPath = Path.Combine(fixture.Root, "not-a-workspace");
            var nonexistent = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CheckAsync(fixture.Settings(missingExecutable) with { WorkspaceRoot = nonexistentPath }));
            Assert.Contains(nonexistentPath, nonexistent.Message, StringComparison.Ordinal);
            Assert.Contains(missingText, nonexistent.Message, StringComparison.OrdinalIgnoreCase);

            var untrusted = await Assert.ThrowsAsync<CliWorkspaceTrustException>(() =>
                service.CheckAsync(fixture.Settings(missingExecutable) with { TrustedWorkspaceRoots = fixture.Root }));
            Assert.Contains(fixture.Workspace, untrusted.Message, StringComparison.Ordinal);
            Assert.Contains(trustedText, untrusted.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(fixture.Workspace), untrusted.WorkspacePath);
            Assert.Null(untrusted.SuggestedWorkspacePath);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public async Task ProbeProvidesSafeRealFolderSuggestionForWorkspaceThatIsAJunction()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "real-workspace");
        var junction = Path.Combine(fixture.Root, "workspace-junction");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(junction, target);
        try
        {
            var settings = fixture.Settings(Path.Combine(fixture.Root, "missing.exe")) with { WorkspaceRoot = junction };

            Assert.False(ExecutionPolicy.IsWorkspaceTrusted(settings, junction));
            var error = await Assert.ThrowsAsync<CliWorkspaceTrustException>(() => new CliProbeService().CheckAsync(settings));

            Assert.Equal(Path.GetFullPath(junction), error.WorkspacePath);
            var suggested = Assert.IsType<string>(error.SuggestedWorkspacePath);
            Assert.Equal(Path.GetFullPath(target), suggested);
            Assert.True(Path.IsPathFullyQualified(suggested));
            Assert.True(Directory.Exists(suggested));
            Assert.False((new DirectoryInfo(suggested).Attributes & FileAttributes.ReparsePoint) != 0);
            Assert.Contains(suggested, error.Message, StringComparison.Ordinal);

            var trustedSuggestion = settings with
            {
                WorkspaceRoot = suggested,
                TrustedWorkspaceRoots = suggested
            };
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(trustedSuggestion, suggested));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task ProbeResolvesAncestorJunctionsInSafeRealFolderSuggestion()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "real-parent");
        var actualWorkspace = Path.Combine(target, "workspace");
        var junction = Path.Combine(fixture.Root, "parent-junction");
        var linkedWorkspace = Path.Combine(junction, "workspace");
        var unrelatedTrustedRoot = Path.Combine(fixture.Root, "other-trusted-root");
        Directory.CreateDirectory(actualWorkspace);
        Directory.CreateDirectory(unrelatedTrustedRoot);
        CreateDirectoryLink(junction, target);
        try
        {
            var settings = fixture.Settings(Path.Combine(fixture.Root, "missing.exe")) with
            {
                WorkspaceRoot = linkedWorkspace,
                TrustedWorkspaceRoots = unrelatedTrustedRoot
            };

            Assert.False(ExecutionPolicy.IsWorkspaceTrusted(settings, linkedWorkspace));
            var error = await Assert.ThrowsAsync<CliWorkspaceTrustException>(() => new CliProbeService().CheckAsync(settings));

            var suggested = Assert.IsType<string>(error.SuggestedWorkspacePath);
            Assert.Equal(Path.GetFullPath(actualWorkspace), suggested);
            Assert.True(Directory.Exists(suggested));
            Assert.False((new DirectoryInfo(suggested).Attributes & FileAttributes.ReparsePoint) != 0);
        }
        finally { Directory.Delete(junction); }
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

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target }
        }) ?? throw new InvalidOperationException("Could not create test directory junction.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
