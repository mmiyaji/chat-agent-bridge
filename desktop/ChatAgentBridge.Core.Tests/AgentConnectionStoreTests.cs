using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentConnectionStoreTests
{
    [Fact]
    public async Task Registration_replaces_provider_profile_and_round_trips_metadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-connections-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AgentConnectionStore(directory);
            var checkedAt = DateTimeOffset.UtcNow;
            await store.SaveVerifiedAsync(Profile("codex", "codex", "exec --json -", "exec", checkedAt));
            await store.SaveVerifiedAsync(Profile("claude", "claude", "", "exec", checkedAt));
            await store.SaveVerifiedAsync(Profile("codex", "C:\\Tools\\codex.exe", "exec --json -", "exec", checkedAt) with { Model = "gpt-5" });

            var profiles = await store.ListAsync();
            Assert.Equal(2, profiles.Count);
            Assert.Equal("C:\\Tools\\codex.exe", (await store.FindAsync("CODEX"))!.Executable);
            Assert.Equal("gpt-5", (await store.FindAsync("codex"))!.Model);
            Assert.Equal(checkedAt.ToUniversalTime(), (await store.FindAsync("claude"))!.CheckedAt);
            Assert.True(File.Exists(Path.Combine(directory, "agent-connections.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Save_rejects_unsupported_provider_and_noncanonical_arguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-connections-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AgentConnectionStore(directory);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(Profile("gemini", "gemini", "", "exec", DateTimeOffset.UtcNow)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(Profile("codex", "codex", "--dangerous", "exec", DateTimeOffset.UtcNow)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(Profile("claude", "claude", "--api-key secret", "exec", DateTimeOffset.UtcNow)));
            Assert.False(File.Exists(Path.Combine(directory, "agent-connections.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ApplyTo_changes_only_agent_configuration()
    {
        var defaults = new BridgeSettings
        {
            WorkspaceRoot = "C:\\workspace",
            MattermostToken = "local-token",
            SlackBotToken = "slack-token",
            TrustedWorkspaceRoots = "C:\\workspace\nC:\\other",
            AutoConnectProvider = "slack"
        };
        var profile = Profile("claude", "claude", "", "exec", DateTimeOffset.UtcNow) with
        {
            Model = "sonnet",
            ReasoningEffort = "high",
            PermissionMode = "workspace-write",
            AllowNetwork = true
        };

        var applied = profile.ApplyTo(defaults);

        Assert.Equal("claude", applied.AgentProvider);
        Assert.Equal("sonnet", applied.AgentModel);
        Assert.Equal("C:\\workspace", applied.WorkspaceRoot);
        Assert.Equal("local-token", applied.MattermostToken);
        Assert.Equal("slack-token", applied.SlackBotToken);
        Assert.Equal(defaults.TrustedWorkspaceRoots, applied.TrustedWorkspaceRoots);
        Assert.Equal("slack", applied.AutoConnectProvider);
    }

    [Fact]
    public async Task Load_rejects_duplicate_provider_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-connections-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new AgentConnectionStore(directory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Profiles = new[]
                {
                    Profile("codex", "codex", "exec --json -", "exec", DateTimeOffset.UtcNow),
                    Profile("codex", "codex2", "exec --json -", "exec", DateTimeOffset.UtcNow)
                }
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "agent-connections.json"), json);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Separate_store_instances_serialize_provider_registrations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cab-connections-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AgentConnectionStore(directory);
            var second = new AgentConnectionStore(directory);
            await Task.WhenAll(
                first.SaveVerifiedAsync(Profile("codex", "codex", "exec --json -", "exec", DateTimeOffset.UtcNow)),
                second.SaveVerifiedAsync(Profile("claude", "claude", "", "exec", DateTimeOffset.UtcNow)));

            Assert.Equal(2, (await first.ListAsync()).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static AgentConnectionProfile Profile(string provider, string executable, string arguments, string mode, DateTimeOffset checkedAt)
        => new(provider, executable, arguments, mode, null, null, "inherit", false, "2.1.0", checkedAt);
}
