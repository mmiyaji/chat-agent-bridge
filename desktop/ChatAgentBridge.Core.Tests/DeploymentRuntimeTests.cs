using ChatAgentBridge_Desktop.Deployment;

namespace ChatAgentBridge.Core.Tests;

public sealed class DeploymentRuntimeTests
{
    [Fact]
    public void ExplicitDataDirectoryWinsForPortableAndPackagedLaunches()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cab-data-override"));

        Assert.Equal(expected, DeploymentRuntime.ResolveDataDirectory(
            isPortable: true,
            explicitDirectory: expected,
            environmentDirectory: Path.Combine(Path.GetTempPath(), "cab-env-data"),
            executableDirectory: Path.GetTempPath(),
            packageLocalDirectory: null));

        Assert.Equal(expected, DeploymentRuntime.ResolveDataDirectory(
            isPortable: false,
            explicitDirectory: expected,
            environmentDirectory: null,
            executableDirectory: Path.GetTempPath(),
            packageLocalDirectory: Path.Combine(Path.GetTempPath(), "cab-package-data")));
    }

    [Fact]
    public void PortableDefaultsBesideExecutableAndPackagedDefaultsToLocalFolder()
    {
        var executableDirectory = Path.Combine(Path.GetTempPath(), "cab-portable");
        var packageDirectory = Path.Combine(Path.GetTempPath(), "cab-package");

        Assert.Equal(Path.GetFullPath(Path.Combine(executableDirectory, "Data")),
            DeploymentRuntime.ResolveDataDirectory(
                isPortable: true,
                explicitDirectory: null,
                environmentDirectory: null,
                executableDirectory,
                packageLocalDirectory: null));

        Assert.Equal(Path.GetFullPath(packageDirectory),
            DeploymentRuntime.ResolveDataDirectory(
                isPortable: false,
                explicitDirectory: null,
                environmentDirectory: null,
                executableDirectory,
                packageDirectory));
    }
}
