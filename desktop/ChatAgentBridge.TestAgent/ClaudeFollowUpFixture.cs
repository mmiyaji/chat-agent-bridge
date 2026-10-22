using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Guarded offline fixture for Claude Code's native stream-json follow-up protocol.</summary>
internal static class ClaudeFollowUpFixture
{
    private const string MarkerName = ".cab-claude-follow-up-fixture";
    private const string MarkerValue = "Isolated offline Claude follow-up fixture";
    private const string ReleaseFileName = ".cab-live-steering-release";
    private const string ObservationFileName = "protocol-observations.jsonl";
    private static readonly HashSet<string> AllowedPrompts = new(StringComparer.Ordinal)
    {
        "LIVE_STEER_QUEUE_1", "LIVE_STEER_QUEUE_2", "LIVE_STEER_STEER_1", "LIVE_STEER_STEER_2",
        "LIVE_STEER_OTHER_USER_1", "LIVE_STEER_OTHER_THREAD_1", "LIVE_STEER_UNKNOWN_1"
    };
    private static readonly SemaphoreSlim OutputGate = new(1, 1);
    private static readonly SemaphoreSlim ObservationGate = new(1, 1);
    private static string _observationsPath = "";
    private static string _releasePath = "";

    public static async Task RunAsync(string[] args)
    {
        if (!TryResolvePaths(out var workspace, out var dataRoot, out var reason))
        {
            await Console.Error.WriteLineAsync(reason);
            Environment.ExitCode = 2;
            return;
        }

        _observationsPath = Path.Combine(dataRoot, ObservationFileName);
        _releasePath = Path.Combine(dataRoot, ReleaseFileName);
        var model = ReadArgument(args, "--model") ?? "fixture-claude";
        var sessionId = ReadArgument(args, "--resume") ?? ReadArgument(args, "--session-id") ?? Guid.NewGuid().ToString("D");
        var initialized = false;
        string? activeMarker = null;
        string? activeUuid = null;
        Task? completion = null;

        while (await Console.In.ReadLineAsync() is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                await Console.Error.WriteLineAsync("Offline Claude follow-up fixture received malformed JSON input.");
                Environment.ExitCode = 2;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "control_request")
                {
                    var request = root.TryGetProperty("request", out var requestElement) ? requestElement : default;
                    if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "initialize")
                    {
                        var requestId = root.TryGetProperty("request_id", out var id) ? id.GetString() : null;
                        if (int.TryParse(Environment.GetEnvironmentVariable("CAB_CLAUDE_STEERING_INIT_DELAY_MS"), out var delayMs))
                            await Task.Delay(Math.Clamp(delayMs, 0, 30000));
                        if (Environment.GetEnvironmentVariable("CAB_CLAUDE_STEERING_INIT_UNSUPPORTED") == "1")
                        {
                            await WriteAsync(new
                            {
                                type = "control_response",
                                response = new { subtype = "error", request_id = requestId, error = "Offline fixture reports Claude live follow-up initialization unsupported." }
                            });
                            return;
                        }
                        await WriteAsync(new
                        {
                            type = "control_response",
                            response = new { subtype = "success", request_id = requestId, response = new { models = new object[] { new { value = model, displayName = "Offline fixture Claude", supportsEffort = true, supportedEffortLevels = new[] { "low", "medium", "high", "max" } } } } }
                        });
                    }
                    continue;
                }

