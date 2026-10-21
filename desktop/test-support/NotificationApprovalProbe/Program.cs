using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Invoke the published Inbox entry points without Windows notifications, WinUI, a CLI, or chat.
if (args.Length != 2) throw new ArgumentException("Usage: NotificationApprovalProbe <published-app-directory> <new-test-data-directory>");
var publish = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new ArgumentException("Use a new directory to preserve previous test evidence.");
Directory.CreateDirectory(root);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(publish, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var desktop = Assembly.LoadFrom(Path.Combine(publish, "ChatAgentBridge.Desktop.dll"));
var core = Assembly.LoadFrom(Path.Combine(publish, "ChatAgentBridge.Core.dll"));
var transports = Assembly.LoadFrom(Path.Combine(publish, "ChatAgentBridge.Transports.dll"));
var vmType = desktop.GetType("ChatAgentBridge_Desktop.Inbox.ChatInboxViewModel", true)!;
var itemType = desktop.GetType("ChatAgentBridge_Desktop.Inbox.InboxItem", true)!;
var settingsType = core.GetType("ChatAgentBridge.Core.BridgeSettings", true)!;
var storeType = core.GetType("ChatAgentBridge.Core.SettingsStore", true)!;
var requestType = transports.GetType("ChatAgentBridge.Transports.ChatRequest", true)!;
var transportType = transports.GetType("ChatAgentBridge.Transports.IChatTransport", true)!;
var workspaceCommandType = core.GetType("ChatAgentBridge.Core.WorkspaceChatCommand", true)!;
const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var results = new List<object>();
var failures = 0;
object Model(Type type, object value) => JsonSerializer.Deserialize(JsonSerializer.Serialize(value), type)!;
object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
void Set(object value, string name, object? propertyValue) => value.GetType().GetProperty(name)!.SetValue(value, propertyValue);
void Field(object value, string name, object? fieldValue) => value.GetType().GetField(name, Methods)!.SetValue(value, fieldValue);
object? Call(object value, string name, params object?[] values) => value.GetType().GetMethod(name, Methods)!.Invoke(value, values);
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Confirm(object vm, Func<Task<bool>> callback)
{
    var property = vmType.GetProperty("ConfirmAsync")!;
    var parameters = property.PropertyType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
    property.SetValue(vm, Expression.Lambda(property.PropertyType,
        Expression.Invoke(Expression.Constant(callback)), parameters).Compile());
}
Dictionary<string, byte[]> Disk(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
void Unchanged(Fixture fixture, Dictionary<string, byte[]> before)
{
    var current = Disk(fixture.Data);
    Assert(current.Count == before.Count && before.All(pair =>
        current.TryGetValue(pair.Key, out var bytes) && bytes.SequenceEqual(pair.Value)), "Unavailable action changed persisted data");
    Assert(fixture.Transport.Replies == 0 && fixture.Transport.Runs == 0, "Unavailable action attempted transport work");
}
async Task<Fixture> Prepare(string name)
{
    var data = Path.Combine(root, name);
    var workspace = Path.Combine(data, "original-workspace");
    var proposed = Path.Combine(data, "proposed-workspace");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(proposed);
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", data);
    var settings = Model(settingsType, new
    {
        WorkspaceRoot = workspace, TrustedWorkspaceRoots = data,
        AgentProvider = "custom", AgentExecutable = Path.Combine(data, "never-start-a-real-agent.exe"), AgentArguments = "",
        AgentMode = "exec", ChatApprovalMode = "always", SlackAllowedUserIds = "U_TEST"
    });
    var store = Activator.CreateInstance(storeType, data)!;
    await (Task)Call(store, "SaveAsync", settings, CancellationToken.None)!;
    var loading = (Task)Call(store, "LoadAsync", CancellationToken.None)!;
    await loading;
    settings = Get(loading, "Result")!;
    var fake = DispatchProxy.Create(transportType, typeof(RecordingTransportProxy));
    object Item(string thread, string target) => Activator.CreateInstance(itemType,
        Model(requestType, new
        {
            Provider = "slack", ChannelId = "C_TEST", ThreadId = thread, UserId = "U_TEST",
            Text = "/folder " + target, MessageId = "M_" + thread
        }), fake, settings)!;
    var item = Item("123.456", proposed);
    var other = Item("234.567", workspace);
    foreach (var pair in new[] { (Item: item, Path: proposed), (Item: other, Path: workspace) })
    {
        object?[] parsedArguments = [(string)Get(pair.Item, "Prompt")!, null];
        Assert((bool)workspaceCommandType.GetMethod("TryParse")!.Invoke(null, parsedArguments)! &&
            (string?)parsedArguments[1] == pair.Path, "Synthetic request is not an exact workspace command");
    }
    Assert(!File.Exists((string)Get(settings, "AgentExecutable")!), "Synthetic agent executable unexpectedly exists");
    var vm = Activator.CreateInstance(vmType)!;
    // Keep the probe deterministic and prevent unrelated policy scheduling after an action completes.
    Field(vm, "_processingPolicies", true);
    Field(vm, "_transport", fake);
    var connection = new CancellationTokenSource();
    Field(vm, "_connection", connection);
    Set(vm, "IsConnectionConfirmed", true);
    var requests = (IList)Get(vm, "Requests")!;
    requests.Add(item);
    requests.Add(other);
    return new(vm, item, other, store, vmType.GetField("_sessions", Methods)!.GetValue(vm)!,
        vmType.GetMethod("CreateSessionKey", Methods)!.Invoke(null, [item])!, data, proposed,
        (RecordingTransportProxy)fake, connection);
}
async Task<string?> Invoke(Fixture fixture, string action)
{
    var task = (Task)Call(fixture.Vm, action == "approve" ? "ApproveNotificationAsync" : "RejectNotificationAsync", fixture.Item)!;
    await task.WaitAsync(TimeSpan.FromSeconds(10));
    return task.GetType().GetProperty("Result")?.GetValue(task)?.ToString();
}
async Task<string?> SavedWorkspace(Fixture fixture)
{
    var loading = (Task)Call(fixture.Sessions, "FindWorkspaceAsync", fixture.Key, CancellationToken.None)!;
    await loading;
    return (string?)Get(loading, "Result");
}
async Task Run(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error)
    {
        failures++;
        results.Add(new { name, passed = false, error = error.GetBaseException().Message });
        Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message);
    }
}

