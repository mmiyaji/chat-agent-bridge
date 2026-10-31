using System.Diagnostics;
using System.Text;

namespace ChatAgentBridge.Core;

/// <summary>Where a conversation worktree lives and which branch backs it.</summary>
public sealed record GitWorktreePlan(string RepositoryRoot, string CommonGitDirectory, string Path, string Branch, string Name);

/// <summary>Creates per-conversation git worktrees. Runs git directly (no shell) with a bounded timeout.</summary>
public static class GitWorktreeService
{
    public const int MaximumNameLength = 40;
    public static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || name[0] is '-' or '.') return false;
        foreach (var character in name)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        // "x.lock" and "x." are not valid git ref components; a ".." sequence is never valid.
        return !name.EndsWith('.') && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>Validates the name and resolves the repository for <paramref name="workspace"/> without changing anything.</summary>
    public static async Task<GitWorktreePlan> PlanAsync(string workspace, string name, CancellationToken cancellationToken = default,
        string gitExecutable = "git")
    {
        if (!IsValidName(name)) throw new InvalidOperationException("Invalid worktree name.");
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            throw new InvalidOperationException("The workspace folder does not exist.");
        var top = (await RunAsync(gitExecutable, workspace, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false))
            .RequireOutput("The workspace is not inside a git repository.");
        var topPath = Normalize(top);
        var common = (await RunAsync(gitExecutable, workspace, ["rev-parse", "--git-common-dir"], cancellationToken).ConfigureAwait(false))
            .RequireOutput("The git directory could not be resolved.");
        var commonPath = Normalize(System.IO.Path.GetFullPath(common, workspace));
        var path = Normalize(System.IO.Path.Combine(topPath, ".cab", "worktrees", name));
        return new GitWorktreePlan(topPath, commonPath, path, "cab/" + name, name);
    }

    /// <summary>Creates the worktree (or reuses a registered one) and excludes <c>.cab/</c> from the repository.</summary>
    public static async Task<bool> EnsureAsync(GitWorktreePlan plan, CancellationToken cancellationToken = default,
        string gitExecutable = "git")
    {
        ArgumentNullException.ThrowIfNull(plan);
        await EnsureExcludedAsync(plan.CommonGitDirectory, cancellationToken).ConfigureAwait(false);
        var list = await RunAsync(gitExecutable, plan.RepositoryRoot, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        list.RequireSuccess();
        foreach (var line in list.Output.Split('\n'))
        {
            if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
            if (PathsEqual(Normalize(line["worktree ".Length..].Trim()), plan.Path)) return true;
        }
        if (Directory.Exists(plan.Path) || File.Exists(plan.Path))
            throw new InvalidOperationException("The worktree folder already exists but is not a registered git worktree.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(plan.Path)!);
        var branch = await RunAsync(gitExecutable, plan.RepositoryRoot,
            ["rev-parse", "--verify", "--quiet", "refs/heads/" + plan.Branch], cancellationToken).ConfigureAwait(false);
        string[] arguments = branch.ExitCode == 0
            ? ["worktree", "add", plan.Path, plan.Branch]
            : ["worktree", "add", "-b", plan.Branch, plan.Path];
        (await RunAsync(gitExecutable, plan.RepositoryRoot, arguments, cancellationToken).ConfigureAwait(false)).RequireSuccess();
        return false;
    }

    /// <summary>Plans and creates in one call. Returns the plan and whether an existing worktree was reused.</summary>
    public static async Task<(GitWorktreePlan Plan, bool Reused)> CreateAsync(string workspace, string name,
        CancellationToken cancellationToken = default, string gitExecutable = "git")
    {
        var plan = await PlanAsync(workspace, name, cancellationToken, gitExecutable).ConfigureAwait(false);
        return (plan, await EnsureAsync(plan, cancellationToken, gitExecutable).ConfigureAwait(false));
    }

    private static async Task EnsureExcludedAsync(string commonGitDirectory, CancellationToken cancellationToken)
    {
        var infoDirectory = System.IO.Path.Combine(commonGitDirectory, "info");
        var file = System.IO.Path.Combine(infoDirectory, "exclude");
        var existing = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : "";
        if (existing.Split('\n').Any(line => line.Trim() is ".cab/" or ".cab" or "/.cab/" or "/.cab")) return;
        Directory.CreateDirectory(infoDirectory);
        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? "" : Environment.NewLine;
        await File.AppendAllTextAsync(file, prefix + ".cab/" + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));

    private static bool PathsEqual(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record GitResult(int ExitCode, string Output, string Error)
    {
        public GitResult RequireSuccess()
        {
            if (ExitCode != 0)
                throw new InvalidOperationException("git failed: " + (Error.Length > 0 ? Error : Output));
            return this;
        }

        public string RequireOutput(string message)
        {
            if (ExitCode != 0 || Output.Length == 0) throw new InvalidOperationException(message);
            return Output;
        }
    }

    private static async Task<GitResult> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("git could not be started.", ex);
        }
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new GitResult(process.ExitCode, (await output.ConfigureAwait(false)).Trim(),
                (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("git timed out.");
        }
    }
}
