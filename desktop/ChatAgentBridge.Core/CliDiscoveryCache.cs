using System.Collections.Concurrent;

namespace ChatAgentBridge.Core;

/// <summary>
/// Short-lived, thread-safe cache for CLI executable discovery. Entries are keyed by the configured
/// executable and the environment that influences the search, and are dropped when stale or when the
/// cached files no longer exist.
/// </summary>
internal sealed class CliDiscoveryCache<T> where T : class
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<(string Executable, string Environment), Entry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;

    public CliDiscoveryCache(TimeSpan? ttl = null, Func<DateTime>? clock = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Returns a fresh cached value, or runs <paramref name="resolve"/>. Only non-null results are cached.</summary>
    public T? GetOrResolve(string executable, string environment, Func<T, bool> stillValid, Func<T?> resolve)
    {
        var key = (executable, environment);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (_clock() - entry.CreatedUtc < _ttl && SafeValid(stillValid, entry.Value)) return entry.Value;
            _entries.TryRemove(new KeyValuePair<(string Executable, string Environment), Entry>(key, entry));
        }
        var value = resolve();
        if (value is not null) _entries[key] = new Entry(value, _clock());
        return value;
    }

    public void Clear() => _entries.Clear();

    private static bool SafeValid(Func<T, bool> stillValid, T value)
    {
        try { return stillValid(value); }
        catch { return false; }
    }

    private sealed record Entry(T Value, DateTime CreatedUtc);
}
