namespace ChatAgentBridge.Transports;

/// <summary>Short-lived, bounded cache of Mattermost user is_bot lookups so accepted posts do not each trigger an HTTP call.</summary>
internal sealed class MattermostBotCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, (bool IsBot, DateTimeOffset Expires)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

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
                _entries.Remove(userId);
            }
        }
        isBot = false;
        return false;
    }

    internal void Set(string userId, bool isBot)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(userId)) _order.Enqueue(userId);
            _entries[userId] = (isBot, _now() + _ttl);
            // The queue may hold ids already removed on expiry; dequeue until the live count is within capacity.
            while (_entries.Count > _capacity && _order.TryDequeue(out var oldest)) _entries.Remove(oldest);
        }
    }
}
