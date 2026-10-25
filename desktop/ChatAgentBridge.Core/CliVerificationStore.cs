using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ChatAgentBridge.Core;

public sealed record CachedCliVerification(CliProbeResult Probe, IReadOnlyList<ModelOption> Models,
    DateTimeOffset CheckedAtUtc, string ConnectionSignature);

/// <summary>Stores successful, non-authoritative CLI diagnostics for reuse by the settings UI.</summary>
public sealed class CliVerificationStore
{
    private const int SchemaVersion = 2;
    private const int MaxFileBytes = 1_048_576;
    private const int MaxModels = 2_000;
    private readonly string _path;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private readonly SemaphoreSlim _gate;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 24 };

    public CliVerificationStore(string? directory = null)
    {
        directory ??= new SettingsStore().DirectoryPath;
        _path = Path.Combine(Path.GetFullPath(directory), "cli-verification.json");
        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Uses the same diagnostic cache for the overview and the isolated provider editor.</summary>
    public static CliVerificationStore ForProvider(string directory, string provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        if (normalized is not (AgentProfile.Codex or AgentProfile.Claude or AgentProfile.Gemini or
            AgentProfile.GitHubCopilot or AgentProfile.Antigravity or AgentProfile.Custom))
            throw new ArgumentException("Only supported agent providers can be cached.", nameof(provider));
        return new CliVerificationStore(Path.Combine(directory, "agent-cli-cache", normalized));
    }

    /// <summary>Compares the validated executable, workspace and trust context without starting either CLI.</summary>
    public static bool HasSameContext(BridgeSettings left, BridgeSettings right) =>
        CreateContextFingerprint(left, CliProbeService.ResolveLaunch(left)) ==
        CreateContextFingerprint(right, CliProbeService.ResolveLaunch(right));

    public async Task SaveAsync(BridgeSettings settings, CachedCliVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(verification);
        if ((AgentProfile.ResolveProvider(settings) != AgentProfile.Custom && !verification.Probe.ProviderRecognized) ||
            string.IsNullOrWhiteSpace(verification.Probe.Version))
            throw new ArgumentException("Only successful CLI verifications can be cached.", nameof(verification));
        if (verification.Models is null || verification.Models.Count > MaxModels)
            throw new ArgumentException("The model catalog exceeds the cache limit.", nameof(verification));
        var launch = CliProbeService.ResolveLaunch(settings);
        var contextFingerprint = CreateContextFingerprint(settings, launch);
        if (!IsValidSignature(verification.ConnectionSignature))
            throw new ArgumentException("The connection signature must be a SHA-256 hex digest.", nameof(verification));
        if (verification.CheckedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || verification.CheckedAtUtc == default)
            throw new ArgumentException("The verification timestamp is invalid.", nameof(verification));

        var document = new CacheDocument
        {
            SchemaVersion = SchemaVersion,
            Probe = verification.Probe,
            Models = verification.Models.ToArray(),
            CheckedAtUtc = verification.CheckedAtUtc.ToUniversalTime(),
            ConnectionSignature = verification.ConnectionSignature,
            ContextFingerprint = contextFingerprint,
            Stamps = GetStamps(launch)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("The verification cache exceeds its size limit.", nameof(verification));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, _path, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
        finally { _gate.Release(); }
    }

    public async Task<CachedCliVerification?> LoadAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length is <= 0 or > MaxFileBytes) return null;
            // Bound the allocation even if another process replaces or grows
            // the file after the initial metadata check.
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            if (length is <= 0 or > MaxFileBytes) return null;
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<CacheDocument>(bytes, JsonOptions);
            if (doc is null || doc.SchemaVersion is not (1 or SchemaVersion) || doc.Probe is null || doc.Models is null ||
                doc.Models.Length > MaxModels || (AgentProfile.ResolveProvider(settings) != AgentProfile.Custom && !doc.Probe.ProviderRecognized) || string.IsNullOrWhiteSpace(doc.Probe.Version) ||
                doc.CheckedAtUtc == default || doc.CheckedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                !IsValidSignature(doc.ConnectionSignature)) return null;
            var launch = CliProbeService.ResolveLaunch(settings); // rechecks existence and trust without launching the CLI
            var contextFingerprint = doc.SchemaVersion == 1
                ? CreateContextFingerprint(settings)
                : CreateContextFingerprint(settings, launch);
            if (string.IsNullOrWhiteSpace(doc.ContextFingerprint) || !IsValidSignature(doc.ContextFingerprint) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(contextFingerprint), Convert.FromHexString(doc.ContextFingerprint))) return null;
            var currentStamps = GetStamps(launch);
            if (!StampsMatch(doc.Stamps, currentStamps)) return null;
            if (!PathEquals(doc.Probe.Executable, launch.Executable)) return null;
            ValidateModels(doc.Models);
            return new CachedCliVerification(doc.Probe, doc.Models, doc.CheckedAtUtc, doc.ConnectionSignature!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or
                                      NotSupportedException or System.Security.SecurityException or FormatException or OverflowException)
        { return null; }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _gate.Release(); }
    }

    /// <summary>Builds a private cache-context digest from settings that determine the probe result.</summary>
    internal static string CreateContextFingerprint(BridgeSettings settings) => CreateContextFingerprint(settings, null);

    private static string CreateContextFingerprint(BridgeSettings settings, CliProbeLaunch? launch)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = AgentProfile.ResolveProvider(settings);
        var workspace = string.IsNullOrWhiteSpace(settings.WorkspaceRoot) ? "" : Path.GetFullPath(settings.WorkspaceRoot);
        // A checked command name and its resolved absolute executable identify the same CLI.
        // Keep the v1 projection above for strict reads of existing caches.
        var values = new[] { provider, launch?.Executable ?? AgentProfile.ResolveEffectiveExecutable(settings), settings.AgentArguments ?? "", workspace,
            settings.AgentMode ?? "", ExecutionPolicy.NormalizeTrustedRoots(settings.TrustedWorkspaceRoots) };
        if (launch is not null)
        {
            var entry = string.IsNullOrWhiteSpace(launch.NodeEntryPoint) ? "" : Path.IsPathFullyQualified(launch.NodeEntryPoint)
                ? launch.NodeEntryPoint : Path.GetFullPath(launch.NodeEntryPoint, launch.Workspace);
            values = [.. values, entry];
        }
        var canonical = string.Join("\n", values.Select(value => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static FileStamp[] GetStamps(CliProbeLaunch launch)
    {
        var paths = new List<string> { launch.Executable };
        if (!string.IsNullOrWhiteSpace(launch.NodeEntryPoint))
        {
            var entry = Path.IsPathFullyQualified(launch.NodeEntryPoint) ? launch.NodeEntryPoint : Path.GetFullPath(launch.NodeEntryPoint, launch.Workspace);
            paths.Add(entry);
        }
        return paths.Distinct(PathComparer).Select(path =>
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists) throw new FileNotFoundException("Resolved CLI file is missing.", full);
            return new FileStamp(full, info.Length, info.LastWriteTimeUtc.Ticks);
        }).ToArray();
    }

    private static bool StampsMatch(FileStamp[]? expected, FileStamp[] actual)
        => expected is not null && expected.Length == actual.Length && expected.All(stamp => stamp is not null && !string.IsNullOrWhiteSpace(stamp.Path)) &&
           expected.Zip(actual).All(pair => PathEquals(pair.First.Path, pair.Second.Path) && pair.First.Length == pair.Second.Length &&
                                             pair.First.LastWriteUtcTicks == pair.Second.LastWriteUtcTicks);

    private static void ValidateModels(ModelOption[] models)
    {
        foreach (var model in models)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 256 || model.DisplayName is null || model.DisplayName.Length > 256 ||
                model.Description is null || model.Description.Length > 2048 || model.SupportedReasoningEfforts is null || model.SupportedReasoningEfforts.Count > 32)
                throw new JsonException("Invalid cached model entry.");
            foreach (var effort in model.SupportedReasoningEfforts)
                if (effort is null || string.IsNullOrWhiteSpace(effort.Id) || effort.Id.Length > 32 || effort.Description is null || effort.Description.Length > 2048)
                    throw new JsonException("Invalid cached reasoning option.");
        }
    }

    private static bool IsValidSignature(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool PathEquals(string left, string right) => PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public CliProbeResult? Probe { get; set; }
        public ModelOption[]? Models { get; set; }
        public DateTimeOffset CheckedAtUtc { get; set; }
        public string? ConnectionSignature { get; set; }
        public string? ContextFingerprint { get; set; }
        public FileStamp[]? Stamps { get; set; }
    }
    private sealed record FileStamp(string Path, long Length, long LastWriteUtcTicks);
}
