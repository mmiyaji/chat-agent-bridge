namespace ChatAgentBridge_Desktop.Deployment;

/// <summary>Deployment-specific values which differ between MSIX and portable builds.</summary>
public static class DeploymentRuntime
{
    public static string GetDisplayVersion()
    {
#if WINDOWS
        if (!IsPortable)
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return FormatVersion(version.Major, version.Minor, version.Build, version.Revision);
            }
            catch (InvalidOperationException) { }
        }
#endif

        var assemblyVersion = GetAssemblyVersion();
        return assemblyVersion.EndsWith(".0", StringComparison.Ordinal) && assemblyVersion.Count(c => c == '.') == 3
            ? assemblyVersion[..^2]
            : assemblyVersion;
    }

    private static string FormatVersion(ushort major, ushort minor, ushort build, ushort revision)
    {
        var version = $"{major}.{minor}.{build}.{revision}";
        return revision == 0 ? version[..^2] : version;
    }

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
