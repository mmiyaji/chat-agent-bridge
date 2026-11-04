using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConfiguredWorkspaceTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-configured-trust-" + Guid.NewGuid().ToString("N"));
    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;

    [Fact]
    public void SelectedWorkspaceBecomesTrustedAndOtherRootsArePreserved()
    {
        var original = Folder("original");
        var selected = Folder("selected");
        var roots = ExecutionPolicy.IncludeConfiguredWorkspace(original, selected);
        var settings = new BridgeSettings { WorkspaceRoot = selected, TrustedWorkspaceRoots = roots };
        Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings, selected));
        Assert.True(ExecutionPolicy.IsWorkspaceTrusted(settings, original));
        Assert.Equal(roots, ExecutionPolicy.IncludeConfiguredWorkspace(roots, selected));
        Assert.True(SettingsSensitiveChanges.Compare(settings with { TrustedWorkspaceRoots = original }, settings).TrustedWorkspaces);
    }

    [Fact]
    public void TrustedParentCoversChildWithoutDuplicateEntryAndRejectsPrefixSibling()
    {
        var parent = Folder("projects");
        var child = Folder(Path.Combine("projects", "child"));
        var sibling = Folder("projects-other");
        var roots = ExecutionPolicy.IncludeConfiguredWorkspace(parent, child);
        Assert.Equal(parent, roots);
        Assert.True(ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = roots }, child));
        Assert.False(ExecutionPolicy.IsWorkspaceTrusted(new BridgeSettings { TrustedWorkspaceRoots = roots }, sibling));
    }

    [Fact]
    public void EmptyListIncludesSelectedFolderButInvalidWorkspaceDoesNotBroadenTrust()
    {
        var selected = Folder("selected");
        Assert.Equal(selected, ExecutionPolicy.IncludeConfiguredWorkspace("", selected));
        foreach (var invalid in new[] { "", "relative", Path.Combine(_root, "missing") })
            Assert.Equal(selected, ExecutionPolicy.IncludeConfiguredWorkspace(selected, invalid));
    }

    [Fact]
    public async Task SavedTrustedParentAllowsConversationSwitchAfterRestartButNotOutsideOrRevokedScope()
    {
        var parent = Folder("projects");
        var child = Folder(Path.Combine("projects", "child"));
        var outside = Folder("outside");
        var settings = new BridgeSettings { WorkspaceRoot = parent,
            TrustedWorkspaceRoots = ExecutionPolicy.IncludeConfiguredWorkspace("", parent) };
        var data = Folder("data");
        var settingsStore = new SettingsStore(data);
        await settingsStore.SaveAsync(settings);
        var reloaded = await new SettingsStore(data).LoadAsync();
        Assert.Equal(parent, reloaded.TrustedWorkspaceRoots);
        var key = new SessionKey("mattermost", "channel", "thread", "alice", new string('a', 64));
        var sessionStore = new SessionStore(data);
        await sessionStore.ChangeWorkspaceAsync(key, child, reloaded);
        var restarted = new SessionStore(data);
        Assert.Equal(child, await restarted.FindWorkspaceAsync(key));
        Assert.Equal(child, (await restarted.ApplyWorkspaceAsync(key, reloaded)).WorkspaceRoot);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.ChangeWorkspaceAsync(key, outside, reloaded));
        Assert.Equal(child, await restarted.FindWorkspaceAsync(key));
        Assert.Null(await restarted.FindWorkspaceAsync(key with { UserId = "bob" }));
        var revoked = reloaded with { TrustedWorkspaceRoots = outside };
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.ChangeWorkspaceAsync(key, child, revoked));
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
