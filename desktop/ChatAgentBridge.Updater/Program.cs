using ChatAgentBridge.Core.Updates;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;

if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--validate-identity-bundle")
{
    try { PortableIdentityBundle.LoadValidated(args[1]); return 0; }
    catch { return 1; }
}
if (OperatingSystem.IsWindows() && args.Length == 4 && args[0] == "--configure-identity" && args[2] == "--nonce")
    return await PortableIdentitySetupRunner.RunAsync(args[1], args[3]);
if (!OperatingSystem.IsWindows() || args.Length != 4 || args[0] != "--apply" || args[2] != "--nonce") return 2;
PortableUpdatePlan? plan = null;
var parentHasExited = false;
using var workLeaseScope = new WorkLeaseScope();
var starter = new WindowsApplicationStarter();
try
{
    plan = await PortableUpdatePlan.ReadAsync(args[1]);
    if (plan.Nonce != args[3]) throw new InvalidDataException("Update nonce does not match.");
    workLeaseScope.Lease = PortableUpdateCacheCleanup.AcquireWorkLease(plan.WorkDirectory);
    await PortableUpdateApplier.ValidateStagingAsync(plan);
    var probe = Path.Combine(plan.InstallDirectory, ".cab-probe-" + plan.Nonce);
    using (File.Create(probe)) { }
    File.Delete(probe);
    await File.WriteAllTextAsync(plan.ReadyFile, plan.Nonce);
    using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTimeUtcTicks)
                await parent.WaitForExitAsync(wait.Token);
        }
        catch (ArgumentException) { /* Parent has already exited. */ }
    }
    parentHasExited = true;
    var result = await new PortableUpdateApplier(starter).ApplyAsync(plan);
    if (result.Success) CleanVerifiedPayload(plan);
    if (!result.Success) ShowFailure(plan, result.RolledBack);
    return result.Success ? 0 : 1;
}
catch (Exception error)
{
    if (plan is not null)
    {
        try
        {
            await File.WriteAllTextAsync(plan.ResultFile,
                JsonSerializer.Serialize(new PortableUpdateResult(false, false, plan.ReleaseVersion, error.GetType().Name)));
            if (parentHasExited && !starter.Restored)
                await starter.StartRestoredAsync(Path.Combine(plan.InstallDirectory, "ChatAgentBridge.Desktop.exe"), plan.DataDirectory, CancellationToken.None);
        }
        catch { /* Preserve the backup and plan for manual recovery. */ }
        if (parentHasExited) ShowFailure(plan, false);
    }
    return 1;
}

static void CleanVerifiedPayload(PortableUpdatePlan plan)
{
    try { PortableUpdateCacheCleanup.CleanSuccessfulPayload(plan); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
}

static void ShowFailure(PortableUpdatePlan plan, bool rolledBack)
{
    var japanese = plan.Language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true;
    var text = japanese
        ? (rolledBack ? "更新後の起動を確認できなかったため、以前の版に戻しました。" : "更新を完了できませんでした。更新結果とバックアップを次の場所に保存しています。")
        : (rolledBack ? "The update did not start successfully. The previous version has been restored." : "The update could not finish. The result and any backup are available here:");
    Native.MessageBoxW(0, text + "\n\n" + plan.WorkDirectory, "Chat Agent Bridge", 0x10);
}

sealed class WindowsApplicationStarter : IPortableApplicationStarter
{
    public bool Restored { get; private set; }

    public async Task<bool> StartAndConfirmAsync(string executable, string dataDirectory, string healthEventName, CancellationToken cancellationToken)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, healthEventName);
        var start = CreateStart(executable, dataDirectory);
        start.Environment["CAB_PORTABLE_UPDATE_READY_EVENT"] = healthEventName;
        using var process = Process.Start(start) ?? throw new IOException("The updated application did not start.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
        {
            if (ready.WaitOne(0)) return true;
            await Task.Delay(100, cancellationToken);
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        return false;
    }

    public Task StartRestoredAsync(string executable, string dataDirectory, CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStart(executable, dataDirectory)) ?? throw new IOException("The previous application did not start.");
        Restored = true;
        return Task.CompletedTask;
    }

    private static ProcessStartInfo CreateStart(string executable, string dataDirectory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(dataDirectory);
        // A restored process must not inherit a stale startup event.
        start.Environment.Remove("CAB_PORTABLE_UPDATE_READY_EVENT");
        return start;
    }
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MessageBoxW(nint owner, string text, string title, uint type);
}

sealed class WorkLeaseScope : IDisposable
{
    public FileStream? Lease { get; set; }
    public void Dispose() => Lease?.Dispose();
}
