namespace ChatAgentBridge.Transports;

/// <summary>Short-lived, bounded cache of Mattermost user is_bot lookups so accepted posts do not each trigger an HTTP call.</summary>
internal sealed class MattermostBotCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    // Oldest-first. Each entry owns exactly one node, so expiry and refresh unlink it and the list never outgrows the cache.
    private readonly LinkedList<string> _order = new();
    private readonly object _gate = new();

    private sealed record Entry(bool IsBot, DateTimeOffset Expires, LinkedListNode<string> Node);

    internal MattermostBotCache(int capacity = 1_000, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    internal bool TryGet(string userId, out bool isBot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(userId, out var entry))
            {
                if (entry.Expires > _now())
                {
                    isBot = entry.IsBot;
                    return true;
                }
                Remove(userId, entry);
            }
        }
        isBot = false;
        return false;
    }

    internal void Set(string userId, bool isBot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(userId, out var existing)) Remove(userId, existing);
            _entries[userId] = new Entry(isBot, _now() + _ttl, _order.AddLast(userId));
            while (_entries.Count > _capacity && _order.First is { } oldest) Remove(oldest.Value, _entries[oldest.Value]);
        }
    }

    // Test hook: the number of tracked ids must always equal the number of live entries.
    internal (int Entries, int Tracked) Sizes()
    {
        lock (_gate) return (_entries.Count, _order.Count);
    }

    private void Remove(string userId, Entry entry)
    {
        _order.Remove(entry.Node);
        _entries.Remove(userId);
    }
}
