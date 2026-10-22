using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

/// <summary>Guarded, offline App Server fixture for queued and steered follow-up smoke tests.</summary>
internal static class AppServerFollowUpFixture
{
    private const string MarkerName = ".cab-live-steering-fixture";
    private const string MarkerValue = "Isolated offline AppServer follow-up fixture";
    private const string ReleaseFileName = ".cab-live-steering-release";
    private const string ObservationFileName = "protocol-observations.jsonl";
    private static readonly HashSet<string> AllowedPrompts = new(StringComparer.Ordinal)
    {
        "LIVE_STEER_QUEUE_1", "LIVE_STEER_QUEUE_2", "LIVE_STEER_STEER_1",
        "LIVE_STEER_STEER_2",
        "LIVE_STEER_OTHER_USER_1", "LIVE_STEER_OTHER_THREAD_1",
        "LIVE_STEER_UNSUPPORTED_1", "LIVE_STEER_UNKNOWN_1"
    };
    private static readonly SemaphoreSlim OutputGate = new(1, 1);
    private static readonly SemaphoreSlim ObservationGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, TurnState> ActiveTurns = new(StringComparer.Ordinal);
    private static string _observationsPath = "";
    private static string _releasePath = "";
    private static string _threadId = "";
    private static int _turnCount;

