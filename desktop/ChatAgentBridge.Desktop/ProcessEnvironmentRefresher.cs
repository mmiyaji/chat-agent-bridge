using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ChatAgentBridge_Desktop;

/// <summary>Refreshes this process' environment from the current user's Windows environment block.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessEnvironmentRefresher
{
    private readonly HashSet<string> _persistedNames;
    private readonly Dictionary<string, string?> _startupCabValues;

    public ProcessEnvironmentRefresher()
    {
        _persistedNames = ReadPersistedNames();
        _startupCabValues = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string key && key.StartsWith("CAB_", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !_persistedNames.Contains((string)entry.Key) ||
                string.Equals((string)entry.Key, "CAB_WINUI_DATA_DIR", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string, StringComparer.OrdinalIgnoreCase);
    }

    public void Refresh()
    {
        // Prepare the complete result first. Process-only values survive, persisted variables
        // removed since startup are deleted, and app launch overrides such as CAB_WINUI_DATA_DIR remain.
        var current = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => !((string)entry.Key).StartsWith("=", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
        var fresh = ReadUserEnvironment();
        var desired = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _persistedNames)
            if (!fresh.ContainsKey(name)) desired.Remove(name);
        foreach (var entry in fresh) desired[entry.Key] = entry.Value;
        foreach (var entry in _startupCabValues) desired[entry.Key] = entry.Value ?? string.Empty;

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in current.Keys)
                if (!desired.ContainsKey(name))
                {
                    touched.Add(name);
                    Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
                }
            foreach (var entry in desired)
                if (!string.Equals(Environment.GetEnvironmentVariable(entry.Key, EnvironmentVariableTarget.Process), entry.Value, StringComparison.Ordinal))
                {
                    touched.Add(entry.Key);
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.Process);
                }
        }
        catch
        {
            foreach (var name in touched)
            {
                try
                {
                    current.TryGetValue(name, out var oldValue);
                    Environment.SetEnvironmentVariable(name, oldValue, EnvironmentVariableTarget.Process);
                }
                catch { /* Preserve the original failure without logging environment values. */ }
            }
            throw;
        }

        _persistedNames.Clear();
        _persistedNames.UnionWith(fresh.Keys);
    }

    private static HashSet<string> ReadPersistedNames()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var userEnvironment = user.OpenSubKey("Environment", writable: false);
            if (userEnvironment is not null) names.UnionWith(userEnvironment.GetValueNames());

            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var machineEnvironment = machine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: false);
            if (machineEnvironment is not null) names.UnionWith(machineEnvironment.GetValueNames());
            names.RemoveWhere(name => name.StartsWith("=", StringComparison.Ordinal));
            return names;
        }
        catch
        {
            // Startup must remain available if registry access is restricted. An empty baseline
            // means refresh overlays current values but does not try to delete unknown names.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ReadUserEnvironment()
    {
        using var token = OpenCurrentProcessToken();
        if (!CreateEnvironmentBlock(out var block, token, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (true)
            {
                var chars = new List<char>();
                short value;
                while ((value = Marshal.ReadInt16(block, offset)) != 0)
                {
                    chars.Add((char)value);
                    offset += sizeof(char);
                }
                offset += sizeof(char);
                if (chars.Count == 0) break;
                var line = new string(chars.ToArray());
                var splitAt = line[0] == '=' ? line.IndexOf('=', 1) : line.IndexOf('=');
                if (line[0] != '=' && splitAt > 0) values[line[..splitAt]] = line[(splitAt + 1)..];
            }
            return values;
        }
        finally { DestroyEnvironmentBlock(block); }
    }

    private static SafeAccessTokenHandle OpenCurrentProcessToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x000A, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return token;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
