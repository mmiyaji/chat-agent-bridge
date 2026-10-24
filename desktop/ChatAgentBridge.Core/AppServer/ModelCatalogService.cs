using System.Text.Json;
using System.Diagnostics;
using System.Text;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core;

/// <summary>A model that can be selected for an agent run.</summary>
public sealed record ModelOption(string Id, string DisplayName, string Description, bool IsDefault)
{
    public string SelectionLabel => $"{DisplayName} ({Id})";
    public IReadOnlyList<ReasoningEffortOption> SupportedReasoningEfforts { get; init; } = Array.Empty<ReasoningEffortOption>();
    public string? DefaultReasoningEffort { get; init; }
}

public sealed record ReasoningEffortOption(string Id, string Description)
{
    public string SelectionLabel => string.IsNullOrWhiteSpace(Description) ? Id : $"{Id} · {Description}";
}

public sealed class ModelCatalogException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Retrieves current models exposed by the configured native agent CLI.</summary>
public sealed class ModelCatalogService
{
    private const int PageSize = 100;
    private const int MaxPages = 20;
    private const int MaxModels = PageSize * MaxPages;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly IAppServerProcessFactory _processFactory;
    private readonly TimeSpan _timeout;

    public ModelCatalogService(IAppServerProcessFactory? processFactory = null, TimeSpan? timeout = null)
    {
        _processFactory = processFactory ?? new CodexAppServerProcessFactory();
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<IReadOnlyList<ModelOption>> GetModelsAsync(BridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        string provider;
        try { provider = AgentProfile.ResolveProvider(settings); }
        catch (ArgumentException ex) { throw new ModelCatalogException(ex.Message, ex); }
        if (provider == AgentProfile.Claude)
            return await GetClaudeModelsAsync(settings, cancellationToken).ConfigureAwait(false);
        if (provider == AgentProfile.Antigravity)
            return await GetAntigravityModelsAsync(settings, cancellationToken).ConfigureAwait(false);
        if (provider != AgentProfile.Codex)
            throw new ModelCatalogException("Model discovery is available only for supported agent providers.");
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ModelCatalogException("A workspace directory is required to discover Codex models.");
        var workspace = Path.GetFullPath(settings.WorkspaceRoot);
        if (!Directory.Exists(workspace)) throw new ModelCatalogException("The configured workspace directory does not exist.");
        if (string.IsNullOrWhiteSpace(settings.AgentExecutable))
            throw new ModelCatalogException("A Codex executable is required to discover models.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        IAppServerProcess? process = null;
        CancellationTokenRegistration killOnCancellation = default;
        try
        {
            process = await _processFactory.StartAsync(settings.AgentExecutable, workspace, deadline.Token).ConfigureAwait(false);
            killOnCancellation = deadline.Token.Register(process.KillTree);
            await InitializeAsync(process.Client, deadline.Token).ConfigureAwait(false);
            var models = new List<ModelOption>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var parameters = new Dictionary<string, object?> { ["limit"] = PageSize, ["includeHidden"] = false };
                if (cursor is not null) parameters["cursor"] = cursor;
                var response = await process.Client.RequestAsync("model/list", parameters, deadline.Token).ConfigureAwait(false);
                var pageModels = ParsePage(response, out var nextCursor);
                foreach (var model in pageModels)
                {
                    if (model.Id.Length == 0 || !seenIds.Add(model.Id)) continue;
                    if (models.Count == MaxModels) throw new ModelCatalogException("Codex returned more models than the catalog limit.");
                    models.Add(model);
                }
                if (string.IsNullOrEmpty(nextCursor)) return models;
                if (string.Equals(cursor, nextCursor, StringComparison.Ordinal))
                    throw new ModelCatalogException("Codex returned a repeated model catalog cursor.");
                cursor = nextCursor;
            }
            throw new ModelCatalogException($"Codex model catalog exceeded the {MaxPages}-page limit.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            process?.KillTree();
            throw new TimeoutException($"Codex model discovery exceeded its {_timeout.TotalSeconds:0}-second time limit.");
        }
        catch (OperationCanceledException)
        {
            process?.KillTree();
            throw;
        }
        catch (ModelCatalogException) { process?.KillTree(); throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException)
        {
            process?.KillTree();
            throw new ModelCatalogException("Could not retrieve the Codex model catalog. Check the Codex CLI and its sign-in status.");
        }
        finally
        {
            killOnCancellation.Dispose();
            if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task InitializeAsync(IAppServerRpcClient client, CancellationToken cancellationToken)
    {
        await client.RequestAsync("initialize", new
        {
            clientInfo = new { name = "chat-agent-bridge", title = "Chat Agent Bridge", version = "0.1.0" },
            capabilities = new { experimentalApi = true }
        }, cancellationToken).ConfigureAwait(false);
        await client.NotifyAsync("initialized", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ModelOption>> GetClaudeModelsAsync(BridgeSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ModelCatalogException("A workspace directory is required to discover Claude Code models.");
        var workspace = Path.GetFullPath(settings.WorkspaceRoot);
        if (!Directory.Exists(workspace)) throw new ModelCatalogException("The configured workspace directory does not exist.");
        var executable = string.IsNullOrWhiteSpace(settings.AgentExecutable) || IsCodexExecutable(settings.AgentExecutable)
            ? "claude" : settings.AgentExecutable;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        Process? process = null;
        Task? stderrDrain = null;
        CancellationTokenRegistration killOnCancellation = default;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--no-session-persistence", "--tools", "", "--strict-mcp-config",
                "--mcp-config", "{\"mcpServers\":{}}", "--settings", "{\"disableAllHooks\":true}"
            }) startInfo.ArgumentList.Add(argument);
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new ModelCatalogException("Claude Code CLI did not start.");
            killOnCancellation = deadline.Token.Register(static state => Kill((Process?)state), process);
            stderrDrain = DrainErrorAsync(process.StandardError);
            const string requestId = "chat-agent-bridge-model-catalog";
            var request = JsonSerializer.Serialize(new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "initialize", hooks = new { } }
            });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            // Keep stdin open while the CLI responds. No user message or turn is ever sent.
            var lineReader = new BoundedLineReader(process.StandardOutput);
            var totalCharacters = 0;
            while (true)
            {
                var line = await lineReader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null) throw new ModelCatalogException("Claude Code CLI closed before returning its model catalog.");
                totalCharacters += line.Length;
                if (totalCharacters > 2 * 1024 * 1024)
                    throw new ModelCatalogException("Claude Code returned an oversized model catalog response.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (String(root, "type") != "control_response") continue;
                var response = root.TryGetProperty("response", out var responseValue) ? responseValue : default;
                if (String(response, "subtype") != "success")
                    throw new ModelCatalogException("Claude Code could not initialize model discovery. Check the Claude Code CLI and its sign-in status.");
                if (String(response, "request_id") != requestId)
                    throw new ModelCatalogException("Claude Code returned a mismatched model catalog response.");
                var payload = response.TryGetProperty("response", out var payloadValue) ? payloadValue : default;
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("models", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new ModelCatalogException("Claude Code returned an invalid model catalog.");
                var models = new List<ModelOption>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows.EnumerateArray())
                {
                    // Parse and retain only model fields; the enclosing response can include account details.
                    var rawId = String(row, "value");
                    if (string.IsNullOrWhiteSpace(rawId)) continue;
                    string id;
                    try { id = AgentProfile.NormalizeModel(rawId)!; }
                    catch (ArgumentException) { continue; }
                    if (!seen.Add(id)) continue;
                    var name = String(row, "displayName");
                    models.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : Truncate(name, 256)!,
                        Truncate(String(row, "description"), 2048) ?? "", id.Equals("default", StringComparison.OrdinalIgnoreCase))
                    {
                        SupportedReasoningEfforts = row.TryGetProperty("supportsEffort", out var supportsEffort) && supportsEffort.ValueKind == JsonValueKind.False
                            ? Array.Empty<ReasoningEffortOption>() : ParseClaudeEfforts(row),
                        DefaultReasoningEffort = null
                    });
                    if (models.Count >= MaxModels) break;
                }
                return models;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"Claude Code model discovery exceeded its {_timeout.TotalSeconds:0}-second time limit.");
        }
        catch (OperationCanceledException) { Kill(process); throw; }
        catch (ModelCatalogException) { Kill(process); throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            Kill(process);
            // Never return stderr or raw control payloads; they may include account or credential data.
            throw new ModelCatalogException("Could not retrieve the Claude Code model catalog. Check the CLI and its sign-in status.");
        }
        finally
        {
            if (process is not null)
            {
                killOnCancellation.Dispose();
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (InvalidOperationException) { }
                if (stderrDrain is not null)
                {
                    try { await stderrDrain.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    catch (IOException) { }
                }
                process.Dispose();
            }
            else killOnCancellation.Dispose();
        }
    }

