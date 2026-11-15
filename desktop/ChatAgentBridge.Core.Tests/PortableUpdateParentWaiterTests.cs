using System.Diagnostics;
using ChatAgentBridge.Core.Updates;

namespace ChatAgentBridge.Core.Tests;

public sealed class PortableUpdateParentWaiterTests
{
    [Fact]
    public void Refuses_a_live_process_with_a_different_start_time()
    {
        using var current = Process.GetCurrentProcess();
        var wrongStart = current.StartTime.ToUniversalTime().Ticks + 1;

        Assert.Throws<InvalidDataException>(() => PortableUpdateParentWaiter.OpenExpectedParent(current.Id, wrongStart));
    }

    [Fact]
    public async Task Waits_for_the_verified_parent_and_handles_exit_before_wait()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "set /p CAB_UPDATE_TEST_INPUT=" : "read line");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test parent.");
        try
        {
            using var verified = PortableUpdateParentWaiter.OpenExpectedParent(child.Id, child.StartTime.ToUniversalTime().Ticks);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortableUpdateParentWaiter.WaitForExitAsync(verified, timeout.Token));
            Assert.False(child.HasExited);

            await child.StandardInput.WriteLineAsync("continue");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // A parent that exits after its handle was verified but before the wait must be accepted.
            await PortableUpdateParentWaiter.WaitForExitAsync(verified).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
}
