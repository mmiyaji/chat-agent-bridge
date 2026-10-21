using System.Collections.Concurrent;

namespace ChatAgentBridge_Desktop.Notifications;

/// <summary>Process-local, single-use decisions. Busy callbacks retain their original expiry.</summary>
internal sealed class PendingNotificationRegistry(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal Entry Add(PendingNotificationKind kind, TimeSpan lifetime,
        Func<Task<PendingNotificationActionResult>> approve, Func<Task<PendingNotificationActionResult>> decline, Func<Task> open)
    {
        var entry = new Entry(Guid.NewGuid().ToString("N"), _clock.GetUtcNow() + lifetime, kind, approve, decline, open);
        _entries[entry.Token] = entry;
        return entry;
    }

    internal bool Remove(string token) => _entries.TryRemove(token, out _);
    internal Entry[] Clear()
    {
        List<Entry> removed = [];
        foreach (var pair in _entries.ToArray())
            if (_entries.TryRemove(pair)) removed.Add(pair.Value);
        return removed.ToArray();
    }
    internal bool Expire(string token) => _entries.TryGetValue(token, out var entry) &&
        entry.ExpiresAt <= _clock.GetUtcNow() && _entries.TryRemove(new KeyValuePair<string, Entry>(token, entry));

    internal async Task<(Outcome Outcome, Entry? Entry)> InvokeAsync(string token, string action)
    {
        if (action is not ("approve" or "decline" or "open")) return (Outcome.InvalidAction, null);
        if (!_entries.TryGetValue(token, out var entry)) return (Outcome.Missing, null);
        if (entry.ExpiresAt <= _clock.GetUtcNow())
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(token, entry));
            return (Outcome.Expired, entry);
        }
        if (Interlocked.CompareExchange(ref entry.InFlight, 1, 0) != 0) return (Outcome.Busy, entry);
        try
        {
            if (!_entries.TryGetValue(token, out var current) || !ReferenceEquals(entry, current)) return (Outcome.Missing, entry);
            if (action == "open")
            {
                await entry.Open();
                return (Outcome.Opened, entry);
            }
            var result = await (action == "approve" ? entry.Approve() : entry.Decline());
            if (result == PendingNotificationActionResult.Busy)
            {
                if (!_entries.TryGetValue(token, out current) || !ReferenceEquals(current, entry)) return (Outcome.Missing, entry);
                if (Expire(token)) return (Outcome.Expired, entry);
                return (Outcome.Busy, entry);
            }
            _entries.TryRemove(new KeyValuePair<string, Entry>(token, entry));
            return (result == PendingNotificationActionResult.Completed ? Outcome.Completed : Outcome.Unavailable, entry);
        }
        finally { Interlocked.Exchange(ref entry.InFlight, 0); }
    }

    internal enum Outcome { Completed, Opened, Busy, Unavailable, Expired, Missing, InvalidAction }
    internal sealed class Entry(string token, DateTimeOffset expiresAt, PendingNotificationKind kind,
        Func<Task<PendingNotificationActionResult>> approve, Func<Task<PendingNotificationActionResult>> decline, Func<Task> open)
    {
        internal readonly string Token = token;
        internal readonly DateTimeOffset ExpiresAt = expiresAt;
        internal readonly PendingNotificationKind Kind = kind;
        internal readonly Func<Task<PendingNotificationActionResult>> Approve = approve;
        internal readonly Func<Task<PendingNotificationActionResult>> Decline = decline;
        internal readonly Func<Task> Open = open;
        internal int InFlight;
    }
}
