using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

/// <summary>Minimal ACP client for Kimi. The bridge intentionally advertises no host tools.</summary>
internal static class KimiAcpRunner
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(10);
    private const int MaxLine = 1_048_576;

    public static async Task<AgentRunResult> RunAsync(BridgeSettings settings, string workspace, string prompt,
        AppServerRunOptions? options, IProgress<AgentRunProgress>? progress, CancellationToken cancellationToken)
    {
        try { _ = AgentProfile.BuildInvocation(settings); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return Failed(ex.Message); }
        var authorized = settings.AuthorizedCliSessionId;
        if (authorized is not null && (!Guid.TryParseExact(authorized, "D", out var parsed) || parsed.ToString("D") != authorized.ToLowerInvariant()))
            return Failed(CoreText.Get("KimiAcpSessionInvalid"));

        var start = new ProcessStartInfo { FileName = AgentProfile.ResolveEffectiveExecutable(settings), WorkingDirectory = workspace,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), CreateNoWindow = true };
        foreach (var arg in AgentProfile.BuildInvocation(settings).Arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var sw = Stopwatch.StartNew();
        try { if (!process.Start()) return Failed(CoreText.Get("KimiAcpStartFailed")); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return Failed(CoreText.Get("KimiAcpStartFailed")); }
        var startMilliseconds = sw.Elapsed.TotalMilliseconds;
        var stderrDrain = DrainErrorAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TurnTimeout);
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
        var rpc = new Rpc(process.StandardInput, process.StandardOutput);
        try
        {
            // Recovery must know the native worker before any prompt can reference staged files.
            IncomingAttachmentRecovery.TrackProcess(process);
            var init = await rpc.RequestAsync("initialize", new { protocolVersion = 1, clientInfo = new { name = "chat-agent-bridge", version = "1" },
                clientCapabilities = new { } }, timeout.Token).ConfigureAwait(false);
            if (!TryIdentity(init, out _)) return Failed(CoreText.Get("KimiAcpIdentityInvalid"));
            var hasLoad = init.GetProperty("agentCapabilities").TryGetProperty("loadSession", out var load) && load.ValueKind == JsonValueKind.True;
            JsonElement session;
            if (authorized is not null)
            {
                if (!hasLoad) return Failed(CoreText.Get("KimiAcpLoadUnsupported"));
                session = await rpc.RequestAsync("session/load", new { sessionId = "session_" + authorized.ToLowerInvariant(), cwd = workspace, mcpServers = Array.Empty<object>() }, timeout.Token).ConfigureAwait(false);
            }
            else session = await rpc.RequestAsync("session/new", new { cwd = workspace, mcpServers = Array.Empty<object>() }, timeout.Token).ConfigureAwait(false);
            var sid = authorized is null ? session.GetProperty("sessionId").GetString() : "session_" + authorized.ToLowerInvariant();
            if (!TrySessionId(sid, out var id) || authorized is not null && !string.Equals(id, authorized, StringComparison.OrdinalIgnoreCase))
                return Failed(CoreText.Get("KimiAcpSessionMismatch"));
            if (!session.TryGetProperty("modes", out var sessionModes) || !sessionModes.TryGetProperty("availableModes", out var available) || available.ValueKind != JsonValueKind.Array)
                return Failed(CoreText.Get("KimiAcpModesMissing"), id);
            string? selectedMode = null;
            foreach (var mode in available.EnumerateArray())
                if (mode.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String &&
                    mid.GetString() == (settings.AgentPermissionMode == "full-access" ? "yolo" : "default")) selectedMode = mid.GetString();
            if (selectedMode is null) return Failed(CoreText.Get("KimiAcpModeUnsupported"), id);
            await rpc.RequestAsync("session/set_mode", new { sessionId = sid, modeId = selectedMode }, timeout.Token).ConfigureAwait(false);
            if (AgentProfile.NormalizeModel(settings.AgentModel) is { } model)
                await rpc.RequestAsync("session/set_model", new { sessionId = sid, modelId = model }, timeout.Token).ConfigureAwait(false);
            rpc.BeginPrompt(sid!, id, progress, options?.ApprovalHandler, settings.AgentPermissionMode == "full-access");
            var result = await rpc.RequestAsync("session/prompt", new { sessionId = sid, prompt = new object[] { new { type = "text", text = prompt } } }, timeout.Token).ConfigureAwait(false);
            if (!result.TryGetProperty("stopReason", out var reason) || reason.ValueKind != JsonValueKind.String || reason.GetString() != "end_turn")
                return Failed(CoreText.Get("KimiAcpIncomplete"), id);
            return new AgentRunResult(true, 0, rpc.Text, "") { CliSession = new CliSessionOutput("kimi", id, rpc.Text, true, false),
                Metrics = new AgentExecutionMetrics { ProcessStartMilliseconds = startMilliseconds } };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failed(CoreText.Get("KimiAcpTimeout")); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or IOException or KeyNotFoundException)
        { cancellationToken.ThrowIfCancellationRequested(); return Failed(CoreText.Get("KimiAcpProtocolFailed")); }
        finally { await StopAsync(process, stderrDrain).ConfigureAwait(false); }
    }

    public static async Task<(string Version, bool Recognized)> ProbeAsync(string executable, string workspace, IReadOnlyList<string> prefix, TimeSpan timeout, CancellationToken token)
    {
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = workspace, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false), CreateNoWindow = true };
        foreach (var arg in prefix) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("acp");
        using var process = Process.Start(start) ?? throw new InvalidOperationException(CoreText.Get("KimiAcpStartFailed"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(() => { try { process.Kill(true); } catch { } });
        var stderrDrain = DrainErrorAsync(process.StandardError);
        try
        {
            var rpc = new Rpc(process.StandardInput, process.StandardOutput);
            var response = await rpc.RequestAsync("initialize", new { protocolVersion = 1, clientInfo = new { name = "chat-agent-bridge-probe", version = "1" }, clientCapabilities = new { } }, cts.Token).ConfigureAwait(false);
            var recognized = TryIdentity(response, out var version);
            return (version, recognized);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException(CoreText.Get("KimiAcpTimeout")); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or IOException or KeyNotFoundException)
        { throw new InvalidOperationException(CoreText.Get("KimiAcpProtocolFailed")); }
        finally { await StopAsync(process, stderrDrain).ConfigureAwait(false); }
    }

    private static bool TrySessionId(string? value, out string id)
    {
        id = "";
        if (value is null || !value.StartsWith("session_", StringComparison.Ordinal) || !Guid.TryParseExact(value[8..], "D", out var guid)) return false;
        id = guid.ToString("D"); return true;
    }
    private static AgentRunResult Failed(string error, string? id = null) => new(false, -1, "", error) { CliSession = id is null ? null : new CliSessionOutput("kimi", id, null, false, true) };
    private static bool TryIdentity(JsonElement response, out string version)
    {
        version = "";
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("protocolVersion", out var protocol) ||
            !protocol.TryGetInt32(out var number) || number != 1 || !response.TryGetProperty("agentInfo", out var info) ||
            info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || !string.Equals(name.GetString(), "Kimi Code CLI", StringComparison.OrdinalIgnoreCase) ||
            !info.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.String) return false;
        version = value.GetString() ?? "";
        return version.Length is > 0 and <= 256 && !version.Any(char.IsControl);
    }
    private static async Task DrainErrorAsync(StreamReader reader)
    {
        // Discard diagnostics in fixed-size chunks: even a newline-free stderr stream stays bounded.
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private static async Task StopAsync(Process process, Task stderrDrain)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private sealed class Rpc(StreamWriter input, StreamReader output)
    {
        private int _id;
        private string? _session;
        private IProgress<AgentRunProgress>? _progress;
        private Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? _approval;
        private bool _fullAccess;
        private string? _sessionUuid;
        private readonly StringBuilder _text = new();
        private readonly char[] _buffer = new char[4096];
        private int _position, _count;
        public string Text => _text.ToString();
        public void BeginPrompt(string session, string uuid, IProgress<AgentRunProgress>? progress, Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? approval, bool fullAccess)
        { _session = session; _sessionUuid = uuid; _progress = progress; _approval = approval; _fullAccess = fullAccess; }
        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref _id);
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, token).ConfigureAwait(false);
            while (true)
            {
                var line = await ReadLineBoundedAsync(output, token).ConfigureAwait(false) ?? throw new EndOfStreamException("Kimi ACP closed stdout.");
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var protocol) ||
                    protocol.ValueKind != JsonValueKind.String || protocol.GetString() != "2.0") throw new InvalidDataException("Malformed ACP message.");
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.TryGetInt32(out var responseId) && responseId == id)
                {
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Kimi ACP request failed.");
                    if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Malformed ACP result.");
                    return result.Clone();
                }
                if (root.TryGetProperty("method", out var meth) && meth.ValueKind == JsonValueKind.String)
                {
                    var methodName = meth.GetString()!; var ps = root.TryGetProperty("params", out var p) ? p : default;
                    if (_session is not null && methodName == "session/update")
                    {
                        if (ps.ValueKind != JsonValueKind.Object || !ps.TryGetProperty("sessionId", out var updateSession) || updateSession.ValueKind != JsonValueKind.String || updateSession.GetString() != _session)
                            throw new InvalidDataException("Kimi update used an unexpected session id.");
                        if (!ps.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object ||
                            !update.TryGetProperty("sessionUpdate", out var typ) || typ.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("Malformed ACP session update.");
                        if (typ.GetString() == "agent_message_chunk")
                        {
                            if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
                                !content.TryGetProperty("type", out var contentType) || contentType.ValueKind != JsonValueKind.String)
                                throw new InvalidDataException("Malformed ACP text content.");
                            // Non-text blocks are not rendered, including model reasoning and binary data.
                            if (contentType.GetString() != "text") continue;
                            if (!content.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String)
                                throw new InvalidDataException("Malformed ACP text content.");
                            var piece = txt.GetString() ?? "";
                            if (_text.Length + piece.Length > 1_000_000) throw new InvalidDataException("ACP output exceeded the limit.");
                            _text.Append(piece); _progress?.Report(new("stdout", piece));
                        }
                        continue;
                    }
                    if (methodName == "session/request_permission")
                    {
                        var requestId = root.TryGetProperty("id", out var qid) ? qid.Clone() : default;
                        var sid = ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("sessionId", out var qsid) ? qsid.GetString() : null;
                        if (_session is null || sid != _session || requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new InvalidDataException("Kimi permission request used an unexpected session id.");
                        var tool = ps.TryGetProperty("toolCall", out var call) ? call : default;
                        var title = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Tool permission" : "Tool permission";
                        if (title.Length > 512) title = title[..512] + "…";
                        var raw = tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("rawInput", out var rawInput) ? rawInput.GetRawText() : null;
                        if (raw?.Length > 4096) raw = raw[..4096] + "…";
                        var decision = _fullAccess ? AppServerDecision.Accept : _approval is null ? AppServerDecision.Decline :
                            await _approval(new AppServerApproval(methodName, _sessionUuid!, "", tool.TryGetProperty("toolCallId", out var tcid) ? tcid.GetString() ?? "" : "", title + (raw is null ? "" : Environment.NewLine + raw), null, null), token).ConfigureAwait(false);
                        var selected = false;
                        string? optionId = null;
                        if (decision == AppServerDecision.Accept && ps.TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array)
                            foreach (var choice in choices.EnumerateArray())
                                if (choice.TryGetProperty("kind", out var kind) && kind.GetString() == "allow_once" && choice.TryGetProperty("optionId", out var oid) && oid.GetString() == "approve_once") { selected = true; optionId = oid.GetString(); break; }
                        await WriteAsync(new { jsonrpc = "2.0", id = requestId, result = new { outcome = selected ? new { outcome = "selected", optionId } : new { outcome = "cancelled", optionId = (string?)null } } }, token).ConfigureAwait(false);
                        if (!selected) throw new InvalidOperationException("Kimi permission request was declined or has no safe allow-once option.");
                        continue;
                    }
                    if (root.TryGetProperty("id", out var reverseId)) await WriteAsync(new { jsonrpc = "2.0", id = reverseId.Clone(), error = new { code = -32601, message = "Client method is unavailable." } }, token).ConfigureAwait(false);
                    continue;
                }
            }
        }
        private async Task WriteAsync<T>(T message, CancellationToken token)
        { var json = JsonSerializer.Serialize(message); if (json.Length > MaxLine) throw new InvalidDataException("ACP request exceeded the limit."); await input.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); await input.FlushAsync(token).ConfigureAwait(false); }
        private async Task<string?> ReadLineBoundedAsync(StreamReader reader, CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }
                var character = _buffer[_position++];
                if (character == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= MaxLine) throw new InvalidDataException("Kimi ACP message exceeded the limit.");
                line.Append(character);
            }
        }
    }
}