                if (root.TryGetProperty("type", out type) && type.GetString() == "user")
                {
                    if (!initialized)
                    {
                        if (!string.Equals(Path.GetFullPath(Environment.CurrentDirectory), workspace, PathComparison))
                        {
                            await Console.Error.WriteLineAsync("Offline Claude fixture workspace changed after startup.");
                            Environment.ExitCode = 2;
                            return;
                        }
                        initialized = true;
                        sessionId = root.TryGetProperty("session_id", out var inputSession) ? inputSession.GetString() ?? sessionId : sessionId;
                        activeUuid = root.TryGetProperty("uuid", out var firstUuid) ? firstUuid.GetString() : null;
                        var prompt = ExtractText(root);
                        activeMarker = FindAllowedMarker(prompt);
                        if (activeMarker is null)
                        {
                            await Console.Error.WriteLineAsync("Offline Claude fixture requires exactly one whitelisted LIVE_STEER marker.");
                            Environment.ExitCode = 2;
                            return;
                        }

                        await WriteAsync(new { type = "system", subtype = "init", session_id = sessionId, model });
                        await RecordAsync(activeMarker, "turn/start", "started", activeUuid, sessionId);
                        // Claude's native stream echoes the accepted user frame, retaining its identity.
                        await WriteAsync(new { type = "user", uuid = activeUuid, session_id = sessionId, isReplay = true, message = root.TryGetProperty("message", out var message) ? message : default });
                        await WriteAsync(new { type = "assistant", message = new { content = new[] { new { type = "text", text = activeMarker + " fixture turn is waiting for release." } } } });
                        completion = CompleteWhenReleasedAsync(activeMarker, activeUuid, sessionId, model);
                    }
                    else if (activeMarker is not null)
                    {
                        var followUpMarker = FindAllowedMarker(ExtractText(root));
                        if (followUpMarker is null)
                        {
                            await Console.Error.WriteLineAsync("Offline Claude fixture accepts only whitelisted follow-up markers.");
                            Environment.ExitCode = 2;
                            return;
                        }

                        var outcome = activeMarker == "LIVE_STEER_UNKNOWN_1" ? "delivery-unknown" :
                            activeMarker == "LIVE_STEER_STEER_1" && followUpMarker == "LIVE_STEER_STEER_2" ? "accepted" : "unsupported";
                        await RecordAsync(activeMarker, "turn/steer", outcome, root.TryGetProperty("uuid", out var followUuid) ? followUuid.GetString() : null, sessionId, followUpMarker);
                        if (outcome != "delivery-unknown")
                            await WriteAsync(new { type = "user", uuid = root.TryGetProperty("uuid", out followUuid) ? followUuid.GetString() : null, session_id = sessionId, isReplay = true, message = root.TryGetProperty("message", out var followMessage) ? followMessage : default });
                    }
                }
            }
        }

        // The host can close stdin as soon as it sees the final result. Drain work already accepted.
        if (completion is not null) await completion;
    }

    public static async Task RunOneShotAsync(string[] args)
    {
        if (!TryResolvePaths(out var workspace, out var dataRoot, out var reason))
        {
            await Console.Error.WriteLineAsync(reason);
            Environment.ExitCode = 2;
            return;
        }

        if (!string.Equals(Path.GetFullPath(Environment.CurrentDirectory), workspace, PathComparison))
        {
            await Console.Error.WriteLineAsync("Offline Claude fixture workspace changed after startup.");
            Environment.ExitCode = 2;
            return;
        }

        _observationsPath = Path.Combine(dataRoot, ObservationFileName);
        _releasePath = Path.Combine(dataRoot, ReleaseFileName);
        var prompt = await Console.In.ReadToEndAsync();
        var marker = FindAllowedMarker(prompt);
        if (marker is null)
        {
            await Console.Error.WriteLineAsync("Offline Claude fixture requires exactly one whitelisted LIVE_STEER marker.");
            Environment.ExitCode = 2;
            return;
        }

        var model = ReadArgument(args, "--model") ?? "fixture-claude";
        var sessionId = ReadArgument(args, "--resume") ?? ReadArgument(args, "--session-id") ?? Guid.NewGuid().ToString("D");
        await RecordAsync(marker, "turn/start", "started", null, sessionId);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (!File.Exists(_releasePath) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(40);
        if (!File.Exists(_releasePath))
        {
            await Console.Error.WriteLineAsync("Offline Claude follow-up fixture release gate timed out.");
            Environment.ExitCode = 3;
            return;
        }

        await WriteAsync(new { type = "system", subtype = "init", session_id = sessionId, model });
        await WriteAsync(new
        {
            type = "result", subtype = "success", session_id = sessionId,
            result = "LIVE_STEER_FINAL single fixture response",
            usage = new { input_tokens = 100, output_tokens = 20, cache_read_input_tokens = 50, cache_creation_input_tokens = 0 },
            modelUsage = new Dictionary<string, object> { [model] = new { } }
        });
        await RecordAsync(marker, "turn/completed", "completed", null, sessionId);
    }

    private static async Task CompleteWhenReleasedAsync(string marker, string? uuid, string sessionId, string model)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (!File.Exists(_releasePath) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(40);
        if (!File.Exists(_releasePath))
        {
            await Console.Error.WriteLineAsync("Offline Claude follow-up fixture release gate timed out.");
            Environment.ExitCode = 3;
            return;
        }

        await WriteAsync(new
        {
            type = "result", subtype = "success", session_id = sessionId,
            result = "LIVE_STEER_FINAL single fixture response",
            usage = new { input_tokens = 100, output_tokens = 20, cache_read_input_tokens = 50, cache_creation_input_tokens = 0 },
            modelUsage = new Dictionary<string, object> { [model] = new { } }
        });
        await RecordAsync(marker, "turn/completed", "completed", uuid, sessionId);
    }

    private static string ExtractText(JsonElement value)
    {
        var parts = new List<string>();
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) { parts.Add(element.GetString() ?? ""); return; }
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    if (property.Name is "text" or "content" or "prompt" or "message") Visit(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
        Visit(value);
        return string.Join("\n", parts);
    }

    private static string? FindAllowedMarker(string prompt)
    {
        var markers = Regex.Matches(prompt, @"\bLIVE_STEER_[A-Z0-9_]+\b", RegexOptions.CultureInvariant).Cast<Match>()
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        return markers.Length == 1 && AllowedPrompts.Contains(markers[0]) ? markers[0] : null;
    }

    private static async Task RecordAsync(string scenario, string method, string outcome, string? uuid, string sessionId, string? inputMarker = null)
    {
        await ObservationGate.WaitAsync();
        try
        {
            var record = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, scenario, method, threadId = sessionId, turnId = sessionId, inputMarker, uuid, outcome });
            await File.AppendAllTextAsync(_observationsPath, record + Environment.NewLine);
        }
        finally { ObservationGate.Release(); }
    }

    private static async Task WriteAsync(object value)
    {
        await OutputGate.WaitAsync();
        try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
        finally { OutputGate.Release(); }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool TryResolvePaths(out string workspace, out string dataRoot, out string error)
    {
        workspace = Path.GetFullPath(Environment.CurrentDirectory);
        dataRoot = "";
        error = "Offline Claude follow-up fixture requires its marked isolated workspace.";
        var data = Directory.GetParent(workspace);
        var runRoot = data?.Parent;
        var artifacts = runRoot?.Parent;
        var desktop = artifacts?.Parent;
        var marker = data is null ? null : Path.Combine(data.FullName, MarkerName);
        var workspaceMarker = Path.Combine(workspace, ".cab-live-steering-workspace");
        var expectedObservations = data is null ? null : Path.Combine(data.FullName, ObservationFileName);
        var expectedGate = data is null ? null : Path.Combine(data.FullName, ReleaseFileName);
        var observations = Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_OBSERVATIONS_FILE");
        if (data is null || runRoot is null || artifacts is null || desktop is null || marker is null ||
            !string.Equals(Path.GetFileName(workspace), "workspace", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(data.FullName), "data", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(runRoot.FullName), "D", out _) ||
            !string.Equals(Path.GetFileName(artifacts.FullName), "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(desktop.FullName), "desktop", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(workspace) || !File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker).Trim(), MarkerValue, StringComparison.Ordinal) ||
            !File.Exists(workspaceMarker) ||
            !string.Equals(File.ReadAllText(workspaceMarker).Trim(), "offline smoke workspace", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(observations ?? ""), expectedObservations, PathComparison) ||
            !string.Equals(Path.GetFullPath(Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_RELEASE_FILE") ?? ""), expectedGate, PathComparison) ||
            !File.Exists(expectedObservations))
            return false;
        dataRoot = data.FullName;
        return true;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
