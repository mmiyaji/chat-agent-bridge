using System.Diagnostics;

namespace ChatAgentBridge.Core.Updates;

/// <summary>Holds a verified parent process handle before the updater announces it is ready.</summary>
public static class PortableUpdateParentWaiter
{
    public static Process OpenExpectedParent(int processId, long startTimeUtcTicks)
    {
        var parent = Process.GetProcessById(processId);
        try
        {
            // Pin the process object before checking its identity. A later PID reuse must not cause the updater
            // to wait on or mistake another process for the application that initiated this update.
            _ = parent.Handle;
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks)
                throw new InvalidDataException("Update parent is no longer the expected running process.");
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    /// <summary>The application kills an uncommitted helper; a committed helper must wait for shutdown.</summary>
    public static Task WaitForExitAsync(Process parent, CancellationToken cancellationToken = default) =>
        parent.WaitForExitAsync(cancellationToken);
}
