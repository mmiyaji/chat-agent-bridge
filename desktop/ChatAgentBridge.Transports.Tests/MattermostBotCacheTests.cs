using ChatAgentBridge.Transports;

namespace ChatAgentBridge.Transports.Tests;

public sealed class MattermostBotCacheTests
{
    [Fact]
    public void Cached_value_is_returned_until_ttl_expires()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new MattermostBotCache(10, TimeSpan.FromMinutes(5), () => now);
        Assert.False(cache.TryGet("u1", out _));

        cache.Set("u1", true);
        cache.Set("u2", false);
        Assert.True(cache.TryGet("u1", out var bot) && bot);
        Assert.True(cache.TryGet("u2", out var human) && !human);

        now += TimeSpan.FromMinutes(5);
        Assert.False(cache.TryGet("u1", out _));
    }

    [Fact]
    public void Cache_is_bounded_and_evicts_the_oldest_entry()
    {
        var cache = new MattermostBotCache(2);
        cache.Set("u1", false);
        cache.Set("u2", false);
        cache.Set("u3", true);

        Assert.False(cache.TryGet("u1", out _));
        Assert.True(cache.TryGet("u2", out _));
        Assert.True(cache.TryGet("u3", out var bot) && bot);
    }
}
