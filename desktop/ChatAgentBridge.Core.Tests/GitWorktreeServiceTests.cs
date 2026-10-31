using System.Diagnostics;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class GitWorktreeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-wt-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Theory]
    [InlineData("feature-1", true)]
    [InlineData("a.b_c", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData(".x", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a..b", false)]
    [InlineData("x.lock", false)]
    [InlineData("\u65E5\u672C\u8A9E", false)]
    public void ValidatesNames(string name, bool expected) => Assert.Equal(expected, GitWorktreeService.IsValidName(name));

    [Fact]
    public void RejectsOverlongName() => Assert.False(GitWorktreeService.IsValidName(new string('a', 41)));

    [Fact]
    public void ParsesWorktreeCommand()
    {
        Assert.True(WorkspaceChatCommand.TryParseWorktree("worktree feat-1", out var name));
        Assert.Equal("feat-1", name);
        Assert.True(WorkspaceChatCommand.TryParseWorktree("/Worktree \"x\"", out name));
        Assert.Equal("x", name);
        Assert.True(WorkspaceChatCommand.TryParseWorktree("worktree", out name));
        Assert.Equal("", name);
        Assert.False(WorkspaceChatCommand.TryParseWorktree("worktrees foo", out _));
        Assert.False(WorkspaceChatCommand.TryParseWorktree("folder C:\\x", out _));
        Assert.True(WorkspaceChatCommand.IsWorkspaceCommand("worktree a"));
        Assert.True(WorkspaceChatCommand.IsWorkspaceCommand("folder C:\\x"));
        Assert.True(ChatFollowUpPolicy.IsConfigurationCommand("worktree a"));
    }

    [Fact]
    public async Task CreatesReusesAndExcludesWorktree()
    {
        if (!GitAvailable()) return;
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Git(repo, "init", "-q");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init");
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src")).FullName;

        var (plan, reused) = await GitWorktreeService.CreateAsync(sub, "feat-1");
        Assert.False(reused);
        Assert.Equal(Path.Combine(Path.GetFullPath(repo), ".cab", "worktrees", "feat-1"), plan.Path, ignoreCase: true);
        Assert.Equal("cab/feat-1", plan.Branch);
        Assert.True(Directory.Exists(plan.Path));
        Assert.Contains("cab/feat-1", Git(repo, "branch", "--list", "cab/feat-1"));
        var excludePath = Path.Combine(repo, ".git", "info", "exclude");
        Assert.Single((await File.ReadAllTextAsync(excludePath)).Split('\n'), line => line.Trim() == ".cab/");

        var (again, reusedAgain) = await GitWorktreeService.CreateAsync(sub, "feat-1");
        Assert.True(reusedAgain);
        Assert.Equal(plan.Path, again.Path);
        Assert.Single((await File.ReadAllTextAsync(excludePath)).Split('\n'), line => line.Trim() == ".cab/");
        Assert.DoesNotContain(".cab", Git(repo, "status", "--porcelain"));
    }

    [Fact]
    public async Task ReusesExistingBranchWhenWorktreeIsMissing()
    {
        if (!GitAvailable()) return;
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Git(repo, "init", "-q");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init");
        Git(repo, "branch", "cab/existing");

        var (plan, reused) = await GitWorktreeService.CreateAsync(repo, "existing");

        Assert.False(reused);
        Assert.True(Directory.Exists(plan.Path));
        Assert.Contains("cab/existing", Git(plan.Path, "branch", "--show-current"));
    }

    [Fact]
    public async Task RejectsNonRepositoryAndBadName()
    {
        if (!GitAvailable()) return;
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorktreeService.PlanAsync(plain, "ok"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorktreeService.PlanAsync(plain, "-bad"));
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
