using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ChatAgentBridge_Desktop;

namespace ChatAgentBridge.Core.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection { }

[Collection("Process environment")]
public sealed class ProcessEnvironmentRefresherTests
{
    [Fact]
    public void Refresh_UpdatesPersistedVariablesAndTracksNewNamesForLaterRemoval()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var existingName = $"CAB_REFRESH_TEST_{suffix}_EXISTING";
        var addedName = $"CAB_REFRESH_TEST_{suffix}_ADDED";
        var processSnapshot = CaptureProcessEnvironment();
        var userSnapshot = new Dictionary<string, string?>
        {
            [existingName] = Environment.GetEnvironmentVariable(existingName, EnvironmentVariableTarget.User),
            [addedName] = Environment.GetEnvironmentVariable(addedName, EnvironmentVariableTarget.User)
        };

        try
        {
            Environment.SetEnvironmentVariable(existingName, "old-value", EnvironmentVariableTarget.User);
            var refresher = new ProcessEnvironmentRefresher();
            Environment.SetEnvironmentVariable(existingName, "changed-value", EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(addedName, "added-value", EnvironmentVariableTarget.User);

            refresher.Refresh();
            Assert.Equal("changed-value", Environment.GetEnvironmentVariable(existingName));
            Assert.Equal("added-value", Environment.GetEnvironmentVariable(addedName));

            Environment.SetEnvironmentVariable(existingName, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(addedName, null, EnvironmentVariableTarget.User);
            refresher.Refresh();
            Assert.Null(Environment.GetEnvironmentVariable(existingName));
            Assert.Null(Environment.GetEnvironmentVariable(addedName));
        }
        finally
        {
            try { RestoreUserEnvironment(userSnapshot); }
            finally { RestoreProcessEnvironment(processSnapshot); }
        }
    }

    [Fact]
    public void Refresh_ReplacesInheritedPathAndKeepsProcessOnlyOverrides()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var fakePath = $"C:\\cab-refresh-fake-{suffix}";
        var cabName = $"CAB_REFRESH_TEST_{suffix}";
        var otherName = $"PROCESS_ONLY_FIXTURE_{suffix}";
        var processSnapshot = CaptureProcessEnvironment();
        try
        {
            var expectedPath = ReadWindowsEnvironmentBlock()["PATH"];
            Environment.SetEnvironmentVariable("PATH", fakePath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", $"cab-data-{suffix}", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(cabName, $"cab-only-{suffix}", EnvironmentVariableTarget.Process);
            var refresher = new ProcessEnvironmentRefresher();
            Environment.SetEnvironmentVariable(otherName, $"process-only-{suffix}", EnvironmentVariableTarget.Process);

            refresher.Refresh();

            var refreshedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Assert.True(string.Equals(expectedPath, refreshedPath, StringComparison.OrdinalIgnoreCase),
                "The process PATH did not match the current Windows user environment.");
            Assert.False(refreshedPath.Contains(fakePath, StringComparison.OrdinalIgnoreCase),
                "The inherited test PATH marker remained after refresh.");
            Assert.Equal($"cab-data-{suffix}", Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR"));
            Assert.Equal($"cab-only-{suffix}", Environment.GetEnvironmentVariable(cabName));
            Assert.Equal($"process-only-{suffix}", Environment.GetEnvironmentVariable(otherName));
        }
        finally { RestoreProcessEnvironment(processSnapshot); }
    }

    [Fact]
    public async Task Refresh_MakesNewUserValuesAvailableToFutureChildProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"CAB_REFRESH_TEST_{suffix}_CHILD";
        var value = $"child-value-{suffix}";
        var processSnapshot = CaptureProcessEnvironment();
        var userSnapshot = new Dictionary<string, string?>
        {
            [name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        };
        try
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            new ProcessEnvironmentRefresher().Refresh();

            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            using var child = new Process
            {
                StartInfo = new ProcessStartInfo(commandProcessor)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            child.StartInfo.ArgumentList.Add("/d");
            child.StartInfo.ArgumentList.Add("/c");
            child.StartInfo.ArgumentList.Add($"echo %{name}%");
            Assert.True(child.Start());
            var outputTask = child.StandardOutput.ReadToEndAsync();
            var errorTask = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("The environment inheritance check process timed out.");
            }
            var output = await outputTask;
            var error = await errorTask;
            Assert.Equal(0, child.ExitCode);
            Assert.True(string.IsNullOrEmpty(error), "The child process wrote to standard error.");
            Assert.Equal(value, output.Trim());
        }
        finally
        {
            try { RestoreUserEnvironment(userSnapshot); }
            finally { RestoreProcessEnvironment(processSnapshot); }
        }
    }

    private static Dictionary<string, string> CaptureProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => !((string)entry.Key).StartsWith("=", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);

    private static void RestoreProcessEnvironment(IReadOnlyDictionary<string, string> snapshot)
    {
        foreach (var key in CaptureProcessEnvironment().Keys.Where(key => !snapshot.ContainsKey(key)))
            Environment.SetEnvironmentVariable(key, null, EnvironmentVariableTarget.Process);
        foreach (var entry in snapshot)
            Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.Process);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreUserEnvironment(IReadOnlyDictionary<string, string?> snapshot)
    {
        foreach (var entry in snapshot)
            Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.User);
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> ReadWindowsEnvironmentBlock()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x000A, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!CreateEnvironmentBlock(out var block, token, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var offset = 0;
                while (true)
                {
                    var chars = new List<char>();
                    short current;
                    while ((current = Marshal.ReadInt16(block, offset)) != 0)
                    {
                        chars.Add((char)current);
                        offset += sizeof(char);
                    }
                    offset += sizeof(char);
                    if (chars.Count == 0) break;
                    var line = new string(chars.ToArray());
                    var split = line[0] == '=' ? line.IndexOf('=', 1) : line.IndexOf('=');
                    if (line[0] != '=' && split > 0) result[line[..split]] = line[(split + 1)..];
                }
                return result;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
