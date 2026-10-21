using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class CliVerificationStoreTests
{
    [Fact]
    public async Task SuccessfulVerificationReloadsAcrossStoreInstancesAndPreservesModelOptionsWithoutSecrets()
    {
        using var fixture = new Fixture();
        var first = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification();
        await first.SaveAsync(fixture.Settings, verification);

        var loaded = await new CliVerificationStore(fixture.CacheDirectory).LoadAsync(fixture.Settings);

        Assert.Equal(verification.Probe, loaded!.Probe);
        var expectedModel = Assert.Single(verification.Models);
        var actualModel = Assert.Single(loaded.Models);
        Assert.Equal(expectedModel.Id, actualModel.Id);
        Assert.Equal(expectedModel.DisplayName, actualModel.DisplayName);
        Assert.Equal(expectedModel.Description, actualModel.Description);
        Assert.Equal(expectedModel.IsDefault, actualModel.IsDefault);
        Assert.Equal(expectedModel.DefaultReasoningEffort, actualModel.DefaultReasoningEffort);
        Assert.Equal(expectedModel.SupportedReasoningEfforts.Select(option => option.Id), actualModel.SupportedReasoningEfforts.Select(option => option.Id));
        Assert.Equal(verification.CheckedAtUtc, loaded.CheckedAtUtc);
        Assert.Equal(verification.ConnectionSignature, loaded.ConnectionSignature);
        Assert.Equal("high", Assert.Single(Assert.Single(loaded.Models).SupportedReasoningEfforts).Id);
        var json = await File.ReadAllTextAsync(fixture.CachePath);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Settings.AgentArguments, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Settings.TrustedWorkspaceRoots, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadMissesWhenConnectionContextOrTrustChanges()
    {
        using var fixture = new Fixture();
        await new CliVerificationStore(fixture.CacheDirectory).SaveAsync(fixture.Settings, fixture.Verification());
        var store = new CliVerificationStore(fixture.CacheDirectory);

        Assert.Null(await store.LoadAsync(fixture.Settings with { AgentArguments = "different secret-token args" }));
        Assert.Null(await store.LoadAsync(fixture.Settings with { TrustedWorkspaceRoots = "" }));
    }

    [Fact]
    public async Task LoadMissesWhenExecutableChangesOrDisappears()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(fixture.Settings, fixture.Verification());
        File.AppendAllText(fixture.Executable, " modified");
        Assert.Null(await store.LoadAsync(fixture.Settings));

        await store.SaveAsync(fixture.Settings, fixture.Verification());
        File.Delete(fixture.Executable);
        Assert.Null(await store.LoadAsync(fixture.Settings));
    }

    [Fact]
    public async Task LoadMissesWhenNodeEntrypointChanges()
    {
        using var fixture = new Fixture();
        var node = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "node.exe" : "node");
        var entry = Path.Combine(fixture.Root, "cli.js");
        File.WriteAllText(node, "node executable");
        File.WriteAllText(entry, "cli entrypoint");
        var settings = fixture.Settings with { AgentExecutable = node, AgentArguments = entry };
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(settings, fixture.Verification(node));
        File.AppendAllText(entry, " changed");
        Assert.Null(await store.LoadAsync(settings));
    }

    [Fact]
    public async Task CorruptCacheIsAMissAndClearRemovesIt()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        Directory.CreateDirectory(fixture.CacheDirectory);
        await File.WriteAllTextAsync(fixture.CachePath, "{broken");
        Assert.Null(await store.LoadAsync(fixture.Settings));
        await store.SaveAsync(fixture.Settings, fixture.Verification());
        await store.ClearAsync();
        Assert.Null(await store.LoadAsync(fixture.Settings));
    }

    [Theory]
    [InlineData("Stamps")]
    [InlineData("Models")]
    public async Task MalformedArrayEntriesAreCacheMisses(string property)
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(fixture.Settings, fixture.Verification());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        document[property] = System.Text.Json.Nodes.JsonNode.Parse("[null]");
        await File.WriteAllTextAsync(fixture.CachePath, document.ToJsonString());

        Assert.Null(await store.LoadAsync(fixture.Settings));
    }

    [Fact]
    public async Task SuccessfulCustomProbeDoesNotRequireKnownProviderRecognition()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verified = fixture.Verification();
        verified = verified with { Probe = verified.Probe with { ProviderRecognized = false } };
        await store.SaveAsync(fixture.Settings, verified);

        Assert.NotNull(await store.LoadAsync(fixture.Settings));
    }

    [Fact]
    public async Task OversizedCacheIsIgnored()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.CacheDirectory);
        await File.WriteAllTextAsync(fixture.CachePath, new string(' ', 1_048_577));

        Assert.Null(await new CliVerificationStore(fixture.CacheDirectory).LoadAsync(fixture.Settings));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cab-cli-cache-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string Executable { get; }
        public string CacheDirectory { get; }
        public string CachePath => Path.Combine(CacheDirectory, "cli-verification.json");
        public BridgeSettings Settings { get; }

        public Fixture()
        {
            Workspace = Path.Combine(Root, "workspace");
            Executable = Path.Combine(Root, OperatingSystem.IsWindows() ? "custom.exe" : "custom");
            CacheDirectory = Path.Combine(Root, "cache");
            Directory.CreateDirectory(Workspace);
            File.WriteAllText(Executable, "cli binary");
            Settings = new BridgeSettings
            {
                WorkspaceRoot = Workspace,
                TrustedWorkspaceRoots = Workspace,
                AgentProvider = AgentProfile.Custom,
                AgentExecutable = Executable,
                AgentArguments = "--config secret-token",
                AgentMode = "exec"
            };
        }

        public CachedCliVerification Verification(string? executable = null) => new(
            new CliProbeResult(executable ?? Executable, "custom 1.2.3", false),
            [new ModelOption("model-a", "Model A", "description", true)
            {
                SupportedReasoningEfforts = [new ReasoningEffortOption("high", "Higher reasoning")],
                DefaultReasoningEffort = "high"
            }],
            DateTimeOffset.UtcNow,
            new string('A', 64));

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
