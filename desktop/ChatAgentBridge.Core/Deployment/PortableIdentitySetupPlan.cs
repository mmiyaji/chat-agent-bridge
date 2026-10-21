using System.Text.Json;
using ChatAgentBridge.Core.Updates;

namespace ChatAgentBridge.Core.Deployment;

/// <summary>An explicitly confirmed, current-user identity change, performed after the app exits.</summary>
public sealed record PortableIdentitySetupPlan(
    int SchemaVersion, string InstallDirectory, string DataDirectory, string WorkDirectory,
    int ParentProcessId, long ParentStartTimeUtcTicks, string Nonce, string Operation,
    string PackageSha256, string CertificateSha256, bool TrustCertificate, bool AllowRelocate,
    string Language = "en")
{
    public const string FileName = "identity-plan.json";
    public string ReadyFile => Path.Combine(WorkDirectory, "ready-" + Nonce);
    public string CommitFile => Path.Combine(WorkDirectory, "commit-" + Nonce);
    public string ResultFile => Path.Combine(WorkDirectory, "identity-result.json");

    public void Validate()
    {
        if (SchemaVersion != 1 || ParentProcessId <= 0 || ParentStartTimeUtcTicks <= 0 ||
            Nonce is null || Nonce.Length != 32 || !Nonce.All(Uri.IsHexDigit) ||
            Operation is not ("register" or "unregister") ||
            !IsHash(PackageSha256) || !IsHash(CertificateSha256) ||
            (Operation == "unregister" && (TrustCertificate || AllowRelocate)))
            throw new InvalidDataException("Invalid portable notification setup plan.");

        foreach (var path in new[] { InstallDirectory, DataDirectory, WorkDirectory })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != Path.TrimEndingDirectorySeparator(path) ||
                Path.GetPathRoot(path) == Path.TrimEndingDirectorySeparator(path))
                throw new InvalidDataException("Notification setup requires absolute non-root directories.");
            PortableUpdatePaths.EnsureNoReparsePoints(path);
        }
        var expectedWork = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatAgentBridge", "notification-setup", Nonce);
        if (!Path.GetFullPath(WorkDirectory).Equals(Path.GetFullPath(expectedWork), StringComparison.OrdinalIgnoreCase) ||
            PortableUpdatePaths.Overlaps(WorkDirectory, InstallDirectory) ||
            PortableUpdatePaths.Overlaps(WorkDirectory, DataDirectory) ||
            PortableUpdatePaths.IsWithin(DataDirectory, InstallDirectory))
            throw new InvalidDataException("Unsafe notification setup directory relationship.");
    }

    public static async Task<PortableIdentitySetupPlan> ReadAsync(string path)
    {
        PortableUpdatePaths.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Notification setup plan is too large.");
        var plan = JsonSerializer.Deserialize<PortableIdentitySetupPlan>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("Notification setup plan is empty.");
        plan.Validate();
        if (!Path.GetFullPath(path).Equals(Path.Combine(plan.WorkDirectory, FileName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected notification setup plan location.");
        return plan;
    }

    public void ValidateBundle(PortableIdentityBundle bundle)
    {
        if (!PackageSha256.Equals(bundle.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !CertificateSha256.Equals(bundle.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The notification package changed after confirmation.");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
