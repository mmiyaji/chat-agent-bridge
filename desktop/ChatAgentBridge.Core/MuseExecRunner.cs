using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Core.Localization;

namespace ChatAgentBridge.Core;

internal static class MuseExecRunner
{
    private const int MaxStderrCharacters = 64 * 1024;

    internal static void CleanupAbandonedPrompts()
    {
        if (OperatingSystem.IsWindows()) MusePromptFile.CleanupAbandoned();
    }

    internal static async Task<AgentRunResult> RunAsync(BridgeSettings settings, string workspace, string prompt,
        IProgress<AgentRunProgress>? progress, CancellationToken cancellationToken)
    {
        AgentInvocation invocation;
        try { invocation = AgentProfile.BuildInvocation(settings); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return Failure("MuseConfigurationInvalid"); }

        var sessionId = settings.AuthorizedCliSessionId;
        if (sessionId is null) sessionId = Guid.CreateVersion7().ToString("D");
        if (!Guid.TryParseExact(sessionId, "D", out var parsedId))
            return Failure("MuseConfigurationInvalid");
        sessionId = parsedId.ToString("D");

        MusePromptFile? promptFile = null;
        Process? process = null;
        Task? stdoutTask = null;
        Task? stderrTask = null;
        var timer = Stopwatch.StartNew();
        try
        {
            try { MusePromptFile.EnsureOutsideWorkspace(workspace); }
            catch (MusePromptIsolationException) { return Failure("MusePromptIsolationUnsafe"); }
            try { promptFile = MusePromptFile.Create(prompt); }
            catch (MusePromptIsolationException)
            { return Failure("MusePromptIsolationUnsafe"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            { return Failure("MusePromptStoreUnavailable"); }
            string resolvedExecutable;
            try { resolvedExecutable = MuseExecutableResolver.Resolve(invocation.Executable); }
            catch (InvalidOperationException) { return Failure("MuseExecutableInvalid"); }
            var start = new ProcessStartInfo
            {
                FileName = resolvedExecutable,
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            var args = invocation.Arguments.ToList();
            RemoveOption(args, "--prompt-file");
            RemoveOption(args, "--session-id");
            if (!args.Contains("exec", StringComparer.Ordinal)) args.Insert(0, "exec");
            if (!args.Contains("--json", StringComparer.Ordinal)) args.Add("--json");
            args.Add("--prompt-file"); args.Add(promptFile.Path);
            args.Add("--session-id"); args.Add(sessionId);
            foreach (var argument in args) start.ArgumentList.Add(argument);

            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start()) return Failure("MuseRunFailed");
            var processStartMilliseconds = timer.Elapsed.TotalMilliseconds;
            try { IncomingAttachmentRecovery.TrackProcess(process); }
            catch { TryKill(process); throw; }
            using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

            // Muse exec has no prompt stdin channel. Close it immediately; API-key stdin is never used.
            process.StandardInput.Close();
            var parser = new MuseOutputParser(sessionId);
            stdoutTask = ReadJsonLinesAsync(process.StandardOutput, parser, process, cancellationToken);
            stderrTask = DrainFixedAsync(process.StandardError);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
                await DrainAfterKill(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var parsed = parser.Complete(process.ExitCode == 0);
            var success = process.ExitCode == 0 && parsed.HasResult && !parsed.TurnFailed;
            var error = success ? "" : parsed.FailureGuidance ?? CoreText.Get(parser.SessionMismatch ? "MuseSessionMismatch" : "MuseRunFailed");
            if (success && parsed.FinalMessage is { Length: > 0 } finalMessage)
                foreach (var update in AgentRunProgressParser.Split("stdout", finalMessage)) SafeReport(progress, update);
            return new AgentRunResult(success, success ? 0 : process.ExitCode == 0 ? -1 : process.ExitCode,
                parsed.FinalMessage ?? "", error, HasOutputTruncated: false)
            {
                CliSession = parsed,
                Usage = parsed.Usage,
                Metrics = new AgentExecutionMetrics { ProcessStartMilliseconds = processStartMilliseconds }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null) await TerminateAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            if (process is not null) await TerminateAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return Failure("MuseExecutableNotFound");
        }
        catch (UnauthorizedAccessException)
        {
            if (process is not null) await TerminateAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return Failure("MuseAccessDenied");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            if (process is not null) await TerminateAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return Failure("MuseAccessDenied");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (process is not null) await TerminateAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return Failure("MuseRunFailed");
        }
        finally
        {
            process?.Dispose();
            promptFile?.Dispose();
        }

        AgentRunResult Failure(string messageKey) => new(false, -1, "", CoreText.Get(messageKey))
        { CliSession = new CliSessionOutput(AgentProfile.Muse, null, null, false, true) };
    }

    private static async Task ReadJsonLinesAsync(StreamReader reader, MuseOutputParser parser, Process process, CancellationToken token)
    {
        var line = new StringBuilder(1024);
        var buffer = new char[4096];
        var discarding = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    if (!discarding && !parser.AddLine(line.ToString())) TryKill(process);
                    line.Clear(); discarding = false;
                }
                else if (!discarding)
                {
                    if (line.Length >= MuseOutputParser.MaxLineCharacters) { line.Clear(); discarding = true; parser.RejectInput(); TryKill(process); }
                    else line.Append(ch);
                }
            }
            if (parser.ApprovalPending) TryKill(process);
        }
        if (line.Length > 0 && !discarding && !parser.AddLine(line.ToString())) TryKill(process);
    }