    public static async Task RunAsync()
    {
        if (!TryResolveFixturePaths(out var workspace, out var dataRoot, out var reason))
        {
            await Console.Error.WriteLineAsync(reason);
            Environment.ExitCode = 2;
            return;
        }

        _observationsPath = Path.Combine(dataRoot, ObservationFileName);
        _releasePath = Path.Combine(dataRoot, ReleaseFileName);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                await Console.Error.WriteLineAsync("Offline follow-up fixture received malformed JSON-RPC input.");
                Environment.ExitCode = 2;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var methodElement)) continue;
                var method = methodElement.GetString() ?? "";
                var hasId = root.TryGetProperty("id", out var id);
                var parameters = root.TryGetProperty("params", out var p) ? p : default;
                switch (method)
                {
                    case "initialize":
                        if (hasId) await RespondAsync(new { id, result = new { userAgent = "offline-follow-up-fixture", platformFamily = "windows", platformOs = "windows" } });
                        break;
                    case "initialized":
                        break;
                    case "model/list":
                        if (hasId) await RespondAsync(new { id, result = new { data = Array.Empty<object>(), nextCursor = (string?)null } });
                        break;
                    case "thread/start":
                        _threadId = Guid.NewGuid().ToString("D");
                        if (hasId) await RespondAsync(new { id, result = new { thread = new { id = _threadId }, cwd = parameters.GetProperty("cwd").GetString() } });
                        break;
                    case "thread/resume":
                        _threadId = parameters.GetProperty("threadId").GetString() ?? "";
                        if (hasId) await RespondAsync(new { id, result = new { thread = new { id = _threadId }, cwd = parameters.GetProperty("cwd").GetString() } });
                        break;
                    case "turn/start":
                        await StartTurnAsync(id, hasId, parameters, workspace);
                        break;
                    case "turn/steer":
                        await SteerAsync(id, hasId, parameters);
                        break;
                    case "turn/interrupt":
                        await InterruptAsync(id, hasId, parameters);
                        break;
                    default:
                        if (hasId) await ErrorAsync(id, -32601, "Unsupported fixture method");
                        break;
                }
            }
        }
    }

    private static async Task StartTurnAsync(JsonElement id, bool hasId, JsonElement parameters, string workspace)
    {
        var input = parameters.GetProperty("input");
        var prompt = string.Join("\n", input.EnumerateArray().Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString() ?? ""));
        var markers = Regex.Matches(prompt, @"\bLIVE_STEER_[A-Z0-9_]+\b", RegexOptions.CultureInvariant).Cast<Match>()
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (markers.Length != 1 || !AllowedPrompts.Contains(markers[0]))
        {
            await Console.Error.WriteLineAsync("Offline follow-up fixture requires exactly one whitelisted LIVE_STEER prompt marker.");
            Environment.ExitCode = 2;
            return;
        }

        if (!string.Equals(Path.GetFullPath(Environment.CurrentDirectory), workspace, PathComparison))
        {
            await Console.Error.WriteLineAsync("Offline follow-up fixture workspace changed after startup.");
            Environment.ExitCode = 2;
            return;
        }

        var threadId = parameters.TryGetProperty("threadId", out var suppliedThread) ? suppliedThread.GetString() ?? _threadId : _threadId;
        var turnId = "fixture-live-turn-" + (++_turnCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var state = new TurnState(threadId, turnId, markers[0]);
        ActiveTurns[threadId] = state;
        await RecordAsync("turn/start", state, "started");
        if (hasId) await RespondAsync(new { id, result = new { turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>() } } });
        await NotifyAsync(new { method = "turn/started", @params = new { threadId, turn = new { id = turnId, status = "inProgress" } } });
        await NotifyAsync(new { method = "item/completed", @params = new { threadId, turnId, item = new { id = turnId + "-progress", type = "agentMessage", text = markers[0] + " fixture turn is waiting for release." } } });
        _ = CompleteWhenReleasedAsync(state);
    }

    private static async Task SteerAsync(JsonElement id, bool hasId, JsonElement parameters)
    {
        var threadId = parameters.TryGetProperty("threadId", out var thread) ? thread.GetString() ?? "" : "";
        var expectedTurn = parameters.TryGetProperty("expectedTurnId", out var expected) ? expected.GetString() ?? "" : "";
        var state = ActiveTurns.GetValueOrDefault(threadId);
        if (state is null || state.Completed || !string.Equals(expectedTurn, state.TurnId, StringComparison.Ordinal))
        {
            if (state is not null) await RecordAsync("turn/steer", state, "rejected-turn-mismatch");
            if (hasId) await ErrorAsync(id, -32601, "expectedTurnId does not match the active turn");
            return;
        }

        state.SteerRequests++;
        var steerInput = parameters.GetProperty("input");
        var steerText = string.Join("\n", steerInput.EnumerateArray().Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString() ?? ""));
        var steerMarkers = Regex.Matches(steerText, @"\bLIVE_STEER_[A-Z0-9_]+\b", RegexOptions.CultureInvariant).Cast<Match>()
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (steerMarkers.Length != 1 || !AllowedPrompts.Contains(steerMarkers[0]))
        {
            await RecordAsync("turn/steer", state, "rejected-unrecognized-input");
            if (hasId) await ErrorAsync(id, -32601, "Fixture accepts only whitelisted LIVE_STEER input markers");
            return;
        }

        var behavior = state.PromptMarker switch
        {
            "LIVE_STEER_UNKNOWN_1" => "delivery-unknown",
            "LIVE_STEER_UNSUPPORTED_1" => "unsupported",
            "LIVE_STEER_STEER_1" => "accepted",
            _ => "unsupported"
        };
        await RecordAsync("turn/steer", state, behavior, steerMarkers[0]);
        if (behavior == "delivery-unknown") return;
        if (behavior == "unsupported")
        {
            if (hasId) await ErrorAsync(id, -32601, "Steering is unsupported for this fixture scenario");
            return;
        }

        state.AcceptedSteers++;
        if (hasId) await RespondAsync(new { id, result = new { turnId = state.TurnId } });
    }

    private static async Task InterruptAsync(JsonElement id, bool hasId, JsonElement parameters)
    {
        var threadId = parameters.TryGetProperty("threadId", out var thread) ? thread.GetString() ?? "" : "";
        var expectedTurn = parameters.TryGetProperty("turnId", out var turn) ? turn.GetString() ?? "" : "";
        var state = ActiveTurns.GetValueOrDefault(threadId);
        if (state is not null && !state.Completed && string.Equals(expectedTurn, state.TurnId, StringComparison.Ordinal))
        {
            state.Completed = true;
            ActiveTurns.TryRemove(threadId, out _);
            await RecordAsync("turn/interrupt", state, "interrupted");
            if (hasId) await RespondAsync(new { id, result = new { } });
            await CompleteAsync(state, "interrupted", "LIVE_STEER_FINAL fixture turn interrupted");
            return;
        }
        if (hasId) await ErrorAsync(id, -32601, "No matching active fixture turn");
    }

    private static async Task CompleteWhenReleasedAsync(TurnState state)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (!state.Completed && !File.Exists(_releasePath) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(40);
        if (state.Completed) return;
        if (!File.Exists(_releasePath))
        {
            await Console.Error.WriteLineAsync("Offline follow-up fixture release gate timed out.");
            Environment.ExitCode = 3;
            return;
        }

        state.Completed = true;
        ActiveTurns.TryRemove(state.ThreadId, out _);
        await RecordAsync("turn/completed", state, "completed");
        await CompleteAsync(state, "completed", "LIVE_STEER_FINAL single fixture response");
    }

    private static async Task CompleteAsync(TurnState state, string status, string text)
    {
        var item = new { id = state.TurnId + "-final", type = "agentMessage", text };
        await NotifyAsync(new { method = "item/completed", @params = new { threadId = state.ThreadId, turnId = state.TurnId, item } });
        await NotifyAsync(new { method = "turn/completed", @params = new { threadId = state.ThreadId, turn = new { id = state.TurnId, status, items = new[] { item } } } });
    }

    private static async Task RecordAsync(string method, TurnState state, string outcome, string? inputMarker = null)
    {
        await ObservationGate.WaitAsync();
        try
        {
            var record = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                scenario = state.PromptMarker,
                method,
                threadId = state.ThreadId,
                turnId = state.TurnId,
                requestCount = method == "turn/steer" ? (int?)state.SteerRequests : null,
                acceptedCount = state.AcceptedSteers,
                inputMarker,
                outcome
            });
            await File.AppendAllTextAsync(_observationsPath, record + Environment.NewLine);
        }
        finally { ObservationGate.Release(); }
    }

    private static async Task RespondAsync(object response) => await WriteAsync(response);
    private static async Task NotifyAsync(object notification) => await WriteAsync(notification);
    private static async Task ErrorAsync(JsonElement id, int code, string message)
        => await WriteAsync(new { id, error = new { code, message } });

    private static async Task WriteAsync(object value)
    {
        await OutputGate.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
            await Console.Out.FlushAsync();
        }
        finally { OutputGate.Release(); }
    }

    private static bool TryResolveFixturePaths(out string workspace, out string dataRoot, out string error)
    {
        workspace = Path.GetFullPath(Environment.CurrentDirectory);
        dataRoot = "";
        error = "Offline follow-up fixture requires its marked isolated workspace.";
        var data = Directory.GetParent(workspace);
        var runRoot = data?.Parent;
        var artifacts = runRoot?.Parent;
        var desktop = artifacts?.Parent;
        var marker = data is null ? null : Path.Combine(data.FullName, MarkerName);
        if (data is null || runRoot is null || artifacts is null || desktop is null || marker is null ||
            !string.Equals(Path.GetFileName(workspace), "workspace", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(data.FullName), "data", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(artifacts.FullName), "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(desktop.FullName), "desktop", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileName(runRoot.FullName), out _) || !Directory.Exists(workspace) ||
            !File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim(), MarkerValue, StringComparison.Ordinal))
            return false;

        var release = Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_RELEASE_FILE");
        var observations = Environment.GetEnvironmentVariable("CAB_LIVE_STEERING_OBSERVATIONS_FILE");
        if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(observations) ||
            !SamePath(release, Path.Combine(data.FullName, ReleaseFileName)) ||
            !SamePath(observations, Path.Combine(data.FullName, ObservationFileName)))
        {
            error = "Offline follow-up fixture requires release and observation paths inside its isolated data directory.";
            return false;
        }

        dataRoot = data.FullName;
        return true;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class TurnState(string threadId, string turnId, string promptMarker)
    {
        public string ThreadId { get; } = threadId;
        public string TurnId { get; } = turnId;
        public string PromptMarker { get; } = promptMarker;
        public int SteerRequests { get; set; }
        public int AcceptedSteers { get; set; }
        public volatile bool Completed;
    }
}
