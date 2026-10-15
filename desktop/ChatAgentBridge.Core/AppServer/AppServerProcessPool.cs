using System.Diagnostics;

namespace ChatAgentBridge.Core.AppServer;

/// <summary>A small process pool for native App Server sessions. Only idle processes expire.</summary>
internal sealed class AppServerProcessPool : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Timer _timer;
    private readonly TimeSpan _idleTtl;
    private bool _shutdown;

    public AppServerProcessPool(TimeSpan? idleTtl = null)
    {
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(2);
        _timer = new Timer(_ => ExpireIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<Lease> AcquireAsync(string key, IAppServerProcessFactory factory, string executable,
        string workspace, Func<IAppServerRpcClient, CancellationToken, Task> initialize, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        Entry? entry = null;
        List<Entry> evicted = [];
        lock (_sync)
        {
            if (_shutdown) { _slots.Release(); throw new ObjectDisposedException(nameof(AppServerProcessPool)); }
            foreach (var stale in _entries.Values.Where(e => !e.Busy && IsDead(e.Process)).ToArray())
            { _entries.Remove(stale.Key); evicted.Add(stale); }
            foreach (var stale in _entries.Values.Where(e => !e.Busy && !string.Equals(e.Key, key, StringComparison.Ordinal)
                         && SameProcessBase(e.Key, key)).ToArray())
            { _entries.Remove(stale.Key); evicted.Add(stale); }
            if (_entries.TryGetValue(key, out entry) && !entry.Busy)
            {
                entry.Busy = true;
                entry.Reused = true;
            }
            else
            {
                while (_entries.Count >= 2)
                {
                    var oldest = _entries.Values.Where(e => !e.Busy).OrderBy(e => e.LastUsed).FirstOrDefault();
                    if (oldest is null) break;
                    _entries.Remove(oldest.Key); evicted.Add(oldest);
                }
                entry = new Entry(key) { Busy = true };
                _entries[key] = entry;
            }
        }
        foreach (var old in evicted) if (old.Process is { } oldProcess) KillAndDispose(oldProcess);

        if (entry.Process is not null)
            return new Lease(this, entry, _slots, true, null, null);

        var start = Stopwatch.StartNew();
        IAppServerProcess? startedProcess = null;
        var ownershipTransferred = false;
        try
        {
            startedProcess = await factory.StartAsync(executable, workspace, ct).ConfigureAwait(false);
            entry.StartMilliseconds = start.Elapsed.TotalMilliseconds;
            lock (_sync)
            {
                if (_shutdown || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                    throw new ObjectDisposedException(nameof(AppServerProcessPool));
                entry.Process = startedProcess;
                ownershipTransferred = true;
            }
            var init = Stopwatch.StartNew();
            await initialize(startedProcess.Client, ct).ConfigureAwait(false);
            entry.InitializeMilliseconds = init.Elapsed.TotalMilliseconds;
            lock (_sync)
                if (_shutdown || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                    throw new ObjectDisposedException(nameof(AppServerProcessPool));
            return new Lease(this, entry, _slots, false, entry.StartMilliseconds, entry.InitializeMilliseconds);
        }
        catch
        {
            IAppServerProcess? dispose = null;
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
                dispose = entry.Process;
                entry.Process = null;
            }
            if (dispose is not null) KillAndDispose(dispose);
            else if (!ownershipTransferred && startedProcess is not null) KillAndDispose(startedProcess);
            _slots.Release();
            throw;
        }
    }

    private void Release(Entry entry, bool keep)
    {
        IAppServerProcess? dispose = null;
        lock (_sync)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                entry.Busy = false;
                entry.LastUsed = DateTimeOffset.UtcNow;
                if (!keep || _shutdown || IsDead(entry.Process))
                { _entries.Remove(entry.Key); dispose = entry.Process; }
            }
            else dispose = entry.Process;
        }
        if (dispose is not null) KillAndDispose(dispose);
    }

    private void ExpireIdle()
    {
        List<IAppServerProcess> expired = [];
        lock (_sync)
        {
            foreach (var entry in _entries.Values.Where(e => !e.Busy && (DateTimeOffset.UtcNow - e.LastUsed >= _idleTtl || IsDead(e.Process))).ToArray())
            { _entries.Remove(entry.Key); if (entry.Process is not null) expired.Add(entry.Process); }
        }
        foreach (var process in expired) KillAndDispose(process);
    }

    public void Dispose()
    {
        List<IAppServerProcess> processes;
        lock (_sync)
        {
            if (_shutdown) return;
            _shutdown = true;
            processes = _entries.Values.Select(e => e.Process).Where(p => p is not null).Cast<IAppServerProcess>().ToList();
            foreach (var entry in _entries.Values) entry.Process = null;
            _entries.Clear();
        }
        _timer.Dispose();
        foreach (var process in processes) KillAndDispose(process);
    }

    private static bool IsDead(IAppServerProcess? process)
        => process is null || process.ExitCode.IsCompleted || process.Client.Closed.IsCompleted;

    private static bool SameProcessBase(string first, string second)
    {
        var a = first.Split('\u001f');
        var b = second.Split('\u001f');
        return a.Length >= 2 && b.Length >= 2 && a[0] == b[0] && a[1] == b[1];
    }

    private static void KillAndDispose(IAppServerProcess process)
    {
        try { process.KillTree(); } catch { }
        _ = DisposeQuietlyAsync(process);
    }

    private static async Task DisposeQuietlyAsync(IAppServerProcess process)
    { try { await process.DisposeAsync().ConfigureAwait(false); } catch { } }

    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public IAppServerProcess? Process { get; set; }
        public bool Busy { get; set; }
        public bool Reused { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public double? StartMilliseconds { get; set; }
        public double? InitializeMilliseconds { get; set; }
    }

    internal sealed class Lease(AppServerProcessPool pool, Entry entry, SemaphoreSlim slots, bool reused,
        double? startMs, double? initializeMs) : IAsyncDisposable
    {
        private int _released;
        public IAppServerProcess Process => entry.Process!;
        public bool Reused { get; } = reused;
        public double? ProcessStartMilliseconds { get; } = startMs;
        public double? InitializeMilliseconds { get; } = initializeMs;
        public ValueTask ReleaseAsync(bool keep)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            { pool.Release(entry, keep); slots.Release(); }
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ReleaseAsync(false);
    }
}
