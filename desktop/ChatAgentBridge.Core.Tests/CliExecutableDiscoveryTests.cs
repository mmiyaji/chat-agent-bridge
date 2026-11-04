using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class CliExecutableDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cli-discovery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Write(string relative, string content = "")
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("gemini", "gemini")]
    [InlineData("github-copilot", "copilot")]
    [InlineData("antigravity", "agy")]
    [InlineData("kiro", "kiro-cli")]
    [InlineData("qwen", "qwen")]
    [InlineData("kimi", "kimi")]
    [InlineData("muse", "muse")]
    public void ReturnsCommandTokenAndNativePath(string provider, string command)
    {
        var executable = Write(command + ".exe");
        var result = CliExecutableDiscovery.Find(provider, root);
        Assert.Equal(provider == AgentProfile.Muse ? executable : command, result!.Executable);
        Assert.Equal(executable, result.DiscoveredPath);
    }
    [Fact]
    public void KeepsPathOrderAndPrefersExeWithinDirectory()
    {
        var first = Write("first/qwen.exe");
        Write("first/qwen.cmd", "this is not executable code");
        Write("second/qwen.exe");
        var path = string.Join(Path.PathSeparator, Path.Combine(root, "first"), Path.Combine(root, "second"));
        Assert.Equal(first, CliExecutableDiscovery.Find("qwen", path)!.DiscoveredPath);
    }
    [Fact]
    public void SkipsRelativeInvalidAndMissingDirectories()
    {
        var executable = Write("qwen.exe");
        var path = string.Join(Path.PathSeparator, ".", "relative", "bad\0path", Path.Combine(root, "missing"), "\"" + root + "\"");
        Assert.Equal(executable, CliExecutableDiscovery.Find("qwen", path)!.DiscoveredPath);
        Assert.Null(CliExecutableDiscovery.Find("qwen", Path.Combine(root, "missing")));
    }
    [Fact]
    public void CustomAndUnknownDoNotInferInstalledProviders()
    {
        Write("qwen.exe");
        Assert.Null(CliExecutableDiscovery.Find("custom", root));
        Assert.Null(CliExecutableDiscovery.Find("unknown", root));
    }
    [Theory]
    [InlineData("qwen")]
    [InlineData("kimi")]
    [InlineData("claude")]
    public void UnsupportedShimIsNotExecuted(string provider)
    {
        Write(provider + ".cmd", "@echo off\r\nexit /b 0\r\n");
        Assert.Null(CliExecutableDiscovery.Find(provider, root));
    }
    [Fact]
    public void MalformedEarlierQwenShimShadowsLaterNativeInstallation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write("first/qwen.cmd", "invalid shell content must never execute");
        Write("later/qwen.exe");
        var searchPath = string.Join(Path.PathSeparator, Path.Combine(root, "first"), Path.Combine(root, "later"));
        Assert.Null(CliExecutableDiscovery.Find("qwen", searchPath));
    }
    [Fact]
    public void AcceptsValidatedNpmShimWithoutRunningIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Write("qwen.cmd", "invalid shell content must never execute");
        Write("node.exe");
        Write("node_modules/@qwen-code/qwen-code/package.json", "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"cli.js\"}}");
        Write("node_modules/@qwen-code/qwen-code/cli.js");
        Write("later/qwen.exe");
        var searchPath = string.Join(Path.PathSeparator, root, Path.Combine(root, "later"));
        var result = CliExecutableDiscovery.Find("qwen", searchPath);
        Assert.Equal("qwen", result!.Executable);
        Assert.Equal(shim, result.DiscoveredPath);
    }
}
