using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class MuseExecTests : IDisposable
{
    private const string Id = "019e0000-0000-7000-8000-000000000123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-muse-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "cab-muse-fixture-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void InvocationKeepsPromptOutOfArgumentsAndUsesSessionId()
    {
        var settings = Settings("fixture-model") with { AuthorizedCliSessionId = Id };
        var invocation = AgentProfile.BuildInvocation(settings);
        Assert.Equal("muse", invocation.Executable);
        Assert.Contains("exec", invocation.Arguments);
        Assert.Contains("--json", invocation.Arguments);
        Assert.Contains("--session-id", invocation.Arguments);
        Assert.DoesNotContain("fixture-model secret prompt", string.Join(" ", invocation.Arguments));
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(settings with { AuthorizedCliSessionId = "latest" }));
    }

    [Fact]
    public void ParserAcceptsOnlyExpectedRootCompletionAndSafeUsage()
    {
        var parser = new MuseOutputParser(Id);
        Assert.True(parser.AddLine(Event("session", Id, "run.lifecycle.started", new { kind = "run_started", prompt = "private user payload" })));
        Assert.True(parser.AddLine(Event("exec.usage", Id, "run.usage", new { kind = "run_usage", models = new[] { new { model = "muse-spark-1.3", input_tokens = 17, cached_tokens = 5, output_tokens = 8, reasoning_tokens = 999 } } })));
        Assert.True(parser.AddLine(Event("session", "019e0000-0000-7000-8000-000000000999", "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "foreign text" })));
        Assert.True(parser.AddLine(Event("session", Id, "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "final only" })));
        var result = parser.Complete(processSucceeded: true);
        Assert.True(result.HasResult);
        Assert.False(result.TurnFailed);
        Assert.Equal(Id, result.SessionId);
        Assert.Equal("final only", result.FinalMessage);
        Assert.DoesNotContain("private user payload", result.FinalMessage);
        Assert.DoesNotContain("foreign text", result.FinalMessage);
        Assert.Equal(17, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage.CachedInputTokens);
        Assert.Equal(8, result.Usage.OutputTokens);
        Assert.Equal("muse-spark-1.3", result.Usage.Model);
        Assert.Equal("muse.exec-jsonl", result.Usage.Source);
    }

    [Theory]
    [InlineData("run.terminal.failed", "failed")]
    [InlineData("run.terminal.cancelled", "cancelled")]
    public void ParserRejectsFailedTerminalAndExitStatus(string payloadType, string terminal)
    {
        var parser = new MuseOutputParser(Id);
        Assert.True(parser.AddLine(Event("session", Id, payloadType, new { kind = "run_terminal", terminal, text = "must not escape" })));
        Assert.False(parser.Complete(processSucceeded: true).HasResult);
        var completed = new MuseOutputParser(Id);
        completed.AddLine(Event("session", Id, "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "not enough" }));
        Assert.False(completed.Complete(processSucceeded: false).HasResult);
    }

    [Fact]
    public void ParserFailsClosedForForeignOnlyMalformedAndUnsafeUsage()
    {
        var foreign = new MuseOutputParser(Id);
        Assert.True(foreign.AddLine(Event("session", "019e0000-0000-7000-8000-000000000999", "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "foreign" })));
        Assert.False(foreign.Complete(processSucceeded: true).HasResult);
        Assert.True(foreign.SessionMismatch);

        var malformed = new MuseOutputParser(Id);
        Assert.False(malformed.AddLine("{broken"));
        Assert.False(malformed.Complete(processSucceeded: true).HasResult);

        var negative = new MuseOutputParser(Id);
        Assert.False(negative.AddLine(Event("exec.usage", Id, "run.usage", new { kind = "run_usage", models = new[] { new { input_tokens = -1L, cached_tokens = 0L, output_tokens = 0L } } })));
        var overflow = new MuseOutputParser(Id);
        Assert.False(overflow.AddLine(Event("exec.usage", Id, "run.usage", new { kind = "run_usage", models = new[] { new { input_tokens = long.MaxValue, cached_tokens = 0L, output_tokens = 0L }, new { input_tokens = 1L, cached_tokens = 0L, output_tokens = 0L } } })));
    }

    [Fact]
    public void ParserRejectsPendingApprovalAndDoesNotExposeToolPayloads()
    {
        var parser = new MuseOutputParser(Id);
        Assert.True(parser.AddLine(Event("session", Id, "approval_wait.effect.started", new { kind = "approval_wait", command = "private tool payload" })));
        Assert.True(parser.ApprovalPending);
        Assert.False(parser.Complete(processSucceeded: true).HasResult);
        Assert.Contains("承認", parser.Complete(processSucceeded: true).FailureGuidance);

        var unrelated = new MuseOutputParser(Id);
        Assert.True(unrelated.AddLine(Event("session", Id, "approval_wait.effect.completed", new { kind = "approval_wait", state = "approved" })));
        Assert.False(unrelated.ApprovalPending);
        var autoApproved = new MuseOutputParser(Id);
        Assert.True(autoApproved.AddLine(Event("session", Id, "tool_approval.effect.started", new { kind = "tool_approval", state = "auto_approved" })));
        Assert.False(autoApproved.ApprovalPending);
    }

    [Fact]
    public void ParserFlagsSessionMismatchWithoutRetainingForeignSessionId()
    {
        var parser = new MuseOutputParser(Id);
        Assert.True(parser.AddLine(Event("session", "019e0000-0000-7000-8000-000000000999", "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "foreign" })));
        var result = parser.Complete(processSucceeded: true);
        Assert.True(parser.SessionMismatch);
        Assert.False(result.HasResult);
        Assert.Null(result.SessionId);
        Assert.Null(result.FinalMessage);
    }

    [Fact]
    public void ParserStreamsLargeUnknownEventsWithoutRetainingTheirPayloads()
    {
        var parser = new MuseOutputParser(Id);
        var noise = new string('x', 60_000);
        for (var index = 0; index < 50; index++)
            Assert.True(parser.AddLine(Event("task", Guid.CreateVersion7().ToString("D"), "task.progress", new { kind = "task_progress", detail = noise })));
        Assert.True(parser.AddLine(Event("session", Id, "run.terminal.completed", new { kind = "run_terminal", terminal = "completed", text = "done" })));
        var result = parser.Complete(processSucceeded: true);
        Assert.True(result.HasResult);
        Assert.Equal("done", result.FinalMessage);
    }

    [Fact]
    public async Task RunnerUsesPrivatePromptFileAndSessionAgentRunnerResumesWithModelChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        PrepareFixture();
        var settings = Settings("fast") with { AgentExecutable = Path.Combine(_fixture, "muse-fixture.exe") };
        var store = new SessionStore(_root);
        var runner = new SessionAgentRunner(store, new AgentRunner());
        var key = new SessionKey("slack", "channel", "thread", "owner", "slack");
        var prompt = "secret prompt 日本語\nline";
        var promptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAB-MusePrompts");
        var before = Directory.Exists(promptDirectory) ? Directory.GetDirectories(promptDirectory, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

        var first = await runner.RunAsync(key, settings, prompt);
        Assert.True(first.Success, $"{first.Error} | exit={first.ExitCode}, session={first.CliSession?.SessionId}, has={first.CliSession?.HasResult}, failed={first.CliSession?.TurnFailed}, output={first.Output}");
        Assert.Contains("muse-fixture:fast:secret prompt 日本語", first.Output);
        var binding = await store.FindAsync(key);
        Assert.NotNull(binding);
        Assert.True(Guid.TryParse(binding.SessionId, out _));
        Assert.DoesNotContain(prompt, string.Join(" ", AgentProfile.BuildInvocation(settings).Arguments));
        Assert.True(first.Usage is { InputTokens: 11, CachedInputTokens: 3, OutputTokens: 7 });

        await store.UpdateModelAsync(key, "deep");
        var second = await runner.RunAsync(key, settings with { AgentModel = "fast" }, "follow-up");
        Assert.True(second.Success, second.Error);
        Assert.Equal(binding.SessionId, second.CliSession!.SessionId);
        Assert.Contains("muse-fixture:deep:follow-up", second.Output);

        var parallelWorkspace = Directory.CreateDirectory(Path.Combine(_root, "parallel")).FullName;
        var parallelSettings = settings with { WorkspaceRoot = parallelWorkspace, TrustedWorkspaceRoots = _root };
        var independentRunner = new AgentRunner();
        var activeRun = independentRunner.RunAsync(parallelSettings, "__CAB_MUSE_HOLD__");
        await Task.Delay(100);
        var cleanupTrigger = await independentRunner.RunAsync(settings, "cleanup trigger");
        Assert.True(cleanupTrigger.Success, cleanupTrigger.Error);
        var activeResult = await activeRun;
        Assert.True(activeResult.Success, activeResult.Error);

        var after = Directory.Exists(promptDirectory) ? Directory.GetDirectories(promptDirectory, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        Assert.Empty(after.Except(before));
        Assert.False(Directory.EnumerateFiles(_root, "prompt.txt", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task RunnerRejectsFailedMalformedForeignAndApprovalResultsAndKillsOnCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        PrepareFixture();
        var settings = Settings(null) with { AgentExecutable = Path.Combine(_fixture, "muse-fixture.exe") };
        var runner = new AgentRunner();
        var promptDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAB-MusePrompts");
        var before = Directory.Exists(promptDirectory) ? Directory.GetDirectories(promptDirectory, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        foreach (var prompt in new[] { "__CAB_MUSE_FAIL__", "__CAB_MUSE_INVALID_JSON__", "__CAB_MUSE_FOREIGN_ONLY__", "__CAB_MUSE_APPROVAL__", "__CAB_MUSE_USAGE_INVALID__", "__CAB_MUSE_MISSING_TERMINAL__", "__CAB_MUSE_NONZERO__", "__CAB_MUSE_STDERR_SECRET__" })
        {
            var result = await runner.RunAsync(settings, prompt);
            Assert.False(result.Success);
            Assert.NotNull(result.CliSession);
            Assert.True(result.CliSession!.TurnFailed);
            Assert.DoesNotContain(prompt, result.Error);
            Assert.DoesNotContain("private credential", result.Error, StringComparison.OrdinalIgnoreCase);
            if (prompt is "__CAB_MUSE_INVALID_JSON__" or "__CAB_MUSE_USAGE_INVALID__")
                Assert.Equal(CoreText.Get("MuseProtocolInvalid"), result.Error);
            if (prompt == "__CAB_MUSE_MISSING_TERMINAL__")
                Assert.Equal(CoreText.Get("MuseResultMissing"), result.Error);
            if (prompt == "__CAB_MUSE_FOREIGN_ONLY__")
                Assert.Equal(CoreText.Get("MuseSessionMismatch"), result.Error);
        }
        var failedStore = new SessionStore(_root);
        var failedKey = new SessionKey("slack", "channel", "failed-thread", "owner", "slack");
        var failedSession = await new SessionAgentRunner(failedStore, runner).RunAsync(failedKey, settings, "__CAB_MUSE_NONZERO__");
        Assert.False(failedSession.Success);
        Assert.Null(await failedStore.FindAsync(failedKey));
        var retainedKey = new SessionKey("slack", "channel", "retained-thread", "owner", "slack");
        var sessionRunner = new SessionAgentRunner(failedStore, runner);
        var initial = await sessionRunner.RunAsync(retainedKey, settings, "initial success");
        Assert.True(initial.Success, initial.Error);
        var saved = await failedStore.FindAsync(retainedKey);
        Assert.NotNull(saved);
        var failedResume = await sessionRunner.RunAsync(retainedKey, settings, "__CAB_MUSE_NONZERO__");
        Assert.False(failedResume.Success);
        Assert.Equal(saved.SessionId, (await failedStore.FindAsync(retainedKey))?.SessionId);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(settings, "__CAB_MUSE_CANCEL__", cts.Token));
        var after = Directory.Exists(promptDirectory) ? Directory.GetDirectories(promptDirectory, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        Assert.Empty(after.Except(before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerRejectsWorkspaceContainingPrivatePromptRootBeforeCreatingPrompt(bool useParentOfLocalAppData)
    {
        if (!OperatingSystem.IsWindows()) return;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var workspace = useParentOfLocalAppData ? Directory.GetParent(localAppData)!.FullName : localAppData;
        Assert.True(Directory.Exists(workspace));
        var privatePromptRoot = Path.Combine(localAppData, "CAB-MusePrompts");
        var before = Directory.Exists(privatePromptRoot)
            ? Directory.GetDirectories(privatePromptRoot, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var settings = Settings(null) with
        {
            WorkspaceRoot = workspace,
            TrustedWorkspaceRoots = workspace,
            AgentExecutable = Path.Combine(_fixture, "muse-fixture.exe")
        };

        var result = await new AgentRunner().RunAsync(settings, "must not be written under the workspace");

        Assert.False(result.Success);
        Assert.Equal(CoreText.Get("MusePromptIsolationUnsafe"), result.Error);
        Assert.True(result.CliSession is { TurnFailed: true, SessionId: null });
        var after = Directory.Exists(privatePromptRoot)
            ? Directory.GetDirectories(privatePromptRoot, "run-*").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        Assert.Empty(after.Except(before));
    }

    [Fact]
    public async Task RunnerResolvesJunctionTargetForPromptIsolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var realWorkspace = Directory.CreateDirectory(Path.Combine(_root, "real-workspace")).FullName;
        var alias = Path.Combine(_root, "workspace-alias");
        Assert.True(CreateJunction(alias, realWorkspace), "Could not create the Windows test junction.");
        PrepareFixture();
        var settings = Settings(null) with
        {
            WorkspaceRoot = alias,
            TrustedWorkspaceRoots = _root,
            AgentExecutable = Path.Combine(_fixture, "muse-fixture.exe")
        };
        var privateWorkspaceParentAlias = Path.Combine(_root, "private-parent-alias");
        var privateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAB-MusePrompts");
        var privateWorkspaceTarget = Path.Combine(privateRoot, "workspace-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new AgentRunner().RunAsync(settings, "junction workspace");
            Assert.True(result.Success, result.Error);

            Directory.CreateDirectory(privateRoot);
            Directory.CreateDirectory(privateWorkspaceTarget);
            Assert.True(CreateJunction(privateWorkspaceParentAlias, privateRoot), "Could not create the private root parent test junction.");
            var privateWorkspace = Path.Combine(privateWorkspaceParentAlias, Path.GetFileName(privateWorkspaceTarget));
            var blocked = await MuseExecRunner.RunAsync(settings with { WorkspaceRoot = privateWorkspace }, privateWorkspace,
                "must stay private", progress: null, CancellationToken.None);
            Assert.False(blocked.Success);
            Assert.Equal(CoreText.Get("MusePromptIsolationUnsafe"), blocked.Error);
            Assert.Null(blocked.CliSession?.SessionId);

            var unresolved = Path.Combine(_root, "missing-workspace");
            var unresolvedResult = await MuseExecRunner.RunAsync(settings with { WorkspaceRoot = unresolved }, unresolved,
                "must fail closed", progress: null, CancellationToken.None);
            Assert.False(unresolvedResult.Success);
            Assert.Equal(CoreText.Get("MusePromptIsolationUnsafe"), unresolvedResult.Error);
        }
        finally
        {
            if (Directory.Exists(privateWorkspaceParentAlias)) Directory.Delete(privateWorkspaceParentAlias, recursive: false);
            TestDirectories.DeleteWithRetry(privateWorkspaceTarget);
            if (Directory.Exists(alias)) Directory.Delete(alias, recursive: false);
        }
    }

    [Fact]
    public void StartupPromptRecoveryRemovesOrphansAndPreservesActiveLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAB-MusePrompts");
        Directory.CreateDirectory(root);
        var orphan = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, ".lease"), "");
        File.WriteAllText(Path.Combine(orphan, "prompt.txt"), "stale private prompt");
        var active = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(active);
        var leasePath = Path.Combine(active, ".lease");
        File.WriteAllText(Path.Combine(active, "prompt.txt"), "active prompt");
        using var activeLease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        MusePromptRecovery.CleanupAbandoned();

        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(leasePath));
        Assert.True(File.Exists(Path.Combine(active, "prompt.txt")));
        activeLease.Dispose();
        TestDirectories.DeleteWithRetry(active);
    }

    private static bool CreateJunction(string link, string target)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(link);
        process.StartInfo.ArgumentList.Add(target);
        process.Start();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    [Fact]
    public async Task RunnerDistinguishesMissingExecutableInvalidConfigurationAndBrokenLauncher()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var runner = new AgentRunner();
        var missing = await runner.RunAsync(Settings(null) with
        { AgentExecutable = Path.Combine(_root, "missing-muse.exe") }, "private request");
        Assert.Equal(CoreText.Get("MuseExecutableNotFound"), missing.Error);

        var invalid = await runner.RunAsync(Settings(null) with
        { AgentArguments = "exec --json --yolo" }, "private request");
        Assert.Equal(CoreText.Get("MuseConfigurationInvalid"), invalid.Error);

        var launcher = Path.Combine(_root, "muse.cmd");
        await File.WriteAllTextAsync(launcher, "@exit /b 99");
        var broken = await runner.RunAsync(Settings(null) with
        { AgentExecutable = launcher }, "private request");
        Assert.Equal(CoreText.Get("MuseExecutableInvalid"), broken.Error);
        var badImage = Path.Combine(_root, "bad-muse.exe");
        await File.WriteAllTextAsync(badImage, "This is not a Windows executable.");
        var invalidImage = await runner.RunAsync(Settings(null) with
        { AgentExecutable = badImage }, "private request");
        Assert.Equal(CoreText.Get("MuseExecutableInvalid"), invalidImage.Error);
        foreach (var result in new[] { missing, invalid, broken, invalidImage })
        {
            Assert.False(result.Success);
            Assert.Null(result.CliSession?.SessionId);
            Assert.DoesNotContain("private request", result.Error);
        }
    }

    private BridgeSettings Settings(string? model) => new()
    {
        AgentProvider = AgentProfile.Muse,
        AgentExecutable = "muse",
        AgentArguments = AgentProfile.DefaultMuseArguments,
        AgentModel = model,
        WorkspaceRoot = _root,
        TrustedWorkspaceRoots = _root
    };

    private static string Event(string streamKind, string streamId, string type, object payload)
        => JsonSerializer.Serialize(new { schema_version = 1, id = Guid.CreateVersion7(), stream = new { kind = streamKind, id = streamId }, sequence = 1, payload_type = type, payload });

    private void PrepareFixture()
    {
        Directory.CreateDirectory(_fixture);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "ChatAgentBridge.TestAgent"))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "ChatAgentBridge.TestAgent", "bin", configuration, "net10.0");
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix), Path.Combine(_fixture, "ChatAgentBridge.TestAgent" + suffix));
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), Path.Combine(_fixture, "muse-fixture.exe"));
    }

    public void Dispose()
    {
        TestDirectories.DeleteWithRetry(_root);
        TestDirectories.DeleteWithRetry(_fixture);
    }
}
