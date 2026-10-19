namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>Deployment-specific values which differ between MSIX and portable builds.</summary>
public static class DeploymentRuntime
{
    public static bool IsPortable
    {
        get
        {
#if CAB_PORTABLE
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Chooses the settings location while keeping explicit launch overrides ahead of defaults.
    /// The arguments make the selection testable without requiring a package identity.
    /// </summary>
    public static string ResolveDataDirectory(
        bool isPortable,
        string? explicitDirectory,
        string? environmentDirectory,
        string executableDirectory,
        string? packageLocalDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return Path.GetFullPath(explicitDirectory);

        if (!string.IsNullOrWhiteSpace(environmentDirectory))
            return Path.GetFullPath(environmentDirectory);

        if (isPortable)
            return Path.GetFullPath(Path.Combine(executableDirectory, "Data"));

        if (string.IsNullOrWhiteSpace(packageLocalDirectory))
            throw new InvalidOperationException("The packaged application data directory is unavailable.");

        return Path.GetFullPath(packageLocalDirectory);
    }

    public static string GetAssemblyVersion() =>
        typeof(DeploymentRuntime).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";
}
