using System.Text.Json.Nodes;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AgentProfile.Gemini, "gemini", AgentProfile.DefaultGeminiArguments)]
    [InlineData(AgentProfile.GitHubCopilot, "copilot", AgentProfile.DefaultGitHubCopilotArguments)]
    public async Task SessionCliModelNamesPersistWithoutCatalogValidation(string provider, string executable, string arguments)
    {
        var settings = new BridgeSettings
        {
            AgentProvider = provider, AgentExecutable = executable, AgentArguments = arguments,
            AgentModel = "  future-model-alias  ", AgentPermissionMode = "inherit"
        };
        var store = new SettingsStore(_directory);

        await store.SaveAsync(settings);
        var restored = await store.LoadAsync();

        Assert.Equal(settings with { AgentModel = "future-model-alias" }, restored);
        await store.SaveAsync(restored with { AgentModel = " " });
        Assert.Null((await store.LoadAsync()).AgentModel);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("first", true)]
    [InlineData("always", false)]
    public async Task SwitchCommandAutoApprovalPersistsWithFirstOrAutomaticApproval(string approvalMode, bool expected)
    {
        var store = new SettingsStore(_directory);

        await store.SaveAsync(new BridgeSettings { ChatApprovalMode = approvalMode, AutoApproveSwitchCommands = true });

        Assert.Equal(expected, (await store.LoadAsync()).AutoApproveSwitchCommands);
        // Loading a hand-edited file applies the same supported approval modes.
        var path = Path.Combine(_directory, "settings.json");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored["AutoApproveSwitchCommands"] = true;
        await File.WriteAllTextAsync(path, stored.ToJsonString());
        Assert.Equal(expected, (await store.LoadAsync()).AutoApproveSwitchCommands);
    }

    [Theory]
    [InlineData(AgentProfile.Gemini, "AgentReasoningEffort", "high")]
    [InlineData(AgentProfile.GitHubCopilot, "AgentReasoningEffort", "high")]
    [InlineData(AgentProfile.Gemini, "AgentPermissionMode", "workspace-write")]
    [InlineData(AgentProfile.GitHubCopilot, "AgentPermissionMode", "full-access")]
    public async Task SessionCliManagedOverridesAreRejectedOnSaveAndLoad(string provider, string property, string value)
    {
        var settings = new BridgeSettings { AgentProvider = provider, AgentModel = "model-alias" };
        var store = new SettingsStore(_directory);
        var invalid = property == "AgentReasoningEffort"
            ? settings with { AgentReasoningEffort = value }
            : settings with { AgentPermissionMode = value };
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));

        await store.SaveAsync(settings);
        var path = Path.Combine(_directory, "settings.json");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored[property] = value;
        await File.WriteAllTextAsync(path, stored.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_MissingSettingsOnAvailableVolumeIsFirstRun()
    {
        var store = new SettingsStore(Path.Combine(_directory, "not-created-yet"));

        var settings = await store.LoadAsync();

        Assert.NotNull(settings);
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    public async Task LoadAsync_InvalidSettingsNeverBecomeDefaultsAndDeletedSavedFileFailsClosed()
    {
        Directory.CreateDirectory(_directory);
        var store = new SettingsStore(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), "{");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        Assert.NotNull(await store.LoadAsync());
        File.Delete(Path.Combine(_directory, "settings.json"));
        await Assert.ThrowsAnyAsync<IOException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RecoversWhenWindowsReadLockIsReleasedWithinRetryWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SettingsStore(_directory);
        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        var lockStream = new FileStream(Path.Combine(_directory, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var release = Task.Run(async () =>
            {
                await Task.Delay(20);
                lockStream.Dispose();
            });
            var loaded = await store.LoadAsync();
            await release;
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), loaded.WorkspaceRoot);
        }
        finally { lockStream.Dispose(); }
    }

    [Fact]
    public async Task SaveAsync_LongWindowsReadLockFailsAfterBoundedRetriesWithoutChangingSavedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SettingsStore(_directory);
        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        var path = Path.Combine(_directory, "settings.json");
        var before = await File.ReadAllBytesAsync(path);
        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Environment.CurrentDirectory) }));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected exception type: {error?.GetType().Name}");
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LoadAsync_ExistingFileInDataPathAndMissingVolumeDoNotBecomeDefaults()
    {
        Directory.CreateDirectory(_directory);
        var dataPath = Path.Combine(_directory, "data-file");
        await File.WriteAllTextAsync(dataPath, "not a directory");
        await Assert.ThrowsAnyAsync<IOException>(() => new SettingsStore(dataPath).LoadAsync());

        if (!OperatingSystem.IsWindows()) return;
        var usedRoots = new HashSet<string>(Directory.GetLogicalDrives(), StringComparer.OrdinalIgnoreCase);
        var missingRoot = Enumerable.Range('C', 'Z' - 'C' + 1)
            .Select(value => $"{(char)value}:\\")
            .FirstOrDefault(root => !usedRoots.Contains(root) && !Directory.Exists(root));
        if (missingRoot is null) return;
        await Assert.ThrowsAnyAsync<IOException>(() => new SettingsStore(Path.Combine(missingRoot, "ChatAgentBridge" )).LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_AcceptsAbsoluteAndEmptyWorkspaceRoots()
    {
        var store = new SettingsStore(_directory);

        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), (await store.LoadAsync()).WorkspaceRoot);

        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = "" });
        Assert.Equal("", (await store.LoadAsync()).WorkspaceRoot);
    }

    [Fact]
    public async Task DiscordSettingsPersistAndLegacyFilesDefaultDiscordFields()
    {
        var store = new SettingsStore(_directory);
        var settings = new BridgeSettings
        {
            DiscordAllowedUserIds = "123456789012345678;123456789012345678",
            DiscordAllowedGuildIds = "223456789012345678",
            DiscordAllowedChannelIds = "323456789012345678",
            DiscordAllowedThreadIds = "423456789012345678",
            DiscordAllowDirectMessages = true,
            AutoConnectProvider = "discord"
        };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Assert.Equal("123456789012345678", loaded.DiscordAllowedUserIds);
        Assert.Equal(settings.DiscordAllowedGuildIds, loaded.DiscordAllowedGuildIds);
        Assert.Equal(settings.DiscordAllowedChannelIds, loaded.DiscordAllowedChannelIds);
        Assert.Equal(settings.DiscordAllowedThreadIds, loaded.DiscordAllowedThreadIds);
        Assert.True(loaded.DiscordAllowDirectMessages);
        Assert.Equal("discord", loaded.AutoConnectProvider);

        var path = Path.Combine(_directory, "settings.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var name in new[] { "DiscordBotToken", "DiscordAllowedUserIds", "DiscordAllowedGuildIds", "DiscordAllowedChannelIds", "DiscordAllowedThreadIds", "DiscordAllowDirectMessages" })
            json.AsObject().Remove(name);
        await File.WriteAllTextAsync(path, json.ToJsonString());
        loaded = await store.LoadAsync();
        Assert.Empty(loaded.DiscordBotToken);
        Assert.Empty(loaded.DiscordAllowedUserIds);
        Assert.False(loaded.DiscordAllowDirectMessages);
    }

    [Theory]
    [InlineData("DiscordAllowedUserIds")]
    [InlineData("DiscordAllowedGuildIds")]
    [InlineData("DiscordAllowedChannelIds")]
    [InlineData("DiscordAllowedThreadIds")]
    public async Task SaveRejectsMalformedDiscordListsWithoutChangingSettings(string property)
    {
        var store = new SettingsStore(_directory);
        await store.SaveAsync(new BridgeSettings());
        var path = Path.Combine(_directory, "settings.json");
        var original = await File.ReadAllBytesAsync(path);
        var invalid = new BridgeSettings();
        invalid = property switch
        {
            "DiscordAllowedUserIds" => invalid with { DiscordAllowedUserIds = "-1" },
            "DiscordAllowedGuildIds" => invalid with { DiscordAllowedGuildIds = "-1" },
            "DiscordAllowedChannelIds" => invalid with { DiscordAllowedChannelIds = "-1" },
            _ => invalid with { DiscordAllowedThreadIds = "-1" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DiscordBotTokenUsesWindowsDpapiAndRoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SettingsStore(_directory);
        const string token = "discord-secret-token";
        await store.SaveAsync(new BridgeSettings { DiscordBotToken = token });
        var serialized = await File.ReadAllTextAsync(Path.Combine(_directory, "settings.json"));
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.Contains("dpapi:", serialized, StringComparison.Ordinal);
        Assert.Equal(token, (await store.LoadAsync()).DiscordBotToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative/workspace")]
    [InlineData("C:relative\\workspace")]
    [InlineData("bad\u0001path")]
    public async Task SaveAsync_RejectsInvalidWorkspaceRootsWithoutChangingExistingFile(string? workspaceRoot)
    {
        var store = new SettingsStore(_directory);
        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        var path = Path.Combine(_directory, "settings.json");
        var original = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new BridgeSettings { WorkspaceRoot = workspaceRoot! }));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SaveAsync_RejectsWorkspaceRootLongerThan2048WithoutChangingExistingFile()
    {
        var store = new SettingsStore(_directory);
        await store.SaveAsync(new BridgeSettings { WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) });
        var path = Path.Combine(_directory, "settings.json");
        var original = await File.ReadAllBytesAsync(path);
        var tooLong = Path.GetPathRoot(Path.GetTempPath())! + new string('a', 2048);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new BridgeSettings { WorkspaceRoot = tooLong }));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
