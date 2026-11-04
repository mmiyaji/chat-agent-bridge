using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class MuseExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muse-resolver-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ResolvesOfficialLauncherToNativeForActiveVersion()
    {
        var native = Install(_root, "1.4.14-R7309.1");
        var resolved = MuseExecutableResolver.Resolve(Path.Combine(_root, "muse.cmd"), null, null);
        Assert.Equal(native, resolved);
    }

    [Fact]
    public void BareMuseUsesManagedInstallOnlyWhenPathHasNoMatchAndExplicitBrokenShimFailsClosed()
    {
        var native = Install(_root, "1.4.14-R7309.1");
        Assert.Equal(native, MuseExecutableResolver.Resolve("muse", _root, Path.Combine(_root, "missing")));

        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "muse.cmd"), "launcher placeholder");
        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve(Path.Combine(broken, "muse.cmd"), _root, _root));
    }

    [Fact]
    public void BareMuseUsesFirstPathMatchAndNativeWinsWithinDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var nativeDirectory = Path.Combine(_root, "native");
        var launcherDirectory = Path.Combine(_root, "launcher");
        var fallbackDirectory = Path.Combine(_root, "fallback");
        Directory.CreateDirectory(nativeDirectory);
        var pathNative = Path.Combine(nativeDirectory, "muse.exe");
        File.WriteAllText(pathNative, "path native");
        File.WriteAllText(Path.Combine(nativeDirectory, "muse.cmd"), "launcher placeholder");
        Install(launcherDirectory, "1.4.14-R7309.2");
        Install(fallbackDirectory, "1.4.14-R7309.1");
        var searchPath = string.Join(Path.PathSeparator, nativeDirectory, launcherDirectory);

        Assert.Equal(pathNative, MuseExecutableResolver.Resolve("muse", fallbackDirectory, searchPath));

        var sameDirectoryNative = Path.Combine(launcherDirectory, "muse.exe");
        File.WriteAllText(sameDirectoryNative, "native wins over same-directory shim");
        Assert.Equal(sameDirectoryNative, MuseExecutableResolver.Resolve("muse", fallbackDirectory, launcherDirectory));
    }

    [Fact]
    public void BareMuseUsesTheFirstLauncherInstallInsteadOfManagedFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launcherDirectory = Path.Combine(_root, "path-launcher");
        var fallbackDirectory = Path.Combine(_root, "fallback");
        var selected = Install(launcherDirectory, "1.4.14-R7309.2");
        Install(fallbackDirectory, "1.4.14-R7309.1");

        Assert.Equal(selected, MuseExecutableResolver.Resolve("muse", fallbackDirectory, launcherDirectory));
    }

    [Fact]
    public void BareMuseLauncherMatchUsesOnlyThatInstallAndBrokenMatchShadowsLaterAndFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Path.Combine(_root, "first");
        var later = Path.Combine(_root, "later");
        var fallback = Path.Combine(_root, "fallback");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "muse.cmd"), "launcher placeholder");
        var laterNative = Install(later, "1.4.14-R7309.2");
        Install(fallback, "1.4.14-R7309.1");
        var searchPath = string.Join(Path.PathSeparator, first, later);

        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve("muse", fallback, searchPath));
        Assert.NotEqual(laterNative, Path.Combine(first, "muse.cmd"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("1.2.3-R4\\..\\outside")]
    [InlineData("1.2.3-evil")]
    public void RejectsUnsafeOrUnsupportedVersionAndNeverFallsBack(string version)
    {
        Install(_root, version);
        var later = Path.Combine(_root, "later");
        Install(later, "1.4.14-R7309.1");
        var searchPath = string.Join(Path.PathSeparator, _root, later);
        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve(Path.Combine(_root, "muse.cmd"), null, searchPath));
    }

    [Fact]
    public void RejectsOversizedVersionFileAndMissingNativeBinary()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".muse-version"), new string('1', 129));
        File.WriteAllText(Path.Combine(_root, "muse.cmd"), "launcher placeholder");
        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve(Path.Combine(_root, "muse.cmd"), null, null));

        File.WriteAllText(Path.Combine(_root, ".muse-version"), "1.4.14-R7309.1");
        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve(Path.Combine(_root, "muse.cmd"), null, null));
    }

    [Fact]
    public void RejectsReparsePointInstallRoot()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "linked");
        Install(target, "1.4.14-R7309.1");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return;
        }
        Assert.Throws<InvalidOperationException>(() => MuseExecutableResolver.Resolve(Path.Combine(link, "muse.cmd"), null, null));
    }

    [Fact]
    public void InvalidEarlierPathLauncherShadowsLaterInstallation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Path.Combine(_root, "first");
        var later = Path.Combine(_root, "later");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "muse.cmd"), "launcher placeholder");
        Install(later, "1.4.14-R7309.1");
        var searchPath = string.Join(Path.PathSeparator, first, later);
        Assert.Null(CliExecutableDiscovery.Find(AgentProfile.Muse, searchPath));
    }

    [Fact]
    public void DiscoveryReturnsResolvedNativeForOfficialPathLauncher()
    {
        if (!OperatingSystem.IsWindows()) return;
        var native = Install(_root, "1.4.14-R7309.1");
        var detection = CliExecutableDiscovery.Find(AgentProfile.Muse, _root);
        Assert.Equal(native, detection!.Executable);
        Assert.Equal(native, detection.DiscoveredPath);
    }

    [Fact]
    public void DiscoveryReturnsAbsolutePathWhenPathSelectsNativeMuse()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var native = Path.Combine(_root, "muse.exe");
        File.WriteAllText(native, "native placeholder");
        var detection = CliExecutableDiscovery.Find(AgentProfile.Muse, _root);
        Assert.Equal(native, detection!.Executable);
        Assert.Equal(native, detection.DiscoveredPath);
    }

    private static string Install(string root, string version)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "muse.cmd"), "launcher placeholder");
        File.WriteAllText(Path.Combine(root, ".muse-version"), version);
        var native = Path.Combine(root, $"muse-bin-{version}.exe");
        if (version.Contains("..", StringComparison.Ordinal) || version.Contains('/') || version.Contains('\\'))
            return native;
        File.WriteAllText(native, "native placeholder");
        return native;
    }
}
