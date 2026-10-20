using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ArgumentParser_PreservesQuotedArguments()
    {
        var result = AgentArgumentParser.Parse("exec --json \"two words\" -");
        Assert.Equal(new[] { "exec", "--json", "two words", "-" }, result);
    }

    [Fact]
    public void ArgumentParser_PreservesBackslashesBeforeClosingQuote()
    {
        var result = AgentArgumentParser.Parse("--path \"C:\\folder\\\\\"");
        Assert.Equal(new[] { "--path", "C:\\folder\\" }, result);
    }

    [Fact]
    public void ArgumentParser_RejectsUnterminatedQuote()
        => Assert.Throws<FormatException>(() => AgentArgumentParser.Parse("exec \"unfinished"));

    [Fact]
    public async Task SettingsStore_FirstRunSuggestsExistingDocumentsWithoutSaving()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var settings = await new SettingsStore(path).LoadAsync();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.Equal(Path.IsPathFullyQualified(documents) && Directory.Exists(documents) ? documents : "", settings.WorkspaceRoot);
        Assert.Empty(settings.TrustedWorkspaceRoots);
        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:/missing-saved-workspace")]
    public async Task SettingsStore_PreservesSavedWorkspaceInsteadOfApplyingFirstRunDefault(string workspace)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new BridgeSettings { WorkspaceRoot = workspace });
            Assert.Equal(workspace, (await store.LoadAsync()).WorkspaceRoot);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_RoundTripsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            var settings = new BridgeSettings
            {
                WorkspaceRoot = "C:/workspace", AgentArguments = "exec --json -",
                AgentModel = "gpt-5.4", AgentReasoningEffort = "future_level", AutoConnectProvider = "slack",
                MattermostServerUrl = "https://chat.example", MattermostChannelId = "abc",
                MattermostBypassProxy = true, PostConnectionNotice = true,
                MattermostToken = OperatingSystem.IsWindows() ? "mm-secret" : "",
                SlackBotToken = OperatingSystem.IsWindows() ? "slack-secret" : "",
                SlackAppToken = OperatingSystem.IsWindows() ? "app-secret" : "", RequireApproval = false,
                MattermostAllowedUserIds = "mm-user-1; mm-user-2",
                SlackAllowedUserIds = "U12345\nU67890", ShareApprovalsInChat = true, ShareAgentQuestionsInChat = true
            };
            await store.SaveAsync(settings);
            Assert.Equal(settings with
            {
                MattermostAllowedUserIds = "mm-user-1,mm-user-2",
                SlackAllowedUserIds = "U12345,U67890"
            }, await store.LoadAsync());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_InfersLegacyCustomProviderAndDefaultsNewFields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "settings.json"),
                "{\"SchemaVersion\":1,\"AgentExecutable\":\"powershell.exe\",\"AgentArguments\":\"-NoProfile -File legacy.ps1\"}");
            var settings = await new SettingsStore(path).LoadAsync();
            Assert.Equal(AgentProfile.Custom, settings.AgentProvider);
            Assert.Equal("-NoProfile -File legacy.ps1", settings.AgentArguments);
            Assert.Null(settings.AgentModel);
            Assert.Null(settings.AgentReasoningEffort);
            Assert.Equal("none", settings.AutoConnectProvider);
            Assert.Equal("", settings.MattermostAllowedUserIds);
            Assert.Equal("", settings.SlackAllowedUserIds);
            Assert.False(settings.ShareApprovalsInChat);
            Assert.False(settings.ShareAgentQuestionsInChat);
            Assert.False(settings.MattermostBypassProxy);
            Assert.False(settings.PostConnectionNotice);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_NormalizesAllowedUsersAndRejectsInvalidLists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new BridgeSettings { MattermostAllowedUserIds = " first;second\nfirst ", SlackAllowedUserIds = " U123, U456 ", ShareApprovalsInChat = true });
            var loaded = await store.LoadAsync();
            Assert.Equal("first,second", loaded.MattermostAllowedUserIds);
            Assert.Equal("U123,U456", loaded.SlackAllowedUserIds);
            Assert.True(loaded.ShareApprovalsInChat);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new BridgeSettings { SlackAllowedUserIds = "@name" }));

            await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{\"SchemaVersion\":1,\"MattermostAllowedUserIds\":\"@name\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void SettingsStore_DefaultPathHonorsWinUiOverride()
    {
        var previous = Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", path);
            Assert.Equal(Path.GetFullPath(path), new SettingsStore().DirectoryPath);
        }
        finally { Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", previous); }
    }

    [Fact]
    public async Task SettingsStore_ProtectsTokensOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new BridgeSettings { MattermostToken = "distinct-mm-secret", SlackBotToken = "distinct-slack-secret" });
            var contents = await File.ReadAllTextAsync(Path.Combine(path, "settings.json"));
            Assert.DoesNotContain("distinct-mm-secret", contents);
            Assert.DoesNotContain("distinct-slack-secret", contents);
            Assert.Contains("dpapi:", contents);
            Assert.Contains("\"SchemaVersion\": 1", contents);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_RejectsCorruptedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{");
            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(path).LoadAsync());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_RejectsUnsupportedSchemaVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{\"SchemaVersion\":99}");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(path).LoadAsync());
            Assert.Contains("version 99", error.Message);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task SettingsStore_RefusesPlaintextTokensOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new SettingsStore(path).SaveAsync(
                new BridgeSettings { MattermostToken = "secret" }));
            Assert.False(File.Exists(Path.Combine(path, "settings.json")));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task AgentRunner_RejectsMissingWorkspace()
    {
        var runner = new AgentRunner();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => runner.RunAsync(
            new BridgeSettings { WorkspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }, "hello"));
    }

    [Fact]
    public async Task AgentRunner_ReturnsLaunchAndNonzeroFailures()
    {
        var runner = new AgentRunner();
        var workspace = Path.GetTempPath();
        var missing = await runner.RunAsync(new BridgeSettings { WorkspaceRoot = workspace, AgentExecutable = "no-such-agent-executable" }, "hello");
        Assert.False(missing.Success);
        Assert.NotEqual(0, missing.ExitCode);

        if (OperatingSystem.IsWindows())
        {
            var result = await runner.RunAsync(new BridgeSettings
            {
                WorkspaceRoot = workspace, AgentExecutable = "cmd.exe", AgentArguments = "/d /c exit 7"
            }, "hello");
            Assert.False(result.Success);
            Assert.Equal(7, result.ExitCode);
            Assert.NotNull(result.Metrics);
            Assert.True(result.Metrics.ProcessStartMilliseconds >= 0);
            Assert.True(result.Metrics.QueueMilliseconds >= 0);
            Assert.True(result.Metrics.TotalMilliseconds >= result.Metrics.ProcessStartMilliseconds);
        }
    }

    [Fact]
    public async Task AgentRunner_ParsesCodexEventsBeyondCapturedOutputLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var script = Path.Combine(path, "emit.ps1");
        var codexHost = Path.Combine(path, "codex.exe");
        var powershellHost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        File.Copy(powershellHost, codexHost);
        var sessionId = Guid.NewGuid();
        var jsonLines = string.Join("\n", new[]
        {
            $"{{\"type\":\"thread.started\",\"thread_id\":\"{sessionId:D}\"}}",
            "{\"type\":\"turn.failed\"}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"final answer after cap\"}}"
        });
        await File.WriteAllTextAsync(script,
            "$ProgressPreference='SilentlyContinue'; Write-Output ('x' * 2097173); Write-Output @'\n" + jsonLines + "\n'@");
        try
        {
            var result = await new AgentRunner().RunAsync(new BridgeSettings
            {
                WorkspaceRoot = path, AgentExecutable = codexHost, AgentProvider = AgentProfile.Codex,
                AgentArguments = $"-NoProfile -NonInteractive -File \"{script}\""
            }, "ignored");
            Assert.True(result.Success, result.Error);
            Assert.True(result.HasOutputTruncated);
            Assert.NotNull(result.Codex);
            Assert.Equal(sessionId.ToString("D"), result.Codex.SessionId);
            Assert.Equal("final answer after cap", result.Codex.FinalMessage);
            Assert.True(result.Codex.TurnFailed);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void CodexResolver_NeverReturnsWindowsShellShim()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "codex.cmd"))
            .FirstOrDefault(File.Exists);
        if (shim is null) return;
        try
        {
            var native = CodexExecutableResolver.Resolve(shim);
            Assert.EndsWith(".exe", native, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(native));
        }
        catch (InvalidOperationException error)
        {
            Assert.Contains("codex.exe", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AgentRunner_CancellationKillsProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var pidFile = Path.Combine(path, "pid.txt");
        var scriptFile = Path.Combine(path, "wait.ps1");
        await File.WriteAllTextAsync(scriptFile, $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 30");
        using var cancellation = new CancellationTokenSource();
        var runner = new AgentRunner();
        var run = runner.RunAsync(new BridgeSettings
        {
            WorkspaceRoot = path, AgentExecutable = "powershell.exe", AgentArguments = $"-NoProfile -NonInteractive -File \"{scriptFile}\""
        }, "hello", cancellation.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            int? pid = null;
            while (pid is null && DateTime.UtcNow < deadline)
            {
                try
                {
                    var contents = await File.ReadAllTextAsync(pidFile);
                    if (int.TryParse(contents, out var candidate) && candidate > 0 && ProcessExists(candidate))
                        pid = candidate;
                }
                catch (IOException)
                {
                    // PowerShell may still hold the file while writing the process id.
                }
                if (pid is null) await Task.Delay(50);
            }
            Assert.True(pid.HasValue, "PowerShell did not write a complete, readable process id within 10 seconds.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            for (var attempt = 0; attempt < 30 && ProcessExists(pid.Value); attempt++) await Task.Delay(50);
            Assert.False(ProcessExists(pid.Value), $"Cancelled process {pid.Value} is still running.");
        }
        finally
        {
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task AgentRunner_SerializesRunsForSameWorkspaceAcrossInstances()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var events = Path.Combine(path, "events.txt");
        var script = Path.Combine(path, "record.ps1");
        var quotedEventsPath = events.Replace("'", "''", StringComparison.Ordinal);
        await File.WriteAllTextAsync(script,
            $"Add-Content -LiteralPath '{quotedEventsPath}' -Value start; Start-Sleep -Milliseconds 600; Add-Content -LiteralPath '{quotedEventsPath}' -Value end");
        try
        {
            var settings = new BridgeSettings
            {
                WorkspaceRoot = path, AgentExecutable = "powershell.exe",
                AgentArguments = $"-NoProfile -NonInteractive -File \"{script}\""
            };
            var results = await Task.WhenAll(new AgentRunner().RunAsync(settings, "one"), new AgentRunner().RunAsync(settings, "two"));
            Assert.All(results, result => Assert.True(result.Success, result.Error));
            Assert.Equal(new[] { "start", "end", "start", "end" }, await File.ReadAllLinesAsync(events));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private static bool ProcessExists(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
