using ChatAgentBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatAgentBridge_Desktop.ExecutionUi;

/// <summary>Collects on producer threads; updates bindings at most five times per second, less often for large text.</summary>
public sealed partial class LiveOutputViewModel : ObservableObject, IDisposable
{
    private readonly object _gate = new();
    private readonly ExecutionOutputBuffer _buffer = new();
    private DispatcherQueueTimer? _timer;
    private long _generation;
    private long _displayedVersion = -1;
    private bool _accepting;
    private bool _disposed;
    private string? _lastStream;
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMilliseconds(200);
    private const int MediumTextLength = 16 * 1024;
    private const int LargeTextLength = 48 * 1024;

    /// <summary>
    /// Set by the hosting view. While it returns false (not loaded, collapsed or hidden) timer
    /// ticks skip the text update; the next tick after the view is shown catches up.
    /// A null value means no view restricts updates.
    /// </summary>
    public Func<bool>? IsViewVisible { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Text { get; set; } = "";
    public bool HasOutput => Text.Length > 0;
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsCancellationPending { get; set; }
    [ObservableProperty] public partial bool IsTruncated { get; set; }
    [ObservableProperty] public partial bool FollowOutput { get; set; } = true;

    public IProgress<AgentRunProgress> Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _generation++;
            _buffer.Clear();
            _lastStream = null;
            _accepting = true;
        }
        FollowOutput = true;
        IsCancellationPending = false;
        Flush(ignoreVisibility: true);
        IsActive = true;
        _timer ??= CreateTimer();
        _timer.Start();
        return new RunProgress(this, _generation);
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = App.DispatcherQueue.CreateTimer();
        timer.Interval = BaseInterval;
        timer.Tick += (_, _) => Flush();
        return timer;
    }

    private void Append(long generation, AgentRunProgress progress)
    {
        if (progress is null || string.IsNullOrEmpty(progress.Text)) return;
        lock (_gate)
        {
            if (_disposed || !_accepting || generation != _generation) return;
            var stream = progress.Stream is "stdout" or "stderr" or "activity" ? progress.Stream : "activity";
            if (_lastStream != stream)
            {
                _buffer.Append((_lastStream is null ? "" : Environment.NewLine) + "[" + stream + "] ");
                _lastStream = stream;
            }
            // The observer never queues dispatcher work, even if a CLI floods stdout.
            _buffer.Append(progress.Text.Length > ExecutionOutputBuffer.DefaultCapacity
                ? progress.Text[^ExecutionOutputBuffer.DefaultCapacity..] : progress.Text);
        }
    }

    public void Finish()
    {
        lock (_gate) _accepting = false;
        _timer?.Stop();
        // A paused empty view must remain reachable after the running indicator disappears.
        Flush(force: !HasOutput, ignoreVisibility: true);
        IsActive = false;
    }

    private void Flush(bool force = false, bool ignoreVisibility = false)
    {
        // Freeze the displayed snapshot while the user reads or selects older text.
        // Producers continue collecting into the bounded tail without dispatcher work.
        if (!force && !FollowOutput) return;
        // Replacing up to 64K characters in a TextBox is costly; do not do it for a hidden view.
        if (!ignoreVisibility && IsViewVisible is { } isVisible && !isVisible()) return;
        var snapshot = _buffer.SnapshotIfChanged(_displayedVersion);
        if (snapshot is null) return;
        _displayedVersion = snapshot.Version;
        Text = snapshot.Text;
        IsTruncated = snapshot.IsTruncated;
        // Each replacement relayouts the whole TextBox, so slow the cadence as the text grows.
        if (_timer is not null)
            _timer.Interval = snapshot.Text.Length > LargeTextLength ? TimeSpan.FromMilliseconds(1000)
                : snapshot.Text.Length > MediumTextLength ? TimeSpan.FromMilliseconds(500) : BaseInterval;
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _lastStream = null;
            _displayedVersion = _buffer.SnapshotIfChanged(-1)!.Version;
        }
        Text = "";
        IsTruncated = false;
    }

    partial void OnFollowOutputChanged(bool value)
    {
        if (value) Flush();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _accepting = false; _generation++; }
        _timer?.Stop();
        _timer = null;
    }

    private sealed class RunProgress(LiveOutputViewModel owner, long generation) : IProgress<AgentRunProgress>
    {
        public void Report(AgentRunProgress value) => owner.Append(generation, value);
    }
}
