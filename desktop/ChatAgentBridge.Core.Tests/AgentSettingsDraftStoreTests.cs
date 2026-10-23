using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentSettingsDraftStoreTests
{
    [Fact]
    public async Task Save_replaces_one_provider_and_round_trips_incomplete_drafts()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AgentSettingsDraftStore(directory);
            await store.SaveAsync(Draft("codex", "codex", "exec --json -", "exec"));
            await store.SaveAsync(Draft("claude", "", "", "exec"));
            await store.SaveAsync(Draft("CODEX", "C:\\Tools\\codex.exe", "exec --json -", "exec") with { Model = " gpt-5 " });

            var drafts = await store.ListAsync();
            Assert.Equal(2, drafts.Count);
            Assert.Equal(new[] { "claude", "codex" }, drafts.Select(draft => draft.Provider));
            Assert.Equal("", (await store.ListAsync()).Single(draft => draft.Provider == "claude").Executable);
            Assert.Equal("gpt-5", drafts.Single(draft => draft.Provider == "codex").Model);
            Assert.True(File.Exists(Path.Combine(directory, "agent-settings-drafts.json")));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Save_rejects_unknown_provider_and_control_characters()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AgentSettingsDraftStore(directory);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Draft("other", "cli", "", "exec")));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Draft("custom", "cli\nsecret", "", "exec")));
            Assert.False(File.Exists(Path.Combine(directory, "agent-settings-drafts.json")));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Draft_file_contains_only_agent_fields_and_apply_preserves_shared_settings()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AgentSettingsDraftStore(directory);
            var draft = Draft("gemini", "node", "entry.mjs --output-format stream-json", "exec") with
            {
                Model = "gemini-pro",
                AllowNetwork = true
            };
            await store.SaveAsync(draft);

            var json = await File.ReadAllTextAsync(Path.Combine(directory, "agent-settings-drafts.json"));
            Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cliVersion", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checkedAt", json, StringComparison.OrdinalIgnoreCase);

            var defaults = new BridgeSettings
            {
                WorkspaceRoot = "C:\\workspace",
                TrustedWorkspaceRoots = "C:\\workspace\nC:\\shared",
                MattermostToken = "mattermost-secret",
                SlackBotToken = "slack-secret",
                AutoConnectProvider = "slack"
            };
            var applied = (await store.ListAsync()).Single().ApplyTo(defaults);
            Assert.Equal("gemini", applied.AgentProvider);
            Assert.Equal("node", applied.AgentExecutable);
            Assert.Equal("gemini-pro", applied.AgentModel);
            Assert.Equal(defaults.WorkspaceRoot, applied.WorkspaceRoot);
            Assert.Equal(defaults.TrustedWorkspaceRoots, applied.TrustedWorkspaceRoots);
            Assert.Equal(defaults.MattermostToken, applied.MattermostToken);
            Assert.Equal(defaults.SlackBotToken, applied.SlackBotToken);
            Assert.Equal(defaults.AutoConnectProvider, applied.AutoConnectProvider);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public void FromSettings_resolves_custom_executable_without_copying_shared_fields()
    {
        var settings = new BridgeSettings
        {
            AgentProvider = AgentProfile.Codex,
            AgentExecutable = "my-agent",
            AgentArguments = "--json",
            WorkspaceRoot = "C:\\workspace",
            TrustedWorkspaceRoots = "C:\\workspace"
        };

        var draft = AgentSettingsDraft.FromSettings(settings);
        var applied = draft.ApplyTo(settings);

        Assert.Equal(AgentProfile.Custom, draft.Provider);
        Assert.Equal("my-agent", applied.AgentExecutable);
        Assert.Equal(settings.WorkspaceRoot, applied.WorkspaceRoot);
        Assert.Equal(settings.TrustedWorkspaceRoots, applied.TrustedWorkspaceRoots);
    }

    [Fact]
    public async Task Load_rejects_malformed_data_without_overwriting_it()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "agent-settings-drafts.json");
            const string malformed = "{\"SchemaVersion\":1,\"Drafts\":[";
            await File.WriteAllTextAsync(path, malformed);

            await Assert.ThrowsAsync<InvalidDataException>(() => new AgentSettingsDraftStore(directory).ListAsync());
            Assert.Equal(malformed, await File.ReadAllTextAsync(path));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Load_rejects_duplicate_provider_drafts()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var store = new AgentSettingsDraftStore(directory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Drafts = new[] { Draft("codex", "codex", "exec --json -", "exec"), Draft("codex", "other", "args", "exec") }
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "agent-settings-drafts.json"), json);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Multiline_arguments_round_trip_without_allowing_control_characters_in_executable()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AgentSettingsDraftStore(directory);
            var arguments = "--first one\r\n--second\t\"two words\"";
            await store.SaveAsync(Draft("custom", "agent.exe", arguments, "exec"));
            Assert.Equal(arguments, Assert.Single(await store.ListAsync()).Arguments);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Draft("custom", "agent\n.exe", arguments, "exec")));
        }
        finally { DeleteDirectory(directory); }
    }

    private static AgentSettingsDraft Draft(string provider, string executable, string arguments, string mode)
        => new(provider, executable, arguments, mode, null, null, "inherit", false);

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "cab-agent-drafts-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
