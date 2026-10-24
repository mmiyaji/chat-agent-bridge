namespace ChatAgentBridge.Transports;

internal sealed class BoundedSeenSet(int capacity)
{
    private readonly object _lock = new();
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);

    internal bool TryAdd(string value)
    {
        lock (_lock)
        {
            if (!_values.Add(value)) return false;
            _order.Enqueue(value);
            while (_order.Count > capacity) _values.Remove(_order.Dequeue());
            return true;
        }
    }

}
