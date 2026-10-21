using ChatAgentBridge.Core;
using System.Globalization;

namespace ChatAgentBridge.Core.Tests;

public sealed class WorkspaceValidationTests
{
    [Fact]
    public void ValidateWorkspaceAcceptsTrustedRootAndChildAndReturnsCanonicalPaths()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "trusted")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var settings = Settings(child) with { TrustedWorkspaceRoots = root };

        Assert.Equal(Path.GetFullPath(child), CliProbeService.ValidateWorkspace(settings));
        Assert.Equal(Path.GetFullPath(root), CliProbeService.ValidateWorkspace(settings with { WorkspaceRoot = root }));
    }

    [Fact]
    public void ValidateWorkspaceRejectsExplicitOutsideRootAndAllowsItAfterExplicitTrust()
    {
        using var temp = new TempDirectory();
        var trusted = Directory.CreateDirectory(Path.Combine(temp.Path, "trusted")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        var settings = Settings(outside) with { TrustedWorkspaceRoots = trusted };

        var error = Assert.Throws<CliWorkspaceTrustException>(() => CliProbeService.ValidateWorkspace(settings));
        Assert.Equal(Path.GetFullPath(outside), error.WorkspacePath);
        Assert.Null(error.SuggestedWorkspacePath);

        Assert.Equal(Path.GetFullPath(outside), CliProbeService.ValidateWorkspace(settings with { TrustedWorkspaceRoots = outside }));
    }

    [Fact]
    public void ValidateWorkspacePreservesRequiredRelativeMissingAndInvalidPathErrors()
    {
        using var temp = new TempDirectory();
        var baseline = Settings(temp.Path);
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            Assert.Contains("workspace", Assert.Throws<InvalidOperationException>(() =>
                CliProbeService.ValidateWorkspace(baseline with { WorkspaceRoot = " " })).Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("relative", Assert.Throws<InvalidOperationException>(() =>
                CliProbeService.ValidateWorkspace(baseline with { WorkspaceRoot = "relative" })).Message, StringComparison.Ordinal);

            var missing = Path.Combine(temp.Path, "missing");
            Assert.Contains(missing, Assert.Throws<InvalidOperationException>(() =>
                CliProbeService.ValidateWorkspace(baseline with { WorkspaceRoot = missing })).Message, StringComparison.Ordinal);

            var invalid = temp.Path + "\0invalid";
            Assert.Contains("invalid", Assert.Throws<InvalidOperationException>(() =>
                CliProbeService.ValidateWorkspace(baseline with { WorkspaceRoot = invalid })).Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { CultureInfo.CurrentUICulture = previousCulture; }
    }

    [Fact]
    public void ValidateWorkspaceUsesConfiguredWorkspaceWhenTrustedRootsAreEmpty()
    {
        using var temp = new TempDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "workspace")).FullName;

        Assert.Equal(Path.GetFullPath(workspace), CliProbeService.ValidateWorkspace(Settings(workspace)));
    }

    [Fact]
    public void ValidateWorkspaceRejectsSymlinkEscapeWhenPlatformAllowsCreatingLink()
    {
        using var temp = new TempDirectory();
        var trusted = Directory.CreateDirectory(Path.Combine(temp.Path, "trusted")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        var linked = Path.Combine(trusted, "linked");
        try { Directory.CreateSymbolicLink(linked, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var error = Assert.Throws<CliWorkspaceTrustException>(() =>
            CliProbeService.ValidateWorkspace(Settings(linked) with { TrustedWorkspaceRoots = trusted }));
        Assert.Equal(Path.GetFullPath(linked), error.WorkspacePath);
        Assert.Equal(Path.GetFullPath(outside), error.SuggestedWorkspacePath);
    }

    private static BridgeSettings Settings(string workspace) => new() { WorkspaceRoot = workspace };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
