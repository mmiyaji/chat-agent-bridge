using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChatAgentBridge.Core.Deployment;
using ChatAgentBridge.Core.Updates;
using ChatAgentBridge_Desktop.Deployment;

internal static class PortableIdentitySetupRunner
{
    private const string DesktopFileName = "ChatAgentBridge.Desktop.exe";
    private const string HelperFileName = "ChatAgentBridge.Updater.exe";

    public static async Task<int> RunAsync(string planFile, string nonce)
    {
        PortableIdentitySetupPlan? plan = null;
        Process? parent = null;
        var parentVerified = false;
        var parentHasExited = false;
        var setupCommitted = false;
        var restartAttempted = false;
        try
        {
            plan = await PortableIdentitySetupPlan.ReadAsync(planFile);
            if (!string.Equals(plan.Nonce, nonce, StringComparison.Ordinal)) throw new InvalidDataException("Notification setup nonce does not match.");
            await ValidateHelperAsync(plan);
            var bundle = PortableIdentityBundle.LoadValidated(plan.InstallDirectory);
            plan.ValidateBundle(bundle);
            parent = Process.GetProcessById(plan.ParentProcessId);
            var expectedDesktop = PortableUpdatePaths.Resolve(plan.InstallDirectory, DesktopFileName);
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTimeUtcTicks ||
                parent.MainModule?.FileName is not { } parentPath || !SamePath(parentPath, expectedDesktop))
                throw new InvalidDataException("The notification setup parent is not the expected application instance.");
            parentVerified = true;
            PortableUpdatePaths.EnsureNoReparsePoints(plan.ReadyFile);
            await using (var ready = new FileStream(plan.ReadyFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await ready.WriteAsync(Encoding.UTF8.GetBytes(plan.Nonce));

            var deadline = Stopwatch.StartNew();
            var committed = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                parentHasExited = parent.HasExited;
                committed = false;
                if (File.Exists(plan.CommitFile))
                {
                    PortableUpdatePaths.EnsureNoReparsePoints(plan.CommitFile);
                    try
                    {
                        using var commit = new FileStream(plan.CommitFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (commit.Length > 32) throw new InvalidDataException("Invalid notification setup commit marker.");
                        if (commit.Length == 32)
                        {
                            using var text = new StreamReader(commit, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                            committed = string.Equals(await text.ReadToEndAsync(), plan.Nonce, StringComparison.Ordinal);
                            if (!committed) throw new InvalidDataException("Notification setup commit nonce does not match.");
                        }
                    }
                    catch (IOException) { /* The parent may still be writing its commit marker. */ }
                }
                if (parentHasExited && committed) break;
                await Task.Delay(100);
            }
            if (!parentHasExited || !committed) throw new TimeoutException("Notification setup was not committed after the application exited.");
            setupCommitted = true;

            // The lifetime lease prevents registration while another copy in this installation is running.
            using (PortableApplicationLease.Open(plan.InstallDirectory, exclusive: true))
            {
                plan.Validate();
                bundle = PortableIdentityBundle.LoadValidated(plan.InstallDirectory);
                plan.ValidateBundle(bundle);
                if (plan.Operation == "register")
                    await PortableIdentityRegistration.RegisterAsync(plan.InstallDirectory, bundle, plan.TrustCertificate, plan.AllowRelocate);
                else
                    await PortableIdentityRegistration.UnregisterAsync(plan.InstallDirectory, bundle);
            }
            await TryWriteResultAsync(plan, success: true, error: null);
            restartAttempted = true;
            Restart(plan);
            return 0;
        }
        catch (Exception error)
        {
            if (parentVerified && parent is not null)
            {
                try { parentHasExited = parent.HasExited; }
                catch (InvalidOperationException) { }
            }
            if (plan is not null)
            {
                await TryWriteResultAsync(plan, success: false, error);
                if (parentHasExited && setupCommitted)
                {
                    if (!restartAttempted)
                    {
                        try { Restart(plan); }
                        catch { /* Preserve the result for recovery; show the original setup error once. */ }
                    }
                    ShowFailure(plan, error);
                }
            }
            return 1;
        }
        finally { parent?.Dispose(); }
    }

    private static async Task ValidateHelperAsync(PortableIdentitySetupPlan plan)
    {
        var expectedHelper = PortableUpdatePaths.Resolve(plan.WorkDirectory, HelperFileName);
        var actualHelper = Environment.ProcessPath ?? throw new InvalidDataException("The notification setup executable path is unavailable.");
        PortableUpdatePaths.EnsureNoReparsePoints(actualHelper);
        if (!SamePath(expectedHelper, actualHelper)) throw new InvalidDataException("Notification setup must run from its own work directory.");
        var installedHelper = PortableUpdatePaths.Resolve(plan.InstallDirectory, HelperFileName);
        if (!string.Equals(await PortableUpdatePaths.HashAsync(actualHelper), await PortableUpdatePaths.HashAsync(installedHelper), StringComparison.Ordinal))
            throw new InvalidDataException("The notification setup helper does not match this installation.");
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Restart(PortableIdentitySetupPlan plan)
    {
        var desktop = PortableUpdatePaths.Resolve(plan.InstallDirectory, DesktopFileName);
        PortableUpdatePaths.EnsureNoReparsePoints(plan.DataDirectory);
        var start = new ProcessStartInfo(desktop) { UseShellExecute = false, WorkingDirectory = plan.InstallDirectory };
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(plan.DataDirectory);
        start.Environment.Remove("CAB_PORTABLE_UPDATE_READY_EVENT");
        using var process = Process.Start(start) ?? throw new IOException("The application could not be restarted.");
    }

    private static async Task TryWriteResultAsync(PortableIdentitySetupPlan plan, bool success, Exception? error)
    {
        try
        {
            PortableUpdatePaths.EnsureNoReparsePoints(plan.ResultFile);
            var text = JsonSerializer.Serialize(new
            {
                Success = success, plan.Operation,
                ErrorHResult = error is null ? null : HResult(error), ErrorType = error?.GetType().Name
            });
            await File.WriteAllTextAsync(plan.ResultFile, text);
            var visibleResult = Path.Combine(plan.DataDirectory, "portable-notification-result.json");
            PortableUpdatePaths.EnsureNoReparsePoints(visibleResult);
            var temporary = visibleResult + "." + plan.Nonce + ".tmp";
            PortableUpdatePaths.EnsureNoReparsePoints(temporary);
            await File.WriteAllTextAsync(temporary, text);
            File.Move(temporary, visibleResult, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static string HResult(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is COMException) return $"0x{current.HResult:X8}";
        return $"0x{error.GetBaseException().HResult:X8}";
    }

    private static void ShowFailure(PortableIdentitySetupPlan plan, Exception error)
    {
        var code = HResult(error);
        var japanese = plan.Language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true;
        var text = japanese
            ? "通知用の登録を完了できませんでした。アプリとデータは変更していません。"
            : "Notification registration could not finish. The application files and data were preserved.";
        if (code == "0x800B0109")
            text += japanese
                ? "\nWindows が現在のユーザーの証明書の信頼を認識しませんでした。信頼された認証局による署名が必要です。"
                : "\nWindows did not recognize the current-user certificate trust. A certificate from a trusted certificate authority is required.";
        Native.MessageBoxW(0, text + "\n\n" + code, "Chat Agent Bridge", 0x10);
    }
}
