using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Exercise the shipped ViewModel without WinUI, agent processes, or chat connections.
if (args.Length != 2) throw new ArgumentException("Usage: ManualWorkspaceProbe <published-app-directory> <new-test-root>");
var publish = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Use a new test root to preserve previous evidence.");
Directory.CreateDirectory(root);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(publish, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var desktop = Assembly.LoadFrom(Path.Combine(publish, "ChatAgentBridge.Desktop.dll"));
var core = Assembly.LoadFrom(Path.Combine(publish, "ChatAgentBridge.Core.dll"));
var vmType = desktop.GetType("ChatAgentBridge_Desktop.ViewModels.MainPageViewModel", true)!;
var settingsType = core.GetType("ChatAgentBridge.Core.BridgeSettings", true)!;
var storeType = core.GetType("ChatAgentBridge.Core.SettingsStore", true)!;
var recoveryType = desktop.GetType("ChatAgentBridge_Desktop.ViewModels.CliTrustRecovery", true)!;
var coreText = core.GetType("ChatAgentBridge.Core.Localization.CoreText", true)!;
const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var results = new List<object>();
var observations = new List<object>();
var failures = 0;
var originalData = Environment.GetEnvironmentVariable("CAB_WINUI_DATA_DIR");
var originalCulture = CultureInfo.CurrentUICulture;
object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
void Set(object value, string name, object? propertyValue) => value.GetType().GetProperty(name)!.SetValue(value, propertyValue);
object? Call(object value, string name, params object?[] values) => value.GetType().GetMethod(name, Methods)!.Invoke(value, values);
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string Text(string key, params object?[] values) => (string)coreText.GetMethod("Get")!.Invoke(null, [key, values])!;
string Snapshot(object vm) => JsonSerializer.Serialize(Call(vm, "CreateSettingsSnapshot"), settingsType);
Dictionary<string, byte[]> Disk(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
void Unchanged(Fixture fixture, Dictionary<string, byte[]> before, string draft)
{
    var after = Disk(fixture.Data);
    Assert(after.Count == before.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value.SequenceEqual(pair.Value)),
        "Execution preflight/cancellation changed persisted data");
    Assert(Snapshot(fixture.Vm) == draft, "Execution preflight/cancellation changed the settings draft");
    Assert(!(bool)Get(fixture.Vm, "IsRunning")!, "Execution remained running");
    Assert(!File.Exists(Path.Combine(fixture.Data, "history.json")), "Execution created history");
    Assert(!File.Exists(Path.Combine(fixture.Data, "audit.json")), "Execution created audit");
    Assert(!File.Exists(fixture.Executable), "Synthetic nonexistent agent unexpectedly exists");
}
void Confirm(object vm, Func<object, string, Task<bool>> callback)
{
    var property = vmType.GetProperty("ConfirmExecutionAsync")!;
    var parameters = property.PropertyType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
    property.SetValue(vm, Expression.Lambda(property.PropertyType,
        Expression.Invoke(Expression.Constant(callback), Expression.Convert(parameters[0], typeof(object)), parameters[1]), parameters).Compile());
}
void ConfirmSettings(object vm)
{
    var property = vmType.GetProperty("ConfirmSensitiveSettingsAsync")!;
    var parameters = property.PropertyType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
    property.SetValue(vm, Expression.Lambda(property.PropertyType, Expression.Constant(Task.FromResult(true)), parameters).Compile());
}
async Task InvokeRun(Fixture fixture)
{
    Assert((bool)Call(fixture.Vm, "CanRun")!, "Fixture cannot enter the actual manual execution path");
    await ((Task)Call(fixture.Vm, "RunAsync")!).WaitAsync(TimeSpan.FromSeconds(10));
}
async Task<Fixture> Prepare(string name, string mode)
{
    var area = Path.Combine(root, name);
    var data = Path.Combine(area, "data");
    var trusted = Path.Combine(area, "trusted");
    var child = Path.Combine(trusted, "child");
    var outside = Path.Combine(area, "outside");
    Directory.CreateDirectory(data);
    Directory.CreateDirectory(child);
    Directory.CreateDirectory(outside);
    var workspace = mode switch
    {
        "outside" => outside,
        "missing" => Path.Combine(trusted, "missing"),
        "relative" => "relative-fixture-workspace",
        "root" => trusted,
        _ => child
    };
    var executable = Path.Combine(area, "never-start-an-agent.exe");
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", data);
    var settings = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
    {
        WorkspaceRoot = workspace, TrustedWorkspaceRoots = mode == "empty-trust" ? "" : trusted,
        AgentProvider = "custom", AgentExecutable = executable, AgentArguments = "", AgentMode = "exec",
        RequireApproval = true, AutoConnectProvider = "none"
    }), settingsType)!;
    var store = Activator.CreateInstance(storeType, data)!;
    await (Task)Call(store, "SaveAsync", settings, CancellationToken.None)!;
    var vm = Activator.CreateInstance(vmType)!;
    await (Task)Call(vm, "LoadAsync")!;
    Set(vm, "IsOnboardingActive", false);
    Set(vm, "Prompt", "Synthetic preflight probe; never execute an agent.");
    Assert((bool)Get(vm, "IsSettingsLoaded")!, "Fixture settings did not load: " + Get(vm, "StatusMessage"));
    Assert(!(bool)Get(vm, "HasUnsavedSettings")!, "Loaded fixture is unexpectedly dirty");
    Assert(Path.GetFullPath((string)Get(vm, "SettingsLocation")!) == data, "Fixture did not use the isolated data directory");
    Assert(!File.Exists(executable), "Synthetic agent executable unexpectedly exists");
    return new(vm, data, area, workspace, trusted, outside, executable);
}
void Warning(Fixture fixture, string key, bool recovery)
{
    var expected = Text(key, fixture.Workspace);
    Assert((bool)Get(fixture.Vm, "HasManualWorkspaceError")!, "Expected manual workspace warning is absent");
    Assert((string)Get(fixture.Vm, "ManualWorkspaceError")! == expected, "Manual warning did not use the expected localized workspace diagnosis");
    Assert((bool)Get(fixture.Vm, "HasManualTrustRecovery")! == recovery, "Unexpected manual recovery visibility");
    observations.Add(new { culture = CultureInfo.CurrentUICulture.Name, fixture = Path.GetFileName(fixture.Area), error = expected });
}
async Task Run(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.GetBaseException().ToString() }); }
}
void CreateDirectoryJunction(string link, string target)
{
    // Both inputs are fixed fixture paths. Keep every mutation within the explicit new test root.
    var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
    Assert(Path.GetFullPath(link).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Junction paths escaped the test root");
    if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
    Assert(link.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!']) < 0 &&
        target.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!']) < 0, "Junction helper requires paths without cmd metacharacters");
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        ArgumentList = { "/d", "/c", "mklink", "/J", link, target }
    }) ?? throw new InvalidOperationException("Could not start filesystem junction helper");
    Assert(process.WaitForExit(10000), "Junction helper timed out");
    Assert(process.ExitCode == 0, "Junction helper failed: " + process.StandardError.ReadToEnd());
    Assert((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0, "Fixture junction was not created");
}

try
{
    foreach (var culture in new[] { "en", "ja" })
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        foreach (var invalid in new[] { (Mode: "outside", Key: "WorkspaceNotTrusted"), (Mode: "missing", Key: "WorkspaceFolderNotFound"), (Mode: "relative", Key: "WorkspacePathMustBeAbsolute") })
            await Run($"{culture}-saved-{invalid.Mode}-preflight", async () =>
            {
                var fixture = await Prepare($"{culture}-saved-{invalid.Mode}", invalid.Mode);
                Warning(fixture, invalid.Key, invalid.Mode == "outside");
                var confirmations = 0;
                var runningTransitions = 0;
                ((INotifyPropertyChanged)fixture.Vm).PropertyChanged += (_, args) =>
                { if (args.PropertyName == "IsRunning" && (bool)Get(fixture.Vm, "IsRunning")!) runningTransitions++; };
                Confirm(fixture.Vm, (_, _) => { confirmations++; return Task.FromResult(false); });
                var disk = Disk(fixture.Data);
                var draft = Snapshot(fixture.Vm);
                await InvokeRun(fixture);
                Assert(confirmations == 0 && runningTransitions == 0, "Invalid workspace reached confirmation or running state");
                Warning(fixture, invalid.Key, invalid.Mode == "outside");
                Unchanged(fixture, disk, draft);
            });

        foreach (var valid in new[] { "root", "child", "empty-trust" })
            await Run($"{culture}-valid-{valid}-confirmation-cancel", async () =>
            {
                var fixture = await Prepare($"{culture}-valid-{valid}", valid);
                Assert(!(bool)Get(fixture.Vm, "HasManualWorkspaceError")!, "Valid workspace has a load warning");
                var confirmations = 0;
                Confirm(fixture.Vm, (settings, prompt) =>
                {
                    confirmations++;
                    Assert((string)Get(settings, "WorkspaceRoot")! == fixture.Workspace, "Confirmation did not receive the saved workspace");
                    Assert(prompt == (string)Get(fixture.Vm, "Prompt")!, "Confirmation prompt changed");
                    Assert(!File.Exists(Path.Combine(fixture.Data, "history.json")) && !File.Exists(Path.Combine(fixture.Data, "audit.json")), "History/audit preceded confirmation");
                    return Task.FromResult(false);
                });
                var disk = Disk(fixture.Data);
                var draft = Snapshot(fixture.Vm);
                await InvokeRun(fixture);
                Assert(confirmations == 1, "Valid workspace did not reach the synthetic confirmation");
                Unchanged(fixture, disk, draft);
            });

        await Run($"{culture}-typed-recovery-path-and-instance", async () =>
        {
            var fixture = await Prepare($"{culture}-recovery-instance", "outside");
            var recovery = Call(fixture.Vm, "GetCliTrustRecovery");
            Assert(recovery is not null && recovery.GetType() == recoveryType, "Recovery did not return the typed object");
            Assert((string)Get(recovery!, "WorkspacePath")! == fixture.Workspace && (string)Get(recovery!, "TargetPath")! == fixture.Workspace &&
                !(bool)Get(recovery!, "UsesLinkTarget")!, "Recovery proposed the wrong existing directory");
            var clone = Activator.CreateInstance(recoveryType, fixture.Workspace, fixture.Workspace)!;
            var disk = Disk(fixture.Data);
            var draft = Snapshot(fixture.Vm);
            Assert(!(bool)Call(fixture.Vm, "TryApplyCliTrustRecovery", clone)!, "Equal replacement recovery object was accepted");
            Unchanged(fixture, disk, draft);
            Assert((bool)Call(fixture.Vm, "TryApplyCliTrustRecovery", recovery)!, "Current exact recovery instance was rejected");
            Assert(((string)Get(fixture.Vm, "TrustedWorkspaceRoots")!).Split(Environment.NewLine).Contains(fixture.Workspace), "Accepted recovery did not add its exact path");
            Assert(Call(fixture.Vm, "GetCliTrustRecovery") is null, "Applied recovery remained available");
            Assert(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json")).SequenceEqual(disk["settings.json"]), "Recovery silently saved settings");
        });

        await Run($"{culture}-stale-recovery-settings-change", async () =>
        {
            var fixture = await Prepare($"{culture}-recovery-stale", "outside");
            var recovery = Call(fixture.Vm, "GetCliTrustRecovery");
            Assert(recovery is not null, "Load did not produce recovery");
            Set(fixture.Vm, "WorkspaceRoot", fixture.Trusted);
            var disk = Disk(fixture.Data);
            var draft = Snapshot(fixture.Vm);
            Assert(Call(fixture.Vm, "GetCliTrustRecovery") is null, "Settings change did not invalidate recovery");
            Assert(!(bool)Get(fixture.Vm, "HasManualTrustRecovery")!, "Dirty settings retained the manual recovery action");
            Assert(!(bool)Call(fixture.Vm, "TryApplyCliTrustRecovery", recovery)!, "Stale recovery was accepted");
            Unchanged(fixture, disk, draft);
        });

        await Run($"{culture}-persisted-summary-and-save-warning-refresh", async () =>
        {
            var fixture = await Prepare($"{culture}-summary-save", "outside");
            Warning(fixture, "WorkspaceNotTrusted", true);
            Set(fixture.Vm, "WorkspaceRoot", fixture.Trusted);
            Call(fixture.Vm, "UpdateSavedAgentSummary");
            Warning(fixture, "WorkspaceNotTrusted", false);
            ConfirmSettings(fixture.Vm);
            Assert(await (Task<bool>)Call(fixture.Vm, "SavePendingSettingsAsync", false)!, "Valid replacement workspace did not save");
            Assert(!(bool)Get(fixture.Vm, "HasManualWorkspaceError")!, "Saving a valid workspace retained the old warning");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Data, "settings.json")));
            Assert(saved.RootElement.GetProperty("WorkspaceRoot").GetString() == fixture.Trusted, "Replacement workspace was not persisted");
            Assert(!File.Exists(Path.Combine(fixture.Data, "history.json")) && !File.Exists(Path.Combine(fixture.Data, "audit.json")), "Saving settings entered execution");
        });

        await Run($"{culture}-workspace-escapes-during-confirmation", async () =>
        {
            var fixture = await Prepare($"{culture}-pending-escape", "child");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmations = 0;
            Confirm(fixture.Vm, (_, _) => { confirmations++; entered.SetResult(); return answer.Task; });
            var disk = Disk(fixture.Data);
            var draft = Snapshot(fixture.Vm);
            var pending = InvokeRun(fixture);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Move(fixture.Workspace, Path.Combine(fixture.Area, "moved-original-workspace"));
            CreateDirectoryJunction(fixture.Workspace, fixture.Outside);
            answer.SetResult(true);
            await pending;
            Assert(confirmations == 1, "Pending confirmation count changed");
            Assert((bool)Get(fixture.Vm, "HasManualWorkspaceError")! && (bool)Get(fixture.Vm, "HasManualTrustRecovery")!, "Escaping replacement did not produce the recovery warning");
            var recovery = Call(fixture.Vm, "GetCliTrustRecovery");
            Assert(recovery is not null && (string)Get(recovery, "TargetPath")! == fixture.Outside && (bool)Get(recovery, "UsesLinkTarget")!, "Recovery did not identify the escaping target");
            observations.Add(new { culture, fixture = Path.GetFileName(fixture.Area), error = (string)Get(fixture.Vm, "ManualWorkspaceError")! });
            Unchanged(fixture, disk, draft);
        });
    }
}
finally
{
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", originalData);
    CultureInfo.CurrentUICulture = originalCulture;
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new
    {
        publishedDirectory = publish,
        assemblies = new[] { desktop, core }.Select(assembly => new
        { path = assembly.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }),
        passed = results.Count - failures, total = results.Count, failures, results, observations
    }, new JsonSerializerOptions { WriteIndented = true }));
}
return failures == 0 ? 0 : 1;

internal sealed record Fixture(object Vm, string Data, string Area, string Workspace, string Trusted, string Outside, string Executable);
