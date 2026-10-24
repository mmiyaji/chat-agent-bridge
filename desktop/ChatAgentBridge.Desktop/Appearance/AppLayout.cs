using ChatAgentBridge.Core.Appearance;
using Microsoft.UI.Dispatching;

namespace ChatAgentBridge_Desktop.Appearance;

/// <summary>Holds user-adjusted pane widths and saves them shortly after the last change.</summary>
public sealed class AppLayout(LayoutPreferencesStore store)
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private DispatcherQueueTimer? _saveTimer;
    private Task _saving = Task.CompletedTask;

    public LayoutPreferences Current { get; private set; } = new();

    public async Task InitializeAsync()
    {
        try { Current = await store.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Current = new(); }
    }

    /// <summary>Applies a change immediately and schedules one save for a burst of drag updates.</summary>
    public void Update(Func<LayoutPreferences, LayoutPreferences> change)
    {
        var next = change(Current).Normalized();
        if (next == Current) return;
        Current = next;
        if (_saveTimer is null)
        {
            _saveTimer = App.DispatcherQueue.CreateTimer();
            _saveTimer.Interval = SaveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => _ = SaveAsync();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes a pending change now, for example when the window closes.</summary>
    public Task FlushAsync()
    {
        if (_saveTimer is not { IsRunning: true }) return _saving;
        _saveTimer.Stop();
        return SaveAsync();
    }

    private Task SaveAsync()
    {
        var snapshot = Current;
        // Pane widths are a convenience; a failed write keeps the in-memory layout for this session.
        _saving = _saving.ContinueWith(async _ =>
        {
            try { await store.SaveAsync(snapshot).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }, TaskScheduler.Default).Unwrap();
        return _saving;
    }
}
