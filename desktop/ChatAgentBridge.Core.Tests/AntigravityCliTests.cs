using System.Text.Json;
using System.Text.Json.Nodes;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class AntigravityCliTests : IDisposable
{
    private const string Id = "0b8f5f3e-5b1c-4a0e-9a53-0c7d2e4f6a11";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cab-antigravity-" + Guid.NewGuid().ToString("N"));
    private static readonly SessionKey Key = new("slack", "channel", "thread", "user", "slack");
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string L(string raw) => raw.Replace("__ID__", Id, StringComparison.Ordinal);

    private static BridgeSettings Settings() => new() { AgentProvider = AgentProfile.Antigravity };

    // ---- Argument building

    [Fact]
    public void DefaultsUseAgyWithForcedStreamJsonAndNoSessionOrPermissionFlags()
    {
        var invocation = AgentProfile.BuildInvocation(Settings());

        Assert.Equal("agy", invocation.Executable);
        Assert.Equal(AgentProfile.Antigravity, invocation.Provider);
        Assert.Equal(new[] { "--output-format", "stream-json" }, invocation.Arguments);
        Assert.Null(invocation.Model);
        Assert.Null(invocation.ReasoningEffort);
        Assert.True(AgentProfile.IsBasicCliProvider(AgentProfile.Antigravity));
    }

    [Fact]
    public void StaleCodexDefaultsAreReplacedAndConfiguredOutputFormatIsOverridden()
    {
        var stale = AgentProfile.BuildInvocation(Settings() with { AgentArguments = AgentProfile.DefaultCodexArguments, AgentExecutable = "codex" });
        Assert.Equal("agy", stale.Executable);
        Assert.Equal(new[] { "--output-format", "stream-json" }, stale.Arguments);

        var overridden = AgentProfile.BuildInvocation(Settings() with { AgentArguments = "--output-format json --add-dir C:\\other --output-format=text" });
        Assert.Equal(new[] { "--add-dir", "C:\\other", "--output-format", "stream-json" }, overridden.Arguments);
    }

    [Fact]
    public void ModelIsPassedAsSeparateArguments()
    {
        var invocation = AgentProfile.BuildInvocation(Settings() with { AgentModel = " gemini-3.8-flash-high " });

        Assert.Equal("gemini-3.8-flash-high", invocation.Model);
        Assert.Equal(new[] { "--output-format", "stream-json", "--model", "gemini-3.8-flash-high" }, invocation.Arguments);
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings() with { AgentModel = "--dangerously-skip-permissions" }));
    }

    [Fact]
    public void ResumeUsesConversationNeverResumeAndOnlyForAnAuthorizedUuid()
    {
        var settings = Settings() with { AuthorizedCliSessionId = Id.ToUpperInvariant() };
        var arguments = AgentProfile.BuildInvocation(settings).Arguments;

        Assert.Equal(new[] { "--conversation", Id }, arguments.TakeLast(2));
        Assert.DoesNotContain("--resume", arguments);
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(settings with { AuthorizedCliSessionId = "latest" }));
    }

    [Theory]
    [InlineData("inherit", new string[0])]
    [InlineData("workspace-write", new[] { "--mode", "accept-edits" })]
    [InlineData("full-access", new[] { "--dangerously-skip-permissions" })]
    public void PermissionModesMapOntoAgyFlags(string mode, string[] flags)
    {
        var invocation = AgentProfile.BuildInvocation(Settings() with
        {
            AgentPermissionMode = mode, AgentModel = "model-x", AuthorizedCliSessionId = Id
        });

        Assert.Equal(new[] { "--output-format", "stream-json", "--model", "model-x", "--conversation", Id }.Concat(flags),
            invocation.Arguments);
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("ask")]
    public void ReadOnlyAndAskPermissionModesAreRejectedWithAClearError(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings() with { AgentPermissionMode = mode }));
        Assert.Contains("read-only and ask are unavailable", error.Message);
        Assert.False(AgentProfile.SupportsPermissionMode(AgentProfile.Antigravity, mode));
    }

    [Fact]
    public void OnlyAntigravityAcceptsBridgeManagedPermissionModesAmongBasicProviders()
    {
        foreach (var provider in new[] { AgentProfile.Gemini, AgentProfile.GitHubCopilot })
        {
            Assert.True(AgentProfile.SupportsPermissionMode(provider, "inherit"));
            Assert.False(AgentProfile.SupportsPermissionMode(provider, "workspace-write"));
            Assert.False(AgentProfile.SupportsPermissionMode(provider, "full-access"));
        }
        Assert.True(AgentProfile.SupportsPermissionMode(AgentProfile.Antigravity, "workspace-write"));
        Assert.True(AgentProfile.SupportsPermissionMode(AgentProfile.Antigravity, "full-access"));
    }

    [Fact]
    public void ConfiguredModeAndSkipFlagsYieldToAnExplicitPermissionModeButSurviveInherit()
    {
        const string configured = "--mode plan --dangerously-skip-permissions --sandbox --add-dir C:\\x";

        var inherit = AgentProfile.BuildInvocation(Settings() with { AgentArguments = configured });
        Assert.Equal(new[] { "--mode", "plan", "--dangerously-skip-permissions", "--sandbox", "--add-dir", "C:\\x", "--output-format", "stream-json" },
            inherit.Arguments);

        var write = AgentProfile.BuildInvocation(Settings() with { AgentArguments = configured, AgentPermissionMode = "workspace-write" });
        Assert.Equal(new[] { "--sandbox", "--add-dir", "C:\\x", "--output-format", "stream-json", "--mode", "accept-edits" }, write.Arguments);

        var full = AgentProfile.BuildInvocation(Settings() with { AgentArguments = "--mode=plan --sandbox", AgentPermissionMode = "full-access" });
        Assert.Equal(new[] { "--sandbox", "--output-format", "stream-json", "--dangerously-skip-permissions" }, full.Arguments);
    }

    [Fact]
    public void ReasoningEffortOverrideIsUnavailable()
        => Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings() with { AgentReasoningEffort = "high" }));

    [Theory]
    [InlineData("--conversation 00000000-0000-0000-0000-000000000000")]
    [InlineData("--conversation=latest")]
    [InlineData("--continue")]
    [InlineData("-c")]
    [InlineData("--print")]
    [InlineData("--prompt arbitrary")]
    [InlineData("-p arbitrary")]
    [InlineData("--prompt-interactive arbitrary")]
    [InlineData("-i arbitrary")]
    [InlineData("--input-format stream-json")]
    [InlineData("--input-format=stream-json")]
    [InlineData("--remote-control")]
    [InlineData("--new-project")]
    [InlineData("--project other")]
    [InlineData("--project=other")]
    [InlineData("--resume latest")]
    [InlineData("--cwd C:\\elsewhere")]
    [InlineData("-cfoo")]
    public async Task BridgeManagedOptionsAreRejectedInConfiguredArgumentsAndSavedProfiles(string arguments)
    {
        Assert.Throws<ArgumentException>(() => AgentProfile.BuildInvocation(Settings() with { AgentArguments = arguments }));
        var directory = Path.Combine(_directory, "profiles");
        var profile = new AgentConnectionProfile(AgentProfile.Antigravity, "agy", arguments, "exec", null, null,
            "inherit", false, "1.4.7", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentConnectionStore(directory).SaveVerifiedAsync(profile));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void NativeExecutableIsNeverTreatedAsAnNpmShim()
    {
        Assert.False(AgentProfile.UsesNpmShim(AgentProfile.Antigravity));
        Assert.Null(AgentProfile.FindWindowsNpmShim("agy", AgentProfile.Antigravity));
        Assert.Null(AgentProfile.FindWindowsNpmShim("C:\\tools\\agy.cmd", AgentProfile.Antigravity));
        Assert.Null(NodeCliPackageResolver.Resolve("agy", AgentProfile.Antigravity));
    }

    // ---- Settings and registration

    [Theory]
    [InlineData("inherit", true)]
    [InlineData("workspace-write", true)]
    [InlineData("full-access", true)]
    [InlineData("read-only", false)]
    [InlineData("ask", false)]
    public async Task SettingsStoreAcceptsOnlySupportedPermissionModesAndRejectsEffort(string mode, bool allowed)
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings"));
        var settings = new BridgeSettings
        {
            AgentProvider = AgentProfile.Antigravity, AgentExecutable = "agy", AgentArguments = AgentProfile.DefaultAntigravityArguments,
            AgentModel = "  claude-sonnet-4-6 ", AgentPermissionMode = mode
        };
        if (!allowed)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings));
            return;
        }

        await store.SaveAsync(settings);
        Assert.Equal(settings with { AgentModel = "claude-sonnet-4-6" }, await store.LoadAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings with { AgentReasoningEffort = "high" }));

        var path = Path.Combine(_directory, "settings", "settings.json");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored["AgentPermissionMode"] = "read-only";
        await File.WriteAllTextAsync(path, stored.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task ConnectionRegistrationKeepsSupportedPermissionModes()
    {
        var store = new AgentConnectionStore(Path.Combine(_directory, "connections"));
        var profile = new AgentConnectionProfile(AgentProfile.Antigravity, "agy", AgentProfile.DefaultAntigravityArguments, "exec",
            "gemini-3.8-flash-high", null, "workspace-write", false, "1.4.7", DateTimeOffset.UtcNow);

        await store.SaveVerifiedAsync(profile);

        var saved = (await store.FindAsync(AgentProfile.Antigravity))!;
        Assert.Equal("workspace-write", saved.PermissionMode);
        var applied = saved.ApplyTo(new BridgeSettings());
        Assert.Equal(AgentProfile.Antigravity, applied.AgentProvider);
        Assert.Equal("workspace-write", applied.AgentPermissionMode);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(profile with { PermissionMode = "read-only" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(profile with { PermissionMode = "ask" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(profile with { ReasoningEffort = "high" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveVerifiedAsync(profile with { Mode = "app-server" }));
        Assert.Equal(saved, await store.FindAsync(AgentProfile.Antigravity));
    }

    [Theory]
    [InlineData("!cab agent antigravity", "antigravity", null)]
    [InlineData("!cab agent AGY summary", "antigravity", true)]
    [InlineData("!cab agent antigravity fresh", "antigravity", false)]
    public void ChatCommandAcceptsTheProviderAndItsAlias(string text, string provider, bool? includeSummary)
    {
        Assert.True(AgentChatCommand.TryParse(text, out var command));
        Assert.True(command.IsValid);
        Assert.Equal(provider, command.Provider);
        Assert.Equal(includeSummary, command.IncludeSummary);
    }

    [Fact]
    public void SessionMarkerIdentifiesTheProviderFromTheBinding()
    {
        var marker = ConversationAgentService.CliSessionMarker(AgentProfile.Antigravity);
        var binding = new SessionBinding(Key, "C:\\w", "agy", marker + ":" + new string('a', 64), Id, DateTimeOffset.UtcNow);

        Assert.Equal("antigravity-stream-json-v1", marker);
        Assert.Equal(AgentProfile.Antigravity, ConversationAgentService.ProviderOf(binding));
    }

    // ---- CLI probe

    [Theory]
    [InlineData("Usage of agy.exe:", true)]
    [InlineData("Usage of agy:", true)]
    [InlineData("Antigravity CLI 1.4.7", true)]
    [InlineData("1.4.7", false)]
    [InlineData("Usage of gemini:", false)]
    [InlineData("magyar 1.0", false)]
    public void ProbeRecognizesAgyFromHelpButNotFromABareSemver(string output, bool expected)
        => Assert.Equal(expected, CliProbeService.RecognizesProvider(AgentProfile.Antigravity, output));

    // ---- Output parsing

    private static string SampleOutput(string response = "OK\n", string status = "SUCCESS", object? denied = null)
    {
        var lines = new List<string>
        {
            """{"event":"init","conversation_id":"__ID__","init":{"cwd":"C:\\work","tools":["write_file","command"],"permission_mode":"request-review"}}""".Replace("__ID__", Id),
            """{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":0,"state":"DONE","step_type":"user_input","text_delta":"hidden prompt"}}""".Replace("__ID__", Id),
            """{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"OK"}}""".Replace("__ID__", Id),
            """{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":3.1}}""".Replace("__ID__", Id)
        };
        var result = new Dictionary<string, object?>
        {
            ["conversation_id"] = Id, ["status"] = status, ["response"] = response, ["duration_seconds"] = 3.2, ["num_turns"] = 1,
            ["usage"] = new { input_tokens = 6742, output_tokens = 49, thinking_tokens = 48, cache_read_tokens = 0, total_tokens = 6791 }
        };
        if (denied is not null) result["denied_actions"] = denied;
        lines.Add(Json(new { @event = "result", result }));
        return string.Join('\n', lines);
    }

    [Fact]
    public void ParsesTheDocumentedSampleLines()
    {
        var parsed = CliSessionOutputParser.Parse(SampleOutput(), AgentProfile.Antigravity);

        Assert.Equal(AgentProfile.Antigravity, parsed.Provider);
        Assert.Equal(Id, parsed.SessionId);
        Assert.True(parsed.HasResult);
        Assert.False(parsed.TurnFailed);
        Assert.Equal("OK\n", parsed.FinalMessage);
        Assert.Null(parsed.DeniedActions);
    }

    [Fact]
    public void MapsUsageWithAntigravityStreamJsonSource()
    {
        var usage = CliSessionOutputParser.Parse(SampleOutput(), AgentProfile.Antigravity).Usage!;

        // total_tokens (6791) = input (6742) + output (49), so thinking tokens are already inside the output count.
        Assert.Equal(6742, usage.InputTokens);
        Assert.Equal(6742, usage.TotalInputTokens);
        Assert.Equal(49, usage.OutputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal("antigravity.stream-json", usage.Source);
        Assert.Equal("turn", usage.Scope);
        Assert.Null(usage.Model);
    }

    [Fact]
    public void UsageIsOmittedWhenTheResultCarriesNoCounters()
    {
        var output = string.Join('\n', L("""{"event":"init","conversation_id":"__ID__"}"""),
            L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"x"}}"""));

        Assert.Null(CliSessionOutputParser.Parse(output, AgentProfile.Antigravity).Usage);
    }

    [Fact]
    public void FallsBackToStreamedAgentResponseTextWhenTheResponseIsEmpty()
    {
        var output = string.Join('\n',
            L("""{"event":"init","conversation_id":"__ID__"}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"Hel"}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ACTIVE","step_type":"agent_response","text_delta":"lo "}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"Hello "}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"command","text_delta":"hidden tool text"}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":3,"state":"ACTIVE","step_type":"agent_response","text_delta":"world"}}"""),
            L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"  "}}"""));

        var parsed = CliSessionOutputParser.Parse(output, AgentProfile.Antigravity);

        Assert.False(parsed.TurnFailed);
        Assert.Equal("Hello \nworld", parsed.FinalMessage);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("CANCELLED")]
    [InlineData("")]
    public void NonSuccessStatusFailsTheTurnButStillConfirmsTheSession(string status)
    {
        var parsed = CliSessionOutputParser.Parse(SampleOutput(status: status), AgentProfile.Antigravity);

        Assert.True(parsed.TurnFailed);
        Assert.Equal(Id, parsed.SessionId);
        Assert.True(parsed.HasResult);
    }

    [Fact]
    public void MissingResultInvalidJsonConflictingIdsAndMissingIdFailClosed()
    {
        var withoutResult = string.Join('\n', SampleOutput().Split('\n').SkipLast(1));
        var noResult = CliSessionOutputParser.Parse(withoutResult, AgentProfile.Antigravity);
        Assert.False(noResult.HasResult);
        Assert.True(noResult.TurnFailed);

        var malformed = CliSessionOutputParser.Parse("{malformed\n" + SampleOutput(), AgentProfile.Antigravity);
        Assert.True(malformed.TurnFailed);
        Assert.Null(malformed.SessionId);

        var conflicting = CliSessionOutputParser.Parse(
            """{"event":"init","conversation_id":"11111111-2222-4333-8444-555555555555"}""" + "\n" +
            L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"x"}}"""), AgentProfile.Antigravity);
        Assert.True(conflicting.TurnFailed);
        Assert.Null(conflicting.SessionId);

        var missingId = CliSessionOutputParser.Parse(
            """{"event":"result","result":{"status":"SUCCESS","response":"x"}}""", AgentProfile.Antigravity);
        Assert.True(missingId.TurnFailed);
        Assert.Null(missingId.SessionId);

        var invalidId = CliSessionOutputParser.Parse(
            """{"event":"init","conversation_id":"latest"}""" + "\n" + """{"event":"result","result":{"status":"SUCCESS"}}""", AgentProfile.Antigravity);
        Assert.True(invalidId.TurnFailed);
        Assert.Null(invalidId.SessionId);

        var afterResult = CliSessionOutputParser.Parse(SampleOutput() + "\n" + """{"event":"init","conversation_id":"x"}""", AgentProfile.Antigravity);
        Assert.True(afterResult.TurnFailed);

        var wrongDiscriminator = CliSessionOutputParser.Parse(
            L("""{"type":"init","conversation_id":"__ID__"}"""), AgentProfile.Antigravity);
        Assert.True(wrongDiscriminator.TurnFailed);
    }

    [Fact]
    public void DeniedActionsAreAppendedAsALocalizedNoteAndExposed()
    {
        var denied = new object[]
        {
            new { action = "write_file", display_name = "WriteFile" },
            new { action = "command" },
            new { action = "write_file", display_name = "WriteFile" },
            new { }
        };

        var parsed = CliSessionOutputParser.Parse(SampleOutput(denied: denied), AgentProfile.Antigravity);

        Assert.False(parsed.TurnFailed);
        Assert.Equal(new[] { "WriteFile", "command" }, parsed.DeniedActions);
        Assert.StartsWith("OK\n", parsed.FinalMessage);
        Assert.EndsWith(CoreText.Get("CliAntigravityActionsDenied", "WriteFile, command"), parsed.FinalMessage);
        Assert.Contains("settings.json", parsed.FinalMessage);
    }

    [Fact]
    public void DeniedActionsAloneStillProduceTheNoteAndEmptyDeniedListAddsNothing()
    {
        var onlyNote = CliSessionOutputParser.Parse(string.Join('\n',
            L("""{"event":"init","conversation_id":"__ID__"}"""),
            L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"","denied_actions":[{"action":"command"}]}}""")),
            AgentProfile.Antigravity);
        Assert.Equal(CoreText.Get("CliAntigravityActionsDenied", "command"), onlyNote.FinalMessage);

        var none = CliSessionOutputParser.Parse(SampleOutput(denied: Array.Empty<object>()), AgentProfile.Antigravity);
        Assert.Equal("OK\n", none.FinalMessage);
        Assert.Null(none.DeniedActions);
    }

    [Fact]
    public void ProgressReportsToolStepsOnceAndStreamsResponseText()
    {
        var accumulator = new CliSessionOutputParser.Accumulator(AgentProfile.Antigravity);
        var progress = new List<AgentRunProgress>();
        foreach (var line in new[]
        {
            L("""{"event":"init","conversation_id":"__ID__"}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"write_file","tool_info":{"name":"write_file","parameters":{"path":"secret.txt","content":"hidden-arguments"}}}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"write_file"}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":1,"state":"ERROR","step_type":"tool","tool_name":"write_file","tool_info":{"error":{"type":"TOOL_ERROR","message":"hidden-error"}}}}"""),
            L("""{"event":"step_update","step_update":{"conversation_id":"__ID__","step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"streamed"}}"""),
            L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"streamed"}}""")
        }) progress.AddRange(accumulator.AddLine(line));

        var activity = progress.Where(item => item.Stream == "activity").Select(item => item.Text).ToArray();
        var toolLabel = CoreText.Get("ProgressUsingTool", "write_file") + Environment.NewLine;
        Assert.Equal(1, activity.Count(text => text == toolLabel));
        Assert.Contains(CoreText.Get("ProgressFinished") + Environment.NewLine, activity);
        Assert.Equal(new[] { "streamed" }, progress.Where(item => item.Stream == "stdout").Select(item => item.Text));
        Assert.DoesNotContain(progress, item => item.Text.Contains("hidden", StringComparison.Ordinal));
        Assert.Equal("streamed", accumulator.Snapshot().FinalMessage);
    }

    [Fact]
    public void ProgressShowsAResponseThatNeverStreamedExactlyOnce()
    {
        var accumulator = new CliSessionOutputParser.Accumulator(AgentProfile.Antigravity);
        var progress = new List<AgentRunProgress>();
        progress.AddRange(accumulator.AddLine(L("""{"event":"init","conversation_id":"__ID__"}""")));
        progress.AddRange(accumulator.AddLine(L("""{"event":"result","result":{"conversation_id":"__ID__","status":"SUCCESS","response":"only in result"}}""")));

        Assert.Equal(new[] { "only in result" }, progress.Where(item => item.Stream == "stdout").Select(item => item.Text));
    }

    [Fact]
    public void ToolNamesInProgressAreSanitized()
    {
        var accumulator = new CliSessionOutputParser.Accumulator(AgentProfile.Antigravity);
        var toolName = "bad\u001b[31m" + new string('x', 200);
        var progress = accumulator.AddLine(Json(new
        {
            @event = "step_update",
            step_update = new { conversation_id = Id, step_index = 1, state = "ACTIVE", step_type = "tool", tool_name = toolName }
        }));

        var text = Assert.Single(progress).Text;
        Assert.DoesNotContain('\u001b', text);
        Assert.True(text.Length < 120);
    }

    // ---- Model list

    [Fact]
    public void ModelListParsesTabSeparatedRowsAndIgnoresBannerAndInvalidRows()
    {
        var output = "Fetching available models...\r\n" +
                     "gemini-3.8-flash-high\tGemini 3.8 Flash (High)\r\n" +
                     "claude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\r\n" +
                     "\r\n" +
                     "no-label-row\r\n" +
                     "--evil\tNot a model\r\n" +
                     "has space\tBad id\r\n" +
                     "claude-sonnet-4-6\tDuplicate\r\n" +
                     "bare-id\t\r\n";

        var models = ModelCatalogService.ParseAntigravityModels(output);

        Assert.Equal(new[] { "gemini-3.8-flash-high", "claude-sonnet-4-6", "bare-id" }, models.Select(model => model.Id));
        Assert.Equal("Gemini 3.8 Flash (High)", models[0].DisplayName);
        Assert.Equal("Gemini 3.8 Flash (High) (gemini-3.8-flash-high)", models[0].SelectionLabel);
        Assert.Equal("bare-id", models[2].DisplayName);
        Assert.All(models, model => Assert.Empty(model.SupportedReasoningEfforts));
        Assert.Empty(ModelCatalogService.ParseAntigravityModels("Fetching available models...\n"));
    }

    [Fact]
    public async Task ModelCatalogRunsAgyModelsAndIgnoresStderr()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyTestAgentAs("agy");
        var settings = Settings() with { AgentExecutable = executable, WorkspaceRoot = Path.GetDirectoryName(executable)! };

        var models = await new ModelCatalogService().GetModelsAsync(settings).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(new[] { "fixture-agy-fast", "fixture-agy-deep" }, models.Select(model => model.Id));
        Assert.Equal("Fixture Agy Deep (Thinking)", models[1].DisplayName);
    }

    [Fact]
    public async Task ConversationModelServiceUsesTheCatalogForAntigravityAndStillAllowsManualSwitching()
    {
        var store = new SessionStore(Path.Combine(_directory, "model-sessions"));
        var marker = ConversationAgentService.CliSessionMarker(AgentProfile.Antigravity) + ":" + new string('a', 64);
        await store.SaveAsync(new SessionBinding(Key, Path.GetFullPath(Path.GetTempPath()), "agy", marker, Id, DateTimeOffset.UtcNow)
            with { AgentPermissionMode = "inherit" });
        var settings = Settings() with { AgentExecutable = "agy", WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) };
        var discovered = 0;
        var service = new ConversationModelService(store, (_, _) =>
        {
            discovered++;
            return Task.FromResult<IReadOnlyList<ModelOption>>([new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6", "", false)]);
        });

        Assert.Single(await service.GetModelsAsync(settings));
        await service.ChangeAsync(Key, settings, "claude-sonnet-4-6", _ => Task.FromResult(true));
        Assert.Equal("claude-sonnet-4-6", (await store.FindAsync(Key))!.Model);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeAsync(Key, settings, "not-listed", _ => Task.FromResult(true)));
        Assert.Equal(1, discovered);
    }

    // ---- Session runner

    [Fact]
    public async Task SessionsSaveTheConversationIdAndResumeWithConversationFlag()
    {
        var store = new SessionStore(Path.Combine(_directory, "sessions"));
        var fake = new RecordingRunner(_ => new AgentRunResult(true, 0, SampleOutput(), ""));
        var runner = new SessionAgentRunner(store, fake);
        var settings = Settings() with
        {
            AgentExecutable = "agy", AgentModel = "global-model", AgentPermissionMode = "workspace-write",
            WorkspaceRoot = Path.GetFullPath(Path.GetTempPath())
        };

        var first = await runner.RunAsync(Key, settings, "first");
        Assert.True(first.Success, first.Error);
        Assert.Equal("OK\n", first.Output);
        Assert.Equal(6742, first.Usage!.InputTokens);
        var saved = (await store.FindAsync(Key))!;
        Assert.Equal(AgentProfile.Antigravity, ConversationAgentService.ProviderOf(saved));
        Assert.Equal(Id, saved.SessionId);
        var firstArguments = AgentProfile.BuildInvocation(fake.Calls[0]).Arguments;
        Assert.DoesNotContain("--conversation", firstArguments);
        Assert.Equal(new[] { "--mode", "accept-edits" }, firstArguments.TakeLast(2));

        Assert.True((await runner.RunAsync(Key, settings, "second")).Success);
        var secondArguments = AgentProfile.BuildInvocation(fake.Calls[1]).Arguments.ToArray();
        Assert.Equal(Id, secondArguments[Array.IndexOf(secondArguments, "--conversation") + 1]);
        Assert.DoesNotContain("--resume", secondArguments);
        Assert.Equal(new[] { "--mode", "accept-edits" }, secondArguments.TakeLast(2));

        // A different permission mode continues the same conversation: it is not part of the launch identity.
        Assert.True((await runner.RunAsync(Key, settings with { AgentPermissionMode = "full-access" }, "third")).Success);
        Assert.Equal(Id, (await store.FindAsync(Key))!.SessionId);
    }

    [Fact]
    public async Task SessionRunnerSurfacesDeniedActionsInTheReply()
    {
        var store = new SessionStore(Path.Combine(_directory, "denied-sessions"));
        var output = SampleOutput(denied: new[] { new { action = "write_file", display_name = "WriteFile" } });
        var runner = new SessionAgentRunner(store, new RecordingRunner(_ => new AgentRunResult(true, 0, output, "")));

        var result = await runner.RunAsync(Key, Settings() with { AgentExecutable = "agy", WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) }, "edit");

        Assert.True(result.Success, result.Error);
        Assert.Contains(CoreText.Get("CliAntigravityActionsDenied", "WriteFile"), result.Output);
        Assert.Equal(new[] { "WriteFile" }, result.CliSession!.DeniedActions);
    }

    [Fact]
    public async Task SessionRunnerRefusesReasoningEffortAndAnUnsupportedMode()
    {
        var fake = new RecordingRunner(_ => new AgentRunResult(true, 0, SampleOutput(), ""));
        var runner = new SessionAgentRunner(new SessionStore(Path.Combine(_directory, "invalid-sessions")), fake);
        var settings = Settings() with { AgentExecutable = "agy", WorkspaceRoot = Path.GetFullPath(Path.GetTempPath()) };

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Key, settings with { AgentReasoningEffort = "high" }, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Key, settings with { AgentPermissionMode = "read-only" }, "x"));
        Assert.Empty(fake.Calls);
        Assert.Null(await new SessionStore(Path.Combine(_directory, "invalid-sessions")).FindAsync(Key));
    }

    // ---- Real process (Windows fixture only)

    [Fact]
    public async Task FixtureProcessRunsWithStdinPromptResumeAndPermissionFlags()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = CopyTestAgentAs("agy");
        var workspace = Path.GetDirectoryName(executable)!;
        var settings = Settings() with
        {
            AgentExecutable = executable, WorkspaceRoot = workspace, AgentModel = "fixture-agy-fast", AgentPermissionMode = "workspace-write"
        };

        var probe = await new CliProbeService().CheckAsync(settings);
        var first = await new AgentRunner().RunAsync(settings, "hello agy");
        var conversation = first.CliSession!.SessionId!;
        var second = await new AgentRunner().RunAsync(settings with { AuthorizedCliSessionId = conversation }, "again");

        Assert.Equal("1.2.3", probe.Version);
        Assert.True(probe.ProviderRecognized);
        Assert.True(first.Success, first.Error);
        Assert.Contains("antigravity-e2e: new; model=fixture-agy-fast; prompt=hello agy", first.Output);
        Assert.True(second.Success, second.Error);
        Assert.Contains("antigravity-e2e: resumed; model=fixture-agy-fast; prompt=again", second.Output);
        Assert.Equal(conversation, second.CliSession!.SessionId);
        Assert.Equal(100, second.Usage!.InputTokens);
    }

    private string CopyTestAgentAs(string name)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? source = null;
        while (directory is not null && source is null)
        {
            foreach (var root in new[] { Path.Combine(directory.FullName, "ChatAgentBridge.TestAgent"), Path.Combine(directory.FullName, "desktop", "ChatAgentBridge.TestAgent") })
            {
                var candidate = Path.Combine(root, "bin", configuration, "net10.0");
                if (File.Exists(Path.Combine(candidate, "ChatAgentBridge.TestAgent.exe"))) { source = candidate; break; }
            }
            directory = directory.Parent;
        }
        if (source is null) throw new DirectoryNotFoundException("Could not find the built TestAgent fixture.");
        var destination = Path.Combine(_directory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var sourcePath = Path.Combine(source, "ChatAgentBridge.TestAgent" + suffix);
            if (File.Exists(sourcePath)) File.Copy(sourcePath, Path.Combine(destination, "ChatAgentBridge.TestAgent" + suffix), overwrite: true);
        }
        var executable = Path.Combine(destination, name + ".exe");
        File.Copy(Path.Combine(source, "ChatAgentBridge.TestAgent.exe"), executable, overwrite: true);
        return executable;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private sealed class RecordingRunner(Func<BridgeSettings, AgentRunResult> run) : IAgentRunner
    {
        public List<BridgeSettings> Calls { get; } = [];
        public Task<AgentRunResult> RunAsync(BridgeSettings settings, string prompt, CancellationToken cancellationToken = default)
        {
            Calls.Add(settings);
            return Task.FromResult(run(settings));
        }
    }
}
