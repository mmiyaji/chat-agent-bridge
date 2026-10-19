using Windows.ApplicationModel;
using ChatAgentBridge_Desktop.Localization;
#if CAB_PORTABLE
using Microsoft.Win32;
using System.Runtime.InteropServices;
#endif

namespace ChatAgentBridge_Desktop.Startup;

public enum StartupState
{
    Enabled,
    Disabled,
    DisabledByUser,
    DisabledByPolicy,
    Unavailable,
    Error
}

public sealed record StartupTaskStatusResult(StartupState State, string Message)
{
    public bool IsEnabled => State == StartupState.Enabled;
}

/// <summary>Manages the app's manifest-declared, user-controlled Windows login task.</summary>
public sealed class StartupTaskService
{
    public const string TaskId = "ChatAgentBridgeAutoStart";

#if CAB_PORTABLE
    private const string PortableRunValueName = "ChatAgentBridge.Portable";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
#endif

#if CAB_PORTABLE
    public Task<StartupTaskStatusResult> GetStateAsync() => Task.FromResult(GetPortableState());
#else
    public async Task<StartupTaskStatusResult> GetStateAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            return Describe(task.State);
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }
#endif

#if CAB_PORTABLE
    public Task<StartupTaskStatusResult> SetEnabledAsync(bool enabled) => Task.FromResult(SetPortableEnabled(enabled));
#else
    public async Task<StartupTaskStatusResult> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (task.State == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                    return Describe(task.State);
                if (task.State == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
                    return Describe(task.State);

                Windows.ApplicationModel.StartupTaskState state = await task.RequestEnableAsync();
                return Describe(state);
            }

            task.Disable();
            return Describe(task.State);
        }
        catch (Exception)
        {
            return new StartupTaskStatusResult(
                StartupState.Error,
                DesktopText.Get("StartupChangeError"));
        }
    }
#endif

#if CAB_PORTABLE
    private static StartupTaskStatusResult GetPortableState()
    {
        try
        {
            string? expected = BuildPortableCommand(out bool commandTooLong);
            if (expected is null)
                return new(StartupState.Unavailable, DesktopText.Get(commandTooLong
                    ? "StartupPortableCommandTooLong"
                    : "StartupPortableExecutableUnavailable"));

            string? command = ReadPortableCommand();
            if (string.IsNullOrWhiteSpace(command))
                return DescribePortableDisabled();

            string? executable = GetExecutableFromCommand(command);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return new(StartupState.Unavailable, DesktopText.Get("StartupStalePortableEntry"));

            if (!string.Equals(command, expected, StringComparison.OrdinalIgnoreCase))
                return new(StartupState.Unavailable, DesktopText.Get("StartupOtherPortableInstall"));

            return IsDisabledByUser()
                ? new(StartupState.DisabledByUser, DesktopText.Get("StartupDisabledByUser"))
                : new(StartupState.Enabled, DesktopText.Get("StartupEnabled"));
        }
        catch (Exception)
        {
            return new(StartupState.Error, DesktopText.Get("StartupPortableChangeError"));
        }
    }

    private static StartupTaskStatusResult SetPortableEnabled(bool enabled)
    {
        try
        {
            string? existingCommand = ReadPortableCommand();
            string? expectedCommand = BuildPortableCommand(out bool commandTooLong);
            if (expectedCommand is null)
                return new(StartupState.Unavailable, DesktopText.Get(commandTooLong
                    ? "StartupPortableCommandTooLong"
                    : "StartupPortableExecutableUnavailable"));

            if (!string.IsNullOrWhiteSpace(existingCommand))
            {
                string? existingExecutable = GetExecutableFromCommand(existingCommand);
                if (string.IsNullOrWhiteSpace(existingExecutable) || !File.Exists(existingExecutable))
                    return new(StartupState.Unavailable, DesktopText.Get("StartupStalePortableEntry"));

                if (!string.Equals(existingCommand, expectedCommand, StringComparison.OrdinalIgnoreCase))
                    return new(StartupState.Unavailable, DesktopText.Get("StartupOtherPortableInstall"));
            }

            if (enabled)
            {
                // Task Manager's per-user disabled marker is authoritative. Do not
                // rewrite the Run value or the marker behind the user's back.
                if (IsDisabledByUser())
                    return new(StartupState.DisabledByUser, DesktopText.Get("StartupDisabledByUser"));

                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Could not open the current user's Run key.");
                key.SetValue(PortableRunValueName, expectedCommand, RegistryValueKind.String);
                return new(StartupState.Enabled, DesktopText.Get("StartupEnabled"));
            }

            if (!string.IsNullOrWhiteSpace(existingCommand))
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(PortableRunValueName, throwOnMissingValue: false);
            }
            return DescribePortableDisabled();
        }
        catch (Exception)
        {
            return new(StartupState.Error, DesktopText.Get("StartupPortableChangeError"));
        }
    }

    private static string? ReadPortableCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(PortableRunValueName) as string;
    }

    private static bool IsDisabledByUser()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: false);
        if (key?.GetValue(PortableRunValueName) is not byte[] status || status.Length == 0)
            return false;

        // StartupApproved uses 0x03 or 0x07 for disabled; 0x02 is enabled.
        return status[0] is 0x03 or 0x07;
    }

    private static string? BuildPortableCommand(out bool commandTooLong)
    {
        commandTooLong = false;
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            return null;

        string command = QuoteWindowsArgument(Path.GetFullPath(executable));
        string? dataDirectory = Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR");
        string defaultPortableDataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data"));
        if (!string.IsNullOrWhiteSpace(dataDirectory) &&
            !string.Equals(Path.GetFullPath(dataDirectory), defaultPortableDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            command += " --data-dir " + QuoteWindowsArgument(Path.GetFullPath(dataDirectory));
        }

        if (command.Length > 260)
        {
            commandTooLong = true;
            return null;
        }
        return command;
    }

    private static string QuoteWindowsArgument(string argument)
    {
        // The CRT command-line parser treats backslashes before a quote specially;
        // double them and double trailing backslashes so quoted paths round-trip.
        var quoted = new System.Text.StringBuilder(argument.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else
                quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private static string? GetExecutableFromCommand(string command)
    {
        IntPtr arguments = CommandLineToArgvW(command, out int count);
        if (arguments == IntPtr.Zero)
            return null;
        if (count == 0)
        {
            _ = LocalFree(arguments);
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments));
        }
        finally
        {
            _ = LocalFree(arguments);
        }
    }

    private static StartupTaskStatusResult DescribePortableDisabled() =>
        new(StartupState.Disabled, DesktopText.Get("StartupDisabled"));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
#endif

    private static StartupTaskStatusResult Describe(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled => new(StartupState.Enabled, DesktopText.Get("StartupEnabled")),
        Windows.ApplicationModel.StartupTaskState.Disabled => new(StartupState.Disabled, DesktopText.Get("StartupDisabled")),
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => new(StartupState.DisabledByUser, DesktopText.Get("StartupDisabledByUser")),
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => new(StartupState.DisabledByPolicy, DesktopText.Get("StartupDisabledByPolicy")),
        _ => new(StartupState.Unavailable, DesktopText.Get("StartupUnavailable"))
    };

    private static StartupTaskStatusResult Unavailable() => new(
        StartupState.Unavailable,
        DesktopText.Get("StartupTaskUnavailable"));
}
