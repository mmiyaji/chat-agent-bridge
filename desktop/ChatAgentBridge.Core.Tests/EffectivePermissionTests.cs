namespace ChatAgentBridge.Core.Tests;

public sealed class EffectivePermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-effperm-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private string Workspace => Path.Combine(_root, "work");

    public EffectivePermissionTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Workspace);
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    private BridgeSettings Settings(string provider, string mode = "inherit") =>
        new() { AgentProvider = provider, AgentPermissionMode = mode, WorkspaceRoot = Workspace };

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private EffectivePermissionInfo Resolve(BridgeSettings settings, Dictionary<string, string>? env = null) =>
        EffectivePermission.Resolve(settings, Home, name => env is not null && env.TryGetValue(name, out var value) ? value : null);

    [Theory]
    [InlineData("read-only", "plan", false)]
    [InlineData("workspace-write", "acceptEdits", false)]
    [InlineData("full-access", "bypassPermissions", true)]
    public void ClaudeExplicitModeIgnoresSettingsFiles(string mode, string native, bool unattended)
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"permissions":{"defaultMode":"auto"}}""");
        var result = Resolve(Settings("claude", mode));
        Assert.Equal(new EffectivePermissionInfo(mode, native, PermissionSource.BridgeSetting, unattended), result);
    }

    [Fact]
    public void CodexExplicitFullAccessIsUnattended()
    {
        var result = Resolve(Settings("codex", "full-access"));
        Assert.Equal("danger-full-access", result.NativeMode);
        Assert.True(result.Unattended);
    }

    [Fact]
    public void ClaudeInheritWithoutAnyFileUsesCliDefault()
    {
        var result = Resolve(Settings("claude"));
        Assert.Equal(new EffectivePermissionInfo("inherit", "default", PermissionSource.CliDefault, false), result);
    }

    [Fact]
    public void ClaudeInheritReadsUserSettingsAndFlagsAuto()
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"permissions":{"defaultMode":"auto"}}""");
        var result = Resolve(Settings("claude"));
        Assert.Equal(new EffectivePermissionInfo("inherit", "auto", PermissionSource.UserSettings, true), result);
    }

    [Fact]
    public void ClaudeInheritPrecedenceIsLocalThenProjectThenUser()
    {
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"permissions":{"defaultMode":"bypassPermissions"}}""");
        Write(Path.Combine(Workspace, ".claude", "settings.json"), """{"permissions":{"defaultMode":"acceptEdits"}}""");
        Assert.Equal(new("inherit", "acceptEdits", PermissionSource.ProjectSettings, false), Resolve(Settings("claude")));
        Write(Path.Combine(Workspace, ".claude", "settings.local.json"), """{"permissions":{"defaultMode":"dontAsk"}}""");
        Assert.Equal(new("inherit", "dontAsk", PermissionSource.ProjectLocalSettings, true), Resolve(Settings("claude")));
    }

    [Fact]
    public void ClaudeInheritSkipsFilesWithoutDefaultModeAndInvalidJson()
    {
        Write(Path.Combine(Workspace, ".claude", "settings.local.json"), "{ not json");
        Write(Path.Combine(Workspace, ".claude", "settings.json"), """{"permissions":{"allow":["Bash(git status)"]}}""");
        Write(Path.Combine(Home, ".claude", "settings.json"), "{ // comment\n \"permissions\": { \"defaultMode\": \"plan\", }, }");
        Assert.Equal(new("inherit", "plan", PermissionSource.UserSettings, false), Resolve(Settings("claude")));
    }

    [Fact]
    public void ClaudeConfigDirReplacesDefaultUserDirectory()
    {
        var custom = Path.Combine(_root, "custom-claude");
        Write(Path.Combine(Home, ".claude", "settings.json"), """{"permissions":{"defaultMode":"plan"}}""");
        Write(Path.Combine(custom, "settings.json"), """{"permissions":{"defaultMode":"bypassPermissions"}}""");
        var result = Resolve(Settings("claude"), new() { ["CLAUDE_CONFIG_DIR"] = custom });
        Assert.Equal(new("inherit", "bypassPermissions", PermissionSource.UserSettings, true), result);
    }

    [Fact]
    public void CodexInheritReadsTopLevelSandboxModeOnly()
    {
        Write(Path.Combine(Home, ".codex", "config.toml"),
            "# comment\nmodel = \"gpt\"\nsandbox_mode = \"danger-full-access\" # note\n\n[profiles.x]\nsandbox_mode = \"read-only\"\n");
        Assert.Equal(new("inherit", "danger-full-access", PermissionSource.UserSettings, true), Resolve(Settings("codex")));
    }

    [Fact]
    public void CodexInheritIgnoresKeysAfterFirstTableAndMissingFile()
    {
        Assert.Equal(new("inherit", null, PermissionSource.Unknown, false), Resolve(Settings("codex")));
        Write(Path.Combine(Home, ".codex", "config.toml"), "[projects.a]\nsandbox_mode = \"danger-full-access\"\n");
        Assert.Equal(new("inherit", null, PermissionSource.Unknown, false), Resolve(Settings("codex")));
    }

    [Fact]
    public void CodexHomeEnvironmentOverridesDefaultDirectory()
    {
        var custom = Path.Combine(_root, "codex-home");
        Write(Path.Combine(Home, ".codex", "config.toml"), "sandbox_mode = \"read-only\"\n");
        Write(Path.Combine(custom, "config.toml"), "sandbox_mode = 'workspace-write'\n");
        Assert.Equal(new("inherit", "workspace-write", PermissionSource.UserSettings, false),
            Resolve(Settings("codex"), new() { ["CODEX_HOME"] = custom }));
    }

    [Fact]
    public void OtherProvidersInheritAreUnknownButCabFullAccessIsStillUnattended()
    {
        Assert.Equal(new("inherit", null, PermissionSource.Unknown, false), Resolve(Settings("gemini")));
        var full = Resolve(Settings("gemini", "full-access"));
        Assert.True(full.Unattended);
        Assert.Null(full.NativeMode);
    }
}
