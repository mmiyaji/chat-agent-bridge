using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class CliDiscoveryCacheTests
{
    [Fact]
    public void ReusesResultUntilTtlExpires()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new CliDiscoveryCache<string>(TimeSpan.FromSeconds(30), () => now);
        var calls = 0;
        string? Resolve() { calls++; return "codex.exe"; }

        Assert.Equal("codex.exe", cache.GetOrResolve("codex", "PATH-A", _ => true, Resolve));
        now += TimeSpan.FromSeconds(29);
        Assert.Equal("codex.exe", cache.GetOrResolve("codex", "PATH-A", _ => true, Resolve));
        Assert.Equal(1, calls);
        now += TimeSpan.FromSeconds(2);
        cache.GetOrResolve("codex", "PATH-A", _ => true, Resolve);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void EnvironmentChangeInvalidatesAndFileLossRefreshes()
    {
        var cache = new CliDiscoveryCache<string>();
        var calls = 0;
        string? Resolve() { calls++; return "codex" + calls; }

        cache.GetOrResolve("codex", "PATH-A", _ => true, Resolve);
        cache.GetOrResolve("codex", "PATH-B", _ => true, Resolve);
        Assert.Equal(2, calls);
        Assert.Equal("codex3", cache.GetOrResolve("codex", "PATH-A", _ => false, Resolve));
        cache.Clear();
        cache.GetOrResolve("codex", "PATH-A", _ => true, Resolve);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void NullResultsAreNotCached()
    {
        var cache = new CliDiscoveryCache<string>();
        var calls = 0;
        string? Resolve() { calls++; return null; }

        Assert.Null(cache.GetOrResolve("x", "env", _ => true, Resolve));
        Assert.Null(cache.GetOrResolve("x", "env", _ => true, Resolve));
        Assert.Equal(2, calls);
    }
}
