using ChatAgentBridge.Core;


namespace ChatAgentBridge.Core.Tests;

public sealed class QwenStandaloneResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "qwen-resolver-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private string Setup(bool inner)
    {
        Write("qwen-code/manifest.json", "{\"name\":\"@qwen-code/qwen-code\",\"runtime\":\"node\",\"target\":\"win-x64\"}");
        Write("qwen-code/package.json", "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"scripts/cli-entry.js\"}}");
        Write("qwen-code/lib/package.json", "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"cli-entry.js\"}}");
        Write("qwen-code/lib/cli-entry.js");
        Write("qwen-code/node/node.exe");
        var command = Write("qwen-code/bin/qwen.cmd", "arbitrary shim text must never run");
        return inner ? command : Write("bin/qwen.cmd", "arbitrary shim text must never run");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvesOfficialArchiveAndWrapper(bool inner)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = NodeCliPackageResolver.Resolve(Setup(inner), AgentProfile.Qwen);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "qwen-code/node/node.exe")), result!.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "qwen-code/lib/cli-entry.js")), result.EntryPoint);
    }
    [Theory]
    [InlineData("name")]
    [InlineData("runtime")]
    [InlineData("traversal")]
    [InlineData("npm")]
    public void RejectsInvalidOrConflictingLayout(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Setup(false);
        if (failure == "name") Write("qwen-code/lib/package.json", "{\"name\":\"other\"}");
        if (failure == "runtime") File.Delete(Path.Combine(root, "qwen-code/node/node.exe"));
        if (failure == "traversal") Write("qwen-code/lib/package.json", "{\"name\":\"@qwen-code/qwen-code\",\"bin\":{\"qwen\":\"../../outside.js\"}}");
        if (failure == "npm") Write("bin/node_modules/@qwen-code/qwen-code/package.json", "{}");
        Assert.Throws<InvalidOperationException>(() => NodeCliPackageResolver.Resolve(shim, AgentProfile.Qwen));
    }
}
