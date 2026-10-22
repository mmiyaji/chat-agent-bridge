using System.Text;
using System.Text.Json.Nodes;
using ChatAgentBridge.Core.Maintenance;

namespace ChatAgentBridge.Core.Tests;

public sealed class SettingsBackupTests
{
    private static BridgeSettings Source => new()
    {
        AgentProvider = AgentProfile.Claude, AgentExecutable = "claude", AgentArguments = "--secret sentinel-cli-secret",
        WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()), MattermostServerUrl = "https://name:password@example.test/chat?token=secret-query#private",
        MattermostToken = "sentinel-mm-secret", SlackBotToken = "sentinel-bot-secret", SlackAppToken = "sentinel-app-secret",
        AgentPermissionMode = "full-access", RequireApproval = false, ChatApprovalMode = "auto", ChatResultMode = "auto",
        TrustedWorkspaceRoots = Path.GetFullPath(Path.GetTempPath()), SlackAllowedUserIds = "U12345678", AutoConnectProvider = "slack",
        FileAttachmentApprovalMode = "auto", AllowAgentNetwork = true
    };

    [Fact]
    public void ExportUsesAnAllowlistAndStripsServerCredentials()
    {
        var bytes = SettingsBackup.Create(Source).Serialize();
        var json = Encoding.UTF8.GetString(bytes);
        foreach (var sensitive in new[] { "sentinel", "password", "secret-query", "private", "U12345678", "full-access", "MattermostToken", "AgentArguments" })
            Assert.DoesNotContain(sensitive, json);
        Assert.Equal("https://example.test/chat", SettingsBackup.Parse(bytes).MattermostServer);
    }

    [Fact]
    public void RestoreDoesNotReauthorizeAccountsOrImplicitlyTrustAWorkspace()
    {
        var restored = SettingsBackup.Parse(SettingsBackup.Create(Source).Serialize()).ToSettings(false);
        Assert.Empty(restored.WorkspaceRoot);
        Assert.Empty(restored.TrustedWorkspaceRoots);
        Assert.Empty(restored.MattermostToken); Assert.Empty(restored.SlackBotToken); Assert.Empty(restored.SlackAppToken);
        Assert.Empty(restored.SlackAllowedUserIds); Assert.Empty(restored.MattermostAllowedUserIds);
        Assert.Equal("none", restored.AutoConnectProvider);
        Assert.Equal("always", restored.ChatApprovalMode);
        Assert.Equal("manual", restored.ChatResultMode);
        Assert.Equal("always", restored.FileAttachmentApprovalMode);
        Assert.Equal("inherit", restored.AgentPermissionMode);
        Assert.True(restored.RequireApproval);
        Assert.False(restored.AllowAgentNetwork);
        Assert.False(ExecutionPolicy.IsWorkspaceTrusted(restored, Source.WorkspaceRoot));
    }

    [Fact]
    public void ExplicitWorkspaceConsentRestoresOnlyThatRoot()
    {
        var restored = SettingsBackup.Create(Source).ToSettings(true);
        Assert.Equal(Source.WorkspaceRoot, restored.WorkspaceRoot);
        Assert.Equal(Source.WorkspaceRoot, restored.TrustedWorkspaceRoots);
        Assert.Empty(restored.AgentArguments);
        Assert.Equal("always", restored.ChatApprovalMode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"Workspace\":\"relative\"}")]
    [InlineData("{\"Provider\":\"unknown\"}")]
    [InlineData("{\"MattermostToken\":\"do-not-import\"}")]
    [InlineData("{\"Executable\":null}")]
    public void InvalidOrUnexpectedBackupFieldsAreRejected(string json)
    {
        if (json == "{}")
        {
            Assert.Throws<InvalidDataException>(() => SettingsBackup.Parse(Encoding.UTF8.GetBytes(json)));
            return;
        }
        var valid = JsonNode.Parse(SettingsBackup.Create(Source).Serialize())!.AsObject();
        foreach (var pair in JsonNode.Parse(json)!.AsObject()) valid[pair.Key] = pair.Value?.DeepClone();
        Assert.ThrowsAny<Exception>(() => SettingsBackup.Parse(Encoding.UTF8.GetBytes(valid.ToJsonString())));
    }

    [Fact]
    public void OversizedBackupIsRejected() => Assert.Throws<InvalidDataException>(() => SettingsBackup.Parse(new byte[65537]));
}
