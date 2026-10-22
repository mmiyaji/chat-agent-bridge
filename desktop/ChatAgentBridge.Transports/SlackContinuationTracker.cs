using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Transports;

/// <summary>Tracks Slack threads (per user) that started a request during the active socket run, before a binding is saved.</summary>
internal sealed class SlackContinuationTracker
{
    private readonly int _capacity;
    private readonly HashSet<SessionKey> _keys = [];
    private readonly Queue<SessionKey> _order = new();
    private readonly object _gate = new();

    internal SlackContinuationTracker(int capacity = 1_000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal bool Contains(SessionKey key)
    {
        lock (_gate) return _keys.Contains(key);
    }

    internal void Remember(SessionKey key)
    {
        lock (_gate)
        {
            if (!_keys.Add(key)) return;
            _order.Enqueue(key);
            while (_keys.Count > _capacity) _keys.Remove(_order.Dequeue());
        }
    }
}