    /// <summary>Runs `agy models`, which lists "id&lt;TAB&gt;label" rows without starting a model turn.</summary>
    private async Task<IReadOnlyList<ModelOption>> GetAntigravityModelsAsync(BridgeSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
            throw new ModelCatalogException("A workspace directory is required to discover Antigravity CLI models.");
        var workspace = Path.GetFullPath(settings.WorkspaceRoot);
        if (!Directory.Exists(workspace)) throw new ModelCatalogException("The configured workspace directory does not exist.");
        var executable = string.IsNullOrWhiteSpace(settings.AgentExecutable) || IsCodexExecutable(settings.AgentExecutable)
            ? "agy" : settings.AgentExecutable;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        Process? process = null;
        CancellationTokenRegistration killOnCancellation = default;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = CodexExecutableResolver.Resolve(executable),
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("models");
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new ModelCatalogException("Antigravity CLI did not start.");
            killOnCancellation = deadline.Token.Register(static state => Kill((Process?)state), process);
            process.StandardInput.Close();
            // stderr carries the "Fetching available models..." banner and any account diagnostics; discard it.
            var stderrDrain = DrainErrorAsync(process.StandardError);
            var output = await ReadBoundedAsync(process.StandardOutput, 256 * 1024, deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            try { await stderrDrain.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            if (process.ExitCode != 0)
                throw new ModelCatalogException("Could not retrieve the Antigravity CLI model catalog. Check the CLI and its sign-in status.");
            return ParseAntigravityModels(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"Antigravity CLI model discovery exceeded its {_timeout.TotalSeconds:0}-second time limit.");
        }
        catch (OperationCanceledException) { Kill(process); throw; }
        catch (ModelCatalogException) { Kill(process); throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Kill(process);
            // Never return stderr; it may include account details.
            throw new ModelCatalogException("Could not retrieve the Antigravity CLI model catalog. Check the CLI and its sign-in status.");
        }
        finally
        {
            killOnCancellation.Dispose();
            if (process is not null)
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    /// <summary>Parses `id&lt;TAB&gt;label` rows. Rows without a tab or with an unusable id (for example banner text) are ignored.</summary>
    internal static IReadOnlyList<ModelOption> ParseAntigravityModels(string output)
    {
        var models = new List<ModelOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var rawId = line[..tab].Trim();
            if (rawId.Length == 0 || rawId.Any(char.IsWhiteSpace)) continue;
            string id;
            try { id = AgentProfile.NormalizeModel(rawId)!; }
            catch (ArgumentException) { continue; }
            if (!seen.Add(id)) continue;
            var label = Truncate(new string(line[(tab + 1)..].Where(character => !char.IsControl(character)).ToArray()).Trim(), 256);
            models.Add(new(id, string.IsNullOrWhiteSpace(label) ? id : label, "", false));
            if (models.Count >= MaxModels) break;
        }
        return models;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > limit) throw new ModelCatalogException("Antigravity CLI returned an oversized model catalog response.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static bool IsCodexExecutable(string executable)
    {
        var leaf = Path.GetFileName(executable.Trim().Trim('"'));
        return new[] { "codex", "codex.exe", "codex.cmd", "codex.ps1" }
            .Contains(leaf, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static async Task DrainErrorAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static IReadOnlyList<ModelOption> ParsePage(JsonElement response, out string? nextCursor)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ModelCatalogException("Codex returned an invalid model catalog response.");
        if (data.GetArrayLength() > PageSize)
            throw new ModelCatalogException("Codex returned more models than the requested page size.");
        var result = new List<ModelOption>();
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = String(row, "model") ?? String(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            try { id = AgentProfile.NormalizeModel(id)!; }
            catch (ArgumentException) { continue; }
            var displayName = Truncate(String(row, "displayName"), 256);
            var description = Truncate(String(row, "description"), 2048);
            var hidden = row.TryGetProperty("hidden", out var hiddenValue) && hiddenValue.ValueKind == JsonValueKind.True;
            if (hidden) continue;
            result.Add(new(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                description ?? "", row.TryGetProperty("isDefault", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.True)
            {
                SupportedReasoningEfforts = ParseCodexEfforts(row),
                DefaultReasoningEffort = NormalizeCatalogEffort(String(row, "defaultReasoningEffort"))
            });
        }
        nextCursor = response.TryGetProperty("nextCursor", out var cursorValue) && cursorValue.ValueKind == JsonValueKind.String
            ? cursorValue.GetString() : null;
        return result;
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static IReadOnlyList<ReasoningEffortOption> ParseCodexEfforts(JsonElement row)
    {
        if (!row.TryGetProperty("supportedReasoningEfforts", out var values) || values.ValueKind != JsonValueKind.Array) return Array.Empty<ReasoningEffortOption>();
        var result = new List<ReasoningEffortOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var effort = NormalizeCatalogEffort(String(value, "reasoningEffort"));
            if (effort is null || !seen.Add(effort)) continue;
            result.Add(new(effort, Truncate(String(value, "description"), 2048) ?? ""));
        }
        return result;
    }

    private static IReadOnlyList<ReasoningEffortOption> ParseClaudeEfforts(JsonElement row)
    {
        if (!row.TryGetProperty("supportedEffortLevels", out var values) || values.ValueKind != JsonValueKind.Array) return Array.Empty<ReasoningEffortOption>();
        var result = new List<ReasoningEffortOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var effort = NormalizeCatalogEffort(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            if (effort is not null && seen.Add(effort)) result.Add(new(effort, ""));
        }
        return result;
    }

    private static string? NormalizeCatalogEffort(string? value)
    {
        try { return AgentProfile.NormalizeReasoningEffort(value); }
        catch (ArgumentException) { return null; }
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _count;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position >= _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }
                var c = _buffer[_position++];
                if (c == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    return line.ToString();
                }
                if (line.Length >= 256 * 1024)
                    throw new ModelCatalogException("Claude Code returned an oversized model catalog response.");
                line.Append(c);
            }
        }
    }
}
