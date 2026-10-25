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
        Assert.Equal(verification.ModelsFetchedAtUtc, loaded.ModelsFetchedAtUtc);
        Assert.Equal(verification.ConnectionSignature, loaded.ConnectionSignature);
        Assert.Equal("high", Assert.Single(Assert.Single(loaded.Models).SupportedReasoningEfforts).Id);
        var json = await File.ReadAllTextAsync(fixture.CachePath);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Settings.AgentArguments, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Settings.TrustedWorkspaceRoots, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelRetrievalTimeRoundTripsIndependentlyOfCliCheckAndLegacyMissingTimeStaysUnknown()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification() with { ModelsFetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-12).ToOffset(TimeSpan.FromHours(9)) };
        await store.SaveAsync(fixture.Settings, verification);

        var loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Equal(verification.CheckedAtUtc, loaded.CheckedAtUtc);
        Assert.Equal(verification.ModelsFetchedAtUtc, loaded.ModelsFetchedAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.ModelsFetchedAtUtc!.Value.Offset);
        Assert.True(ModelCatalogFreshness.IsRefreshRecommended(loaded.Models.Count, loaded.ModelsFetchedAtUtc, DateTimeOffset.UtcNow));

        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        document.AsObject().Remove("ModelsFetchedAtUtc");
        await File.WriteAllTextAsync(fixture.CachePath, document.ToJsonString());
        loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Null(loaded.ModelsFetchedAtUtc);
        Assert.Single(loaded.Models);
        Assert.Equal(verification.CheckedAtUtc, loaded.CheckedAtUtc);
    }

    [Fact]
    public async Task InvalidModelRetrievalTimeDoesNotInvalidateCliDiagnosticsOrClaimFreshness()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(fixture.Settings, fixture.Verification());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        document["ModelsFetchedAtUtc"] = DateTimeOffset.UtcNow.AddDays(30);
        await File.WriteAllTextAsync(fixture.CachePath, document.ToJsonString());

        var loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Null(loaded.ModelsFetchedAtUtc);
        Assert.Single(loaded.Models);
        var refreshedAt = DateTimeOffset.UtcNow;
        Assert.True(await store.UpdateModelsAsync(fixture.Settings, loaded.CheckedAtUtc, loaded.ConnectionSignature, loaded.Models, refreshedAt));
        Assert.Equal(refreshedAt, (await store.LoadAsync(fixture.Settings))!.ModelsFetchedAtUtc);
    }

    [Fact]
    public async Task ModelOnlyUpdatePreservesCliProbeCheckAndConnectionSignature()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification() with { ModelsFetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        await store.SaveAsync(fixture.Settings, verification);
        var fetchedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        ModelOption[] models = [new("new-model", "New model", "Updated catalog", true)];

        Assert.True(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, verification.ConnectionSignature, models, fetchedAt));

        var loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Equal(verification.Probe, loaded.Probe);
        Assert.Equal(verification.CheckedAtUtc, loaded.CheckedAtUtc);
        Assert.Equal(verification.ConnectionSignature, loaded.ConnectionSignature);
        Assert.Equal(fetchedAt, loaded.ModelsFetchedAtUtc);
        Assert.Equal("new-model", Assert.Single(loaded.Models).Id);
        // An older in-flight catalog cannot replace a newer result.
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, verification.ConnectionSignature, [], fetchedAt.AddMinutes(-1)));
        Assert.Single((await store.LoadAsync(fixture.Settings))!.Models);
        // A successful empty catalog removes stale choices without claiming models are available.
        Assert.True(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, verification.ConnectionSignature, [], fetchedAt.AddMilliseconds(1)));
        loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Empty(loaded.Models);
        Assert.False(ModelCatalogFreshness.IsRefreshRecommended(loaded.Models.Count, loaded.ModelsFetchedAtUtc, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ModelOnlyUpdateCannotCreateVerificationOrOverwriteReplacedOrInvalidDiagnostics()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification();
        var fetchedAt = DateTimeOffset.UtcNow;
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, verification.ConnectionSignature, [], fetchedAt));
        Assert.False(File.Exists(fixture.CachePath));

        await store.SaveAsync(fixture.Settings, verification);
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc.AddTicks(1), verification.ConnectionSignature, [], fetchedAt));
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, new string('B', 64), [], fetchedAt));
        Assert.False(await store.UpdateModelsAsync(fixture.Settings with { AgentArguments = "changed" }, verification.CheckedAtUtc, verification.ConnectionSignature, [], fetchedAt));
        File.AppendAllText(fixture.Executable, " modified");
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc, verification.ConnectionSignature, [], fetchedAt));
    }

    [Fact]
    public async Task CancelledModelOnlyUpdatePreservesExistingCatalogAndTime()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification() with { ModelsFetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        await store.SaveAsync(fixture.Settings, verification);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateModelsAsync(fixture.Settings,
            verification.CheckedAtUtc, verification.ConnectionSignature, [], DateTimeOffset.UtcNow, cancellation.Token));

        var loaded = (await store.LoadAsync(fixture.Settings))!;
        Assert.Single(loaded.Models);
        Assert.Equal(verification.ModelsFetchedAtUtc, loaded.ModelsFetchedAtUtc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ModelOnlyUpdatePreservesOriginalCliContextAndExecutableStamps(int schemaVersion)
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        var verification = fixture.Verification();
        await store.SaveAsync(fixture.Settings, verification);
        var before = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        if (schemaVersion == 1)
        {
            before["SchemaVersion"] = 1;
            before["ContextFingerprint"] = CliVerificationStore.CreateContextFingerprint(fixture.Settings);
            await File.WriteAllTextAsync(fixture.CachePath, before.ToJsonString());
        }

        Assert.True(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc,
            verification.ConnectionSignature, [], DateTimeOffset.UtcNow));

        var after = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        foreach (var property in new[] { "Models", "ModelsFetchedAtUtc" })
        {
            before.AsObject().Remove(property);
            after.AsObject().Remove(property);
        }
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before, after));
        // Replacing a CLI requires another explicit check, even after a model-only update.
        var saved = await File.ReadAllTextAsync(fixture.CachePath);
        File.AppendAllText(fixture.Executable, " replaced binary");
        Assert.False(await store.UpdateModelsAsync(fixture.Settings, verification.CheckedAtUtc,
            verification.ConnectionSignature, verification.Models, DateTimeOffset.UtcNow));
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.CachePath));
        Assert.Null(await store.LoadAsync(fixture.Settings));
    }

    [Fact]
    public async Task LoadMissesWhenConnectionContextOrTrustChanges()
    {
        using var fixture = new Fixture();
        await new CliVerificationStore(fixture.CacheDirectory).SaveAsync(fixture.Settings, fixture.Verification());
        var store = new CliVerificationStore(fixture.CacheDirectory);

        Assert.Null(await store.LoadAsync(fixture.Settings with { AgentArguments = "different secret-token args" }));
        Assert.Null(await store.LoadAsync(fixture.Settings with { TrustedWorkspaceRoots = "" }));
        Assert.Null(await store.LoadAsync(fixture.Settings with { AgentMode = "app-server" }));
    }

    [Fact]
    public async Task ProviderCacheIsSharedAcrossInstancesAndDoesNotOverwriteAnotherProvider()
    {
        using var fixture = new Fixture();
        var custom = CliVerificationStore.ForProvider(fixture.CacheDirectory, AgentProfile.Custom);
        await custom.SaveAsync(fixture.Settings, fixture.Verification());
        var antigravitySettings = fixture.Settings with { AgentProvider = AgentProfile.Antigravity, AgentArguments = AgentProfile.DefaultAntigravityArguments };
        var antigravityVerification = fixture.Verification() with
        {
            Probe = new CliProbeResult(fixture.Executable, "Antigravity 1.2.3", true),
            Models = []
        };
        await CliVerificationStore.ForProvider(fixture.CacheDirectory, AgentProfile.Antigravity)
            .SaveAsync(antigravitySettings, antigravityVerification);

        Assert.NotNull(await CliVerificationStore.ForProvider(fixture.CacheDirectory, AgentProfile.Custom).LoadAsync(fixture.Settings));
        var loaded = await CliVerificationStore.ForProvider(fixture.CacheDirectory, AgentProfile.Antigravity).LoadAsync(antigravitySettings);
        Assert.Equal(antigravityVerification.Probe, loaded!.Probe);
        Assert.Empty(loaded.Models); // CLI success does not require a reasoning/model catalog.
        Assert.Null(await new CliVerificationStore(fixture.CacheDirectory).LoadAsync(fixture.Settings));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("custom/../../outside")]
    [InlineData("")]
    public void ProviderCacheRejectsInvalidProviderDirectories(string provider)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => CliVerificationStore.ForProvider(fixture.CacheDirectory, provider));
    }

    [Fact]
    public async Task ResolvedExecutableSpellingDoesNotInvalidateDiagnosticsOrModels()
    {
        using var fixture = new Fixture();
        var relative = fixture.Settings with { AgentExecutable = Path.Combine("..", Path.GetFileName(fixture.Executable)) };
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(relative, fixture.Verification());

        Assert.True(CliVerificationStore.HasSameContext(relative, fixture.Settings));
        var loaded = await store.LoadAsync(fixture.Settings);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Models);
        Assert.False(CliVerificationStore.HasSameContext(relative, fixture.Settings with { AgentArguments = "--other" }));
    }

    [Fact]
    public async Task LegacyCacheStillRestoresOnlyInItsOriginalContext()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(fixture.Settings, fixture.Verification());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        document["SchemaVersion"] = 1;
        document["ContextFingerprint"] = CliVerificationStore.CreateContextFingerprint(fixture.Settings);
        await File.WriteAllTextAsync(fixture.CachePath, document.ToJsonString());

        Assert.NotNull(await store.LoadAsync(fixture.Settings));
        Assert.Null(await store.LoadAsync(fixture.Settings with { AgentArguments = "--changed" }));
    }

    [Fact]
    public async Task ModelOrPermissionSelectionDoesNotEraseCliDiagnostics()
    {
        using var fixture = new Fixture();
        var store = new CliVerificationStore(fixture.CacheDirectory);
        await store.SaveAsync(fixture.Settings, fixture.Verification());

        var loaded = await store.LoadAsync(fixture.Settings with
        {
            AgentModel = "different-model", AgentReasoningEffort = "low", AgentPermissionMode = "workspace-write", AllowAgentNetwork = true
        });

        Assert.NotNull(loaded);
        Assert.Equal(fixture.Verification().ConnectionSignature, loaded.ConnectionSignature);
        Assert.Single(loaded.Models);
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
