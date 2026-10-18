using System.Collections.Concurrent;
using System.Text.Json;

namespace ChatAgentBridge.Core.Sessions;

public sealed class SessionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Path, SessionKey Key), SemaphoreSlim> SessionGates = new();
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;

    public SessionStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? new SettingsStore().DirectoryPath);
        _filePath = Path.Combine(DirectoryPath, "sessions.json");
        _gate = PathGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string DirectoryPath { get; }

    internal async Task<T> WithSessionLockAsync<T>(SessionKey key,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(action);
        var path = OperatingSystem.IsWindows() ? _filePath.ToUpperInvariant() : _filePath;
        var gate = SessionGates.GetOrAdd((path, key), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SessionBinding>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<SessionBinding?> FindAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Key == key); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SessionBinding binding, CancellationToken cancellationToken = default)
    {
        ValidateBinding(binding);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bindings = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existingIndex = bindings.FindIndex(item => item.Key == binding.Key);
            if (existingIndex >= 0) bindings[existingIndex] = binding;
            else bindings.Add(binding);
            await WriteAsync(bindings, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await WithSessionLockAsync(key, async token =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bindings = await ReadAsync(token).ConfigureAwait(false);
                if (bindings.RemoveAll(item => item.Key == key) > 0)
                    await WriteAsync(bindings, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets or clears a per-session model override. Null follows the current provider default.</summary>
    public Task UpdateModelAsync(SessionKey key, string? model, CancellationToken cancellationToken = default)
        => UpdateModelAndEffortAsync(key, model, null, preserveReasoningEffort: true, cancellationToken);

    /// <summary>Atomically sets or clears a per-session model and reasoning effort override.</summary>
    public Task UpdateModelAndEffortAsync(SessionKey key, string? model, string? effort, CancellationToken cancellationToken = default)
        => UpdateModelAndEffortAsync(key, model, effort, preserveReasoningEffort: false, cancellationToken);

    private Task UpdateModelAndEffortAsync(SessionKey key, string? model, string? effort,
        bool preserveReasoningEffort, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        model = AgentProfile.NormalizeModel(model);
        effort = AgentProfile.NormalizeReasoningEffort(effort);
        return WithSessionLockAsync(key, async token =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bindings = await ReadAsync(token).ConfigureAwait(false);
                var index = bindings.FindIndex(item => item.Key == key);
                if (index < 0) throw new KeyNotFoundException("No saved session exists for this conversation.");
                var current = bindings[index];
                bindings[index] = current with
                {
                    Model = model,
                    ReasoningEffort = preserveReasoningEffort ? current.ReasoningEffort : effort,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await WriteAsync(bindings, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return true;
        }, cancellationToken);
    }

    /// <summary>Atomically updates per-conversation approval, result-sharing, and agent permission overrides.</summary>
    public Task UpdateExecutionPolicyAsync(SessionKey key, string? approval, string? result, string? permission,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        approval = approval is null ? null : ExecutionPolicy.NormalizeChatApprovalMode(approval);
        result = result is null ? null : ExecutionPolicy.NormalizeChatResultMode(result);
        permission = permission is null ? null : ExecutionPolicy.NormalizePermissionMode(permission);
        return WithSessionLockAsync(key, async token =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bindings = await ReadAsync(token).ConfigureAwait(false);
                var index = bindings.FindIndex(item => item.Key == key);
                if (index < 0) throw new KeyNotFoundException("No saved session exists for this conversation.");
                bindings[index] = bindings[index] with
                {
                    ChatApprovalMode = approval,
                    ChatResultMode = result,
                    AgentPermissionMode = permission,
                    FirstApprovalFingerprint = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await WriteAsync(bindings, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores the settings fingerprint approved for first-only chat execution.</summary>
    public Task SetFirstApprovalFingerprintAsync(SessionKey key, string fingerprint, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Approval fingerprint must be a 64-character hexadecimal SHA-256 value.", nameof(fingerprint));
        fingerprint = fingerprint.ToLowerInvariant();
        return WithSessionLockAsync(key, async token =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bindings = await ReadAsync(token).ConfigureAwait(false);
                var index = bindings.FindIndex(item => item.Key == key);
                if (index < 0) throw new KeyNotFoundException("No saved session exists for this conversation.");
                bindings[index] = bindings[index] with { FirstApprovalFingerprint = fingerprint, UpdatedAt = DateTimeOffset.UtcNow };
                await WriteAsync(bindings, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return true;
        }, cancellationToken);
    }

    private async Task<List<SessionBinding>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<StoredSessions>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is null) throw new InvalidDataException("Session store is empty or invalid.");
            if (document.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Session store schema version {document.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
            if (document.Bindings is null) throw new InvalidDataException("Session store has no bindings collection.");
            var keys = new HashSet<SessionKey>();
            foreach (var binding in document.Bindings)
            {
                ValidateBinding(binding);
                if (!keys.Add(binding.Key)) throw new InvalidDataException("Session store contains duplicate session keys.");
            }
            return document.Bindings;
        }
        catch (JsonException ex) { throw new InvalidDataException("Session store contains invalid JSON.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Session store contains an invalid session binding.", ex); }
    }

    private async Task WriteAsync(List<SessionBinding> bindings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = Path.Combine(DirectoryPath, $"sessions.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredSessions
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Bindings = bindings
                }, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await AtomicFile.ReplaceAsync(temp, _filePath, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ValidateBinding(SessionBinding? binding)
    {
        if (binding is null) throw new ArgumentException("Session binding is required.");
        ValidateKey(binding.Key);
        if (string.IsNullOrWhiteSpace(binding.WorkspaceRoot) || !Path.IsPathFullyQualified(binding.WorkspaceRoot))
            throw new ArgumentException("Session workspace must be an absolute path.");
        if (string.IsNullOrWhiteSpace(binding.AgentExecutable)) throw new ArgumentException("Session executable is required.");
        if (binding.AgentArguments is null) throw new ArgumentException("Session arguments are required.");
        if (!Guid.TryParse(binding.SessionId, out _)) throw new ArgumentException("Session id must be a GUID.");
        _ = AgentProfile.NormalizeModel(binding.Model);
        _ = AgentProfile.NormalizeReasoningEffort(binding.ReasoningEffort);
        _ = binding.ChatApprovalMode is null ? null : ExecutionPolicy.NormalizeChatApprovalMode(binding.ChatApprovalMode);
        _ = binding.ChatResultMode is null ? null : ExecutionPolicy.NormalizeChatResultMode(binding.ChatResultMode);
        _ = binding.AgentPermissionMode is null ? null : ExecutionPolicy.NormalizePermissionMode(binding.AgentPermissionMode);
        if (binding.FirstApprovalFingerprint is not null &&
            (binding.FirstApprovalFingerprint.Length != 64 || binding.FirstApprovalFingerprint.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("Session approval fingerprint must be a 64-character hexadecimal SHA-256 value.");
    }

    private static void ValidateKey(SessionKey? key)
    {
        if (key is null) throw new ArgumentException("Session key is required.");
        ValidateIdentifier(key.Provider, "provider");
        ValidateIdentifier(key.ChannelId, "channel id");
        ValidateIdentifier(key.ThreadId, "thread id");
        if ((key.UserId is null) != (key.Origin is null))
            throw new ArgumentException("Session user id and origin must either both be present or both be absent.");
        if (key.UserId is not null)
        {
            ValidateScopedIdentity(key.UserId, "user id");
            ValidateScopedIdentity(key.Origin!, "origin");
            if (key.Provider.Equals("slack", StringComparison.OrdinalIgnoreCase) && key.Origin != "slack")
                throw new ArgumentException("Slack session origin must be 'slack'.");
            if (key.Provider.Equals("mattermost", StringComparison.OrdinalIgnoreCase) &&
                (key.Origin!.Length != 64 || key.Origin.Any(c => !Uri.IsHexDigit(c))))
                throw new ArgumentException("Mattermost session origin must be a SHA-256 hex digest.");
        }
    }

    private static void ValidateScopedIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException($"Session {name} is invalid.");
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".." ||
            value.Any(char.IsControl) || value.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            throw new ArgumentException($"Session {name} is invalid.");
    }

    private sealed class StoredSessions
    {
        public int? SchemaVersion { get; set; }
        public List<SessionBinding>? Bindings { get; set; }
    }
}
