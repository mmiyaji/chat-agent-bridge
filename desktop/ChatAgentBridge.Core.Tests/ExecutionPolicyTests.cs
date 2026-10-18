using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ExecutionPolicyTests
{
    [Fact]
    public void LegacySettingsReceiveSafePolicyDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<BridgeSettings>("{\"WorkspaceRoot\":\"/tmp/work\"}")!;
        Assert.Equal("inherit", settings.AgentPermissionMode);
        Assert.False(settings.AllowAgentNetwork);
        Assert.Equal("", settings.TrustedWorkspaceRoots);
        Assert.Equal("always", settings.ChatApprovalMode);
        Assert.Equal("manual", settings.ChatResultMode);
        Assert.Equal("", settings.SlackAllowedChannelIds);
    }

    [Fact]
    public void TrustedWorkspaceRequiresPathContainment()
    {
        var parent = Path.Combine(Path.GetTempPath(), "cab-policy-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var sibling = Path.Combine(parent, "workspace-escape");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        var grandchild = Path.Combine(root, "child", "grandchild");
        Directory.CreateDirectory(grandchild);
        try
        {
            var settings = new BridgeSettings { WorkspaceRoot = root };
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings, root));
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings, Path.Combine(root, "child")));
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings, grandchild));
            Assert.False(ExecutionPolicy.IsWorkspaceTrusted(settings, sibling));
            Assert.False(ExecutionPolicy.IsWorkspaceTrusted(settings, Path.Combine(parent, "elsewhere")));
            Assert.False(ExecutionPolicy.IsWorkspaceTrusted(settings with { TrustedWorkspaceRoots = sibling }, root));
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings with { WorkspaceRoot = sibling, TrustedWorkspaceRoots = root }, Path.Combine(root, "child")));
            Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings with { WorkspaceRoot = sibling, TrustedWorkspaceRoots = root }, grandchild));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task SettingsStorePersistsPolicyAndRejectsInvalidScopeIds()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            var settings = new BridgeSettings
            {
                WorkspaceRoot = Path.GetTempPath(),
                AgentPermissionMode = "workspace-write", AllowAgentNetwork = true,
                TrustedWorkspaceRoots = Path.GetTempPath(), ChatApprovalMode = "first", ChatResultMode = "auto",
                SlackAllowedChannelIds = "C123, C456", SlackAllowedThreadIds = "123.456", MattermostAllowedThreadIds = "thread-1"
            };
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.Equal(settings with
            {
                TrustedWorkspaceRoots = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                SlackAllowedChannelIds = "C123,C456", SlackAllowedThreadIds = "123.456"
            }, loaded);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { SlackAllowedChannelIds = "C/1" }));
            await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{\"SchemaVersion\":1,\"SlackAllowedThreadIds\":\"bad/id\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStorePersistsBasicCliProvidersAndRejectsManagedOverrides()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            var settings = new BridgeSettings
            {
                AgentProvider = AgentProfile.Gemini,
                AgentExecutable = "gemini",
                AgentArguments = AgentProfile.DefaultGeminiArguments,
                AgentPermissionMode = "inherit"
            };
            await store.SaveAsync(settings);
            Assert.Equal(settings, await store.LoadAsync());

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentModel = "model-x" }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentReasoningEffort = "high" }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentPermissionMode = "workspace-write" }));
            var copilot = settings with { AgentProvider = AgentProfile.GitHubCopilot, AgentExecutable = "copilot", AgentArguments = AgentProfile.DefaultGitHubCopilotArguments };
            await store.SaveAsync(copilot);
            Assert.Equal(copilot, await store.LoadAsync());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void ChatRequestPolicyRequiresUserAndConfiguredScopes()
    {
        var settings = new BridgeSettings
        {
            SlackAllowedUserIds = "U1", SlackAllowedChannelIds = "C1", SlackAllowedThreadIds = "123.456",
            MattermostAllowedUserIds = "u1", MattermostAllowedThreadIds = "thread-1"
        };
        Assert.True(ChatAccessPolicy.IsAllowedRequest(settings, "slack", "U1", "C1", "123.456"));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "slack", "U1", "C2", "123.456"));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "slack", "U2", "C1", "123.456"));
        Assert.True(ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", "u1", "channel", "thread-1"));
        Assert.False(ChatAccessPolicy.IsAllowedRequest(settings, "mattermost", "u1", "channel", "thread-2"));
    }
}
