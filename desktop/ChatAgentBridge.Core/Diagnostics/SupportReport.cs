using ChatAgentBridge.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core.Diagnostics;

/// <summary>An allowlisted support export with no user configuration or raw process output.</summary>
public sealed record SupportReport
{
    private static readonly Regex SafeBuild = new(@"^[0-9][A-Za-z0-9.+_-]{0,79}$", RegexOptions.CultureInvariant);
    private static readonly Regex NumericSystemVersion = new(@"^[0-9]+(?:\.[0-9]+){1,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex CliVersionToken = new(@"^\s*v?([0-9]+(?:\.[0-9]+){1,3})(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "cli_verified", "cli_unverified", "chat_configured", "chat_not_configured",
        "notifications_registered", "notifications_unavailable", "updater_supported", "updater_unavailable"
    };
    private static readonly HashSet<string> AllowedArchitectures = new(StringComparer.Ordinal)
        { "X86", "X64", "Arm", "Arm64", "Wasm", "S390x", "LoongArch64" };
    private static readonly HashSet<string> AllowedProviders = new(StringComparer.Ordinal)
        { "Codex", "Claude", "Gemini", "GitHub Copilot", "Antigravity", "Custom", "Unknown" };

    public string Product { get; }
    public string Build { get; }
    public string WindowsVersion { get; }
    public string RuntimeVersion { get; }
    public string Architecture { get; }
    public string Provider { get; }
    public string CliVersion { get; }
    public string ErrorType { get; }
    public string HResult { get; }
    public IReadOnlyList<string> Status { get; }
    public IReadOnlyList<StorageOperationDiagnostic> StorageOperations { get; }
    public IReadOnlyList<SettingsNavigationDiagnostic> SettingsNavigation { get; }

    private SupportReport(string build, string windowsVersion, string runtimeVersion, string architecture,
        string provider, string cliVersion, IEnumerable<string> status, string errorType, string hResult,
        IEnumerable<StorageOperationDiagnostic> storageOperations, IEnumerable<SettingsNavigationDiagnostic> settingsNavigation)
    {
        Product = "Chat Agent Bridge";
        Build = SafeBuild.IsMatch(build) ? build : "unknown";
        WindowsVersion = NumericSystemVersion.IsMatch(windowsVersion) ? windowsVersion : "unknown";
        RuntimeVersion = NumericSystemVersion.IsMatch(runtimeVersion) ? runtimeVersion : "unknown";
        Architecture = AllowedArchitectures.Contains(architecture) ? architecture : "Unknown";
        Provider = AllowedProviders.Contains(provider) ? provider : "Unknown";
        CliVersion = ParseCliVersion(cliVersion);
        ErrorType = errorType is "none" or "diagnostic_unavailable" ? errorType : "none";
        HResult = Regex.IsMatch(hResult, @"^0x[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant) ? hResult : "";
        Status = status.Where(AllowedStatuses.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Revalidate at the export boundary; the diagnostic record is also publicly constructible.
        StorageOperations = storageOperations.Take(64).Where(item => item is not null &&
            item.Operation is "read" or "write" or "probe" &&
            item.Outcome is "success" or "failure" or "cancelled" &&
            item.ElapsedMilliseconds >= 0 && item.RetryCount is >= 0 and <= 2)
            .Select(item => item with { RecordedAtUtc = item.RecordedAtUtc.ToUniversalTime() }).ToArray();
        SettingsNavigation = settingsNavigation.Take(32).Where(item => item is not null &&
            item.Page is "agent_settings" or "chat_settings" && item.ElapsedMilliseconds >= 0)
            .Select(item => item with { RecordedAtUtc = item.RecordedAtUtc.ToUniversalTime() }).ToArray();
    }

    public static SupportReport Create(string? build, string? windowsVersion, string? runtimeVersion,
        string? architecture, string? provider, string? cliVersion, IEnumerable<string>? status,
        string? errorType = null, string? hResult = null, IEnumerable<StorageOperationDiagnostic>? storageOperations = null,
        IEnumerable<SettingsNavigationDiagnostic>? settingsNavigation = null) =>
        new(build ?? "unknown", windowsVersion ?? "unknown", runtimeVersion ?? "unknown",
            architecture ?? "Unknown", provider ?? "Unknown", cliVersion ?? "unknown",
            status ?? [], errorType ?? "none", hResult ?? "", storageOperations ?? [], settingsNavigation ?? []);

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static string ParseCliVersion(string value)
    {
        var match = CliVersionToken.Match(value);
        return match.Success ? match.Groups[1].Value : "unknown";
    }
}
