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

    [Fact]
    public void Repeated_expiry_and_refetch_does_not_grow_the_internal_order()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new MattermostBotCache(10, TimeSpan.FromMinutes(1), () => now);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.False(cache.TryGet("u1", out _));
            cache.Set("u1", false);
            now += TimeSpan.FromMinutes(2);
        }

        // Only the last fetch is still tracked, and the tracked ids match the live entries exactly.
        Assert.Equal((1, 1), cache.Sizes());
        now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("u1", out _));
        Assert.Equal((0, 0), cache.Sizes());
    }

    [Fact]
    public void Refreshing_an_entry_is_not_evicted_by_its_stale_position()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new MattermostBotCache(2, TimeSpan.FromMinutes(1), () => now);
        cache.Set("u1", false);
        cache.Set("u2", false);
        now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("u1", out _));
        cache.Set("u1", true);

        // u1 was refreshed after u2; adding u3 must evict u2 (oldest), not the refreshed u1.
        cache.Set("u3", false);

        Assert.True(cache.TryGet("u1", out var bot) && bot);
        Assert.Equal((2, 2), cache.Sizes());
    }
}
