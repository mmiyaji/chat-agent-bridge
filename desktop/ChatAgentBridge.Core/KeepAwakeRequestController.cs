namespace ChatAgentBridge.Core;

public interface IKeepAwakeRequest : IDisposable
{
    void SetActive(bool active);
    void ResetAfterResume();
}

/// <summary>Applies only state transitions and reacquires the OS request after resume.</summary>
public sealed class KeepAwakeRequestController(IKeepAwakeRequest request) : IDisposable
{
    private bool _active, _disposed;
    public bool IsActive => _active;

    public void Update(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active == active) return;
        request.SetActive(active);
        _active = active;
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request.ResetAfterResume();
        _active = false;
    }

    public void MarkInactiveAfterFailure() => _active = false;

    public void Dispose()
    {
        if (_disposed) return;
        if (_active) { try { request.SetActive(false); } catch { } }
        _active = false;
        _disposed = true;
        request.Dispose();
    }
}