    private static async Task DrainFixedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) return;
            total = Math.Min(MaxStderrCharacters, total + read); // Drain without retaining or surfacing raw text.
        }
    }

    private static async Task DrainAfterKill(Task stdout, Task stderr)
    { try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { } }

    private static async Task TerminateAndDrainAsync(Process process, Task? stdout, Task? stderr)
    {
        TryKill(process);
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        foreach (var task in new[] { stdout, stderr })
            if (task is not null) try { await task.ConfigureAwait(false); } catch { }
    }

    private static void RemoveOption(List<string> args, string option)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (args[i] == option) { args.RemoveAt(i); if (i < args.Count) args.RemoveAt(i); }
            else if (args[i].StartsWith(option + "=", StringComparison.Ordinal)) args.RemoveAt(i);
        }
    }

    private static void TryKill(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }

    private static void SafeReport(IProgress<AgentRunProgress>? progress, AgentRunProgress update)
    { try { progress?.Report(update); } catch { } }

    private sealed class MusePromptFile : IDisposable
    {
        private const string PrivateRootName = "CAB-MusePrompts";
        private readonly FileStream _lease;
        private readonly FileStream _directoryLease;
        private readonly string _directory;
        private MusePromptFile(string directory, string path, FileStream lease, FileStream directoryLease)
        { _directory = directory; Path = path; _lease = lease; _directoryLease = directoryLease; }
        internal string Path { get; }

        internal static void EnsureOutsideWorkspace(string workspace)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Muse prompt files require Windows path isolation.");
            try
            {
                var fullWorkspace = ResolvePhysicalDirectoryPath(workspace) ?? throw new MusePromptIsolationException();
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData)) throw new MusePromptIsolationException();
                var privateRootPath = NormalizePath(System.IO.Path.Combine(localAppData, PrivateRootName));
                if (HasReparsePointInPath(privateRootPath)) throw new MusePromptIsolationException();
                var privateRoot = Directory.Exists(privateRootPath)
                    ? ResolvePhysicalDirectoryPath(privateRootPath)
                    : ResolvePhysicalDirectoryPath(localAppData) is { } physicalLocalAppData
                        ? NormalizePath(System.IO.Path.Combine(physicalLocalAppData, PrivateRootName))
                        : null;
                if (privateRoot is null) throw new MusePromptIsolationException();
                if (IsSameOrDescendant(fullWorkspace, privateRoot) || IsSameOrDescendant(privateRoot, fullWorkspace))
                    throw new MusePromptIsolationException();
            }
            catch (MusePromptIsolationException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            { throw new MusePromptIsolationException(); }
        }

        internal static MusePromptFile Create(string prompt)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Muse prompt files require Windows ACL protection.");
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Local application data is unavailable.");
            var parent = System.IO.Path.Combine(root, PrivateRootName);
            EnsurePrivateDirectory(parent);
            CleanupOrphans(parent);
            var directory = System.IO.Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(directory);
            FileStream? directoryLease = null;
            var path = System.IO.Path.Combine(directory, "prompt.txt");
            FileStream? lease = null;
            try
            {
                directoryLease = new FileStream(System.IO.Path.Combine(directory, ".lease"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                using (var writerStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    ApplyCurrentUserAcl(path, isDirectory: false);
                    using var writer = new StreamWriter(writerStream, new UTF8Encoding(false), 4096, leaveOpen: true);
                    writer.Write(prompt); writer.Flush(); writerStream.Flush(flushToDisk: true);
                }
                lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                return new MusePromptFile(directory, path, lease ?? throw new IOException("Muse prompt lease was not created."),
                    directoryLease ?? throw new IOException("Muse run lease was not created."));
            }
            catch { lease?.Dispose(); directoryLease?.Dispose(); SafeDelete(path); SafeDelete(System.IO.Path.Combine(directory, ".lease")); SafeDelete(directory); throw; }
        }

        public void Dispose()
        {
            _lease.Dispose();
            SafeDelete(Path);
            _directoryLease.Dispose();
            SafeDelete(System.IO.Path.Combine(_directory, ".lease"));
            SafeDelete(_directory);
        }

        [SupportedOSPlatform("windows")]
        private static void EnsurePrivateDirectory(string path)
        {
            var root = System.IO.Path.GetPathRoot(path)!;
            var ancestors = new List<string>();
            var current = System.IO.Path.GetDirectoryName(path)!;
            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                ancestors.Add(current);
                current = System.IO.Path.GetDirectoryName(current)!;
            }
            foreach (var ancestor in ancestors)
                if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Muse private prompt path contains a reparse point.");
            if (!Directory.Exists(path)) new DirectoryInfo(path).Create(CreatePrivateSecurity());
            else
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !IsOwnedByCurrentUser(path))
                    throw new IOException("Muse private prompt directory is not owned by the current user.");
                ApplyCurrentUserAcl(path, isDirectory: true);
            }
        }

        [SupportedOSPlatform("windows")]
        private static void ApplyCurrentUserAcl(string path, bool isDirectory)
        {
            var security = isDirectory ? (FileSystemSecurity)CreatePrivateSecurity() : new FileSecurity();
            if (!isDirectory)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User ?? throw new UnauthorizedAccessException("Current Windows user SID is unavailable.");
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.SetOwner(sid);
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            }
            if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else new FileInfo(path).SetAccessControl((FileSecurity)security);
        }

        [SupportedOSPlatform("windows")]
        private static DirectorySecurity CreatePrivateSecurity()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new UnauthorizedAccessException("Current Windows user SID is unavailable.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        [SupportedOSPlatform("windows")]
        internal static void CleanupAbandoned()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root)) return;
                var parent = System.IO.Path.Combine(root, PrivateRootName);
                if (!Directory.Exists(parent) || HasReparsePointInPath(parent) || !IsOwnedByCurrentUser(parent)) return;
                CleanupOrphans(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        [SupportedOSPlatform("windows")]
        private static void CleanupOrphans(string parent)
        {
            var candidates = Directory.EnumerateDirectories(parent, "run-*", SearchOption.TopDirectoryOnly)
                .Take(128)
                .Where(path => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path), "^run-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));
            foreach (var directory in candidates)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !IsOwnedByCurrentUser(directory)) continue;
                    var leasePath = System.IO.Path.Combine(directory, ".lease");
                    if (!File.Exists(leasePath) || (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) continue;
                    using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    var file = System.IO.Path.Combine(directory, "prompt.txt");
                    if (File.Exists(file))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        SafeDelete(file);
                    }
                    SafeDelete(leasePath);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool IsOwnedByCurrentUser(string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User;
            if (sid is null) return false;
            var owner = new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            return sid.Equals(owner);
        }

        private static string NormalizePath(string path)
        {
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                path = "\\\\" + path[8..];
            else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            {
                if (path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
                    throw new IOException("Muse prompt path could not be normalized safely.");
                path = path[4..];
            }
            var fullPath = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(fullPath)!;
            return fullPath.Length == root.Length ? fullPath : System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        }

        private static string? ResolvePhysicalDirectoryPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var full = System.IO.Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;
            using var handle = CreateDirectoryHandle(full);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }
            return NormalizePath(buffer.ToString());
        }

        [SupportedOSPlatform("windows")]
        private static SafeFileHandle CreateDirectoryHandle(string path)
            => CreateFile(path, 0, FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero,
                FileMode.Open, BackupSemantics, IntPtr.Zero);

        private const uint BackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

        private static bool IsSameOrDescendant(string candidate, string ancestor)
        {
            if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = ancestor.EndsWith(System.IO.Path.DirectorySeparatorChar) ? ancestor : ancestor + System.IO.Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        [SupportedOSPlatform("windows")]
        private static bool HasReparsePointInPath(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                var root = System.IO.Path.GetPathRoot(current);
                if (string.IsNullOrEmpty(root) || string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
                current = System.IO.Path.GetDirectoryName(current)!;
            }
            return false;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
                else if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, recursive: false);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class MusePromptIsolationException : Exception { }
}