foreach (var culture in new[] { "en", "ja" })
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    await Run(culture + "-approve-filtered-target", async () =>
    {
        var fixture = await Prepare(culture + "-approve-filtered-target");
        Confirm(fixture.Vm, () => throw new InvalidOperationException("Notification approval opened another confirmation dialog"));
        Set(fixture.Vm, "Selected", fixture.Other);
        Set(fixture.Vm, "SearchText", "no matches");
        Set(fixture.Vm, "StateFilter", "completed");
        Assert(await Invoke(fixture, "approve") == "Completed", "Successful approval did not report completion");
        Assert((string)Get(fixture.Item, "State")! == "フォルダー変更済み", "Notification did not approve its target");
        Assert(!File.Exists(Path.Combine(fixture.Data, "history.json")), "Folder command entered agent execution");
        Assert(await SavedWorkspace(fixture) == fixture.Proposed, "Approved workspace was not persisted");
        Assert((string)Get(fixture.Other, "State")! == "承認待ち", "Notification approved another request");
        Assert((string)Get(fixture.Vm, "SearchText")! == "" && (string)Get(fixture.Vm, "StateFilter")! == "all", "Target was not revealed through filters");
        Assert(fixture.Transport.Replies == 1 && fixture.Transport.Runs == 0, "Approval did not use only the recording reply stub");
        var after = Disk(fixture.Data);
        Assert(await Invoke(fixture, "approve") == "Unavailable", "Duplicate approval remained available");
        var current = Disk(fixture.Data);
        Assert(current.Count == after.Count && after.All(pair => current.TryGetValue(pair.Key, out var bytes) && bytes.SequenceEqual(pair.Value)), "Duplicate approval changed persisted data");
        Assert(fixture.Transport.Replies == 1, "Duplicate approval sent another reply");
    });
    await Run(culture + "-reject-target", async () =>
    {
        var fixture = await Prepare(culture + "-reject-target");
        Set(fixture.Vm, "Selected", fixture.Other);
        Set(fixture.Vm, "SearchText", "no matches");
        Assert(await Invoke(fixture, "decline") == "Completed", "Successful rejection did not report completion");
        Assert((string)Get(fixture.Item, "State")! == "拒否", "Notification did not reject its target");
        Assert((string)Get(fixture.Other, "State")! == "承認待ち", "Notification rejected another request");
        Assert(await SavedWorkspace(fixture) is null, "Rejection changed workspace");
        Assert(fixture.Transport.Replies == 1 && fixture.Transport.Runs == 0, "Rejection did not use only the recording reply stub");
    });
    foreach (var action in new[] { "approve", "decline" })
    {
        await Run($"{culture}-{action}-command-still-finishing", async () =>
        {
            var fixture = await Prepare($"{culture}-{action}-command-still-finishing");
            Task<string?>? reentrant = null;
            ((INotifyPropertyChanged)fixture.Vm).PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "IsExecuting" && !(bool)Get(fixture.Vm, "IsExecuting")!)
                    reentrant = Invoke(fixture with { Item = fixture.Other }, action);
            };
            Assert(await Invoke(fixture, action) == "Completed", "Initial action did not complete");
            Assert(reentrant is not null && await reentrant == "Busy", "Action did not remain busy until its relay command finished");
            Assert((string)Get(fixture.Other, "State")! == "承認待ち", "Finishing command approved or rejected another request");
            Assert(fixture.Transport.Replies == 1 && fixture.Transport.Runs == 0, "Finishing command attempted additional transport work");
        });
        foreach (var unavailable in new[] { "busy", "removed", "disconnected", "cancelled-connection", "missing-connection", "old-transport", "closing", "resolved", "revoked", "stale-and-busy" })
            await Run($"{culture}-{action}-{unavailable}", async () =>
            {
                var fixture = await Prepare($"{culture}-{action}-{unavailable}");
                switch (unavailable)
                {
                    case "busy": Set(fixture.Vm, "IsExecuting", true); break;
                    case "removed": ((IList)Get(fixture.Vm, "Requests")!).Remove(fixture.Item); break;
                    case "disconnected": Set(fixture.Vm, "IsConnectionConfirmed", false); break;
                    case "cancelled-connection": fixture.Connection.Cancel(); break;
                    case "missing-connection": Field(fixture.Vm, "_connection", null); break;
                    case "old-transport": Field(fixture.Vm, "_transport", DispatchProxy.Create(transportType, typeof(RecordingTransportProxy))); break;
                    case "closing": Field(fixture.Vm, "_closing", true); break;
                    case "resolved": Set(fixture.Item, "State", "完了"); break;
                    case "revoked": Set(fixture.Item, "State", "許可取り消し・実行不可"); break;
                    case "stale-and-busy": Set(fixture.Vm, "IsExecuting", true); fixture.Connection.Cancel(); break;
                }
                var before = Disk(fixture.Data);
                var state = Get(fixture.Item, "State");
                var outcome = await Invoke(fixture, action);
                Assert(outcome == (unavailable == "busy" ? "Busy" : "Unavailable"), "Unexpected action outcome: " + outcome);
                Unchanged(fixture, before);
                Assert(Equals(Get(fixture.Item, "State"), state), "Unavailable action changed request state");
                Assert((string)Get(fixture.Other, "State")! == "承認待ち", "Unavailable action changed another request");
            });
        await Run($"{culture}-{action}-selection-changed-during-reveal", async () =>
        {
            var fixture = await Prepare($"{culture}-{action}-selection-changed-during-reveal");
            Confirm(fixture.Vm, () => Task.FromResult(true));
            ((INotifyPropertyChanged)fixture.Vm).PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "IsDetailOpen" && (bool)Get(fixture.Vm, "IsDetailOpen")!)
                    Set(fixture.Vm, "Selected", fixture.Other);
            };
            var before = Disk(fixture.Data);
            Assert(await Invoke(fixture, action) == "Unavailable", "Changed selection remained available");
            Unchanged(fixture, before);
            Assert((string)Get(fixture.Item, "State")! == "承認待ち" && (string)Get(fixture.Other, "State")! == "承認待ち", "Selection change approved or rejected a different request");
        });
        await Run($"{culture}-{action}-access-revoked", async () =>
        {
            var fixture = await Prepare($"{culture}-{action}-access-revoked");
            var loading = (Task)Call(fixture.Store, "LoadAsync", CancellationToken.None)!;
            await loading;
            var revoked = Get(loading, "Result")!;
            Set(revoked, "SlackAllowedUserIds", "U_OTHER");
            await (Task)Call(fixture.Store, "SaveAsync", revoked, CancellationToken.None)!;
            await (Task)Call(fixture.Vm, "RefreshAccessAsync")!;
            var before = Disk(fixture.Data);
            Assert(await Invoke(fixture, action) == "Unavailable", "Revoked request remained available");
            Unchanged(fixture, before);
            Assert((string)Get(fixture.Item, "State")! == "許可取り消し・実行不可", "Revoked request state was changed");
            Assert(await SavedWorkspace(fixture) is null, "Revoked request changed workspace");
        });
    }
}
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; all replies were recorded in memory; no CLI, WinUI, Windows notifications, or chat connection started.");
return failures == 0 ? 0 : 1;

internal sealed record Fixture(object Vm, object Item, object Other, object Store, object Sessions, object Key,
    string Data, string Proposed, RecordingTransportProxy Transport, CancellationTokenSource Connection);

public class RecordingTransportProxy : DispatchProxy
{
    public int Replies { get; private set; }
    public int Runs { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "ReplyAsync") { Replies++; return Task.CompletedTask; }
        if (targetMethod?.Name == "DisposeAsync") return ValueTask.CompletedTask;
        if (targetMethod?.Name == "RunAsync") Runs++;
        throw new InvalidOperationException("Unexpected fake transport call: " + targetMethod?.Name);
    }
}
