using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Exercise the published Inbox ViewModel without starting WinUI, a CLI, or a chat connection.
if (args.Length != 2) throw new ArgumentException("Usage: InboxCancellationProbe <published-app-directory> <new-test-data-directory>");
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
var bindingType = core.GetType("ChatAgentBridge.Core.Sessions.SessionBinding", true)!;
var connectionStoreType = core.GetType("ChatAgentBridge.Core.AgentConnectionStore", true)!;
var profileType = core.GetType("ChatAgentBridge.Core.AgentConnectionProfile", true)!;
var commandType = core.GetType("ChatAgentBridge.Core.AgentChatCommand", true)!;
var requestType = transports.GetType("ChatAgentBridge.Transports.ChatRequest", true)!;
var transportType = transports.GetType("ChatAgentBridge.Transports.IChatTransport", true)!;
const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var results = new List<object>();
var failures = 0;
object Model(Type type, object values) => JsonSerializer.Deserialize(JsonSerializer.Serialize(values), type)!;
object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
void Set(object value, string name, object? propertyValue) => value.GetType().GetProperty(name)!.SetValue(value, propertyValue);
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
Dictionary<string, byte[]> Disk(string data) => Directory.GetFiles(data, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(data, path), File.ReadAllBytes);
void Unchanged(Fixture fixture)
{
    var current = Disk(fixture.Data);
    Assert(current.Count == fixture.Disk.Count && fixture.Disk.All(pair =>
        current.TryGetValue(pair.Key, out var bytes) && bytes.SequenceEqual(pair.Value)), "Canceled operation changed persisted data");
    Assert(fixture.Transport.Replies == 0 && fixture.Transport.Runs == 0, "Canceled operation attempted a transport action");
    Assert((string)Get(fixture.Item, "State")! == "承認待ち", "Cancel changed the request state");
}
async Task<Fixture> Prepare(string name, string operation)
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
        AgentProvider = "codex", AgentExecutable = "codex", AgentArguments = "exec --json -",
        AgentModel = "baseline-model", AgentMode = "exec", SlackAllowedUserIds = "U_TEST"
    });
    var store = Activator.CreateInstance(storeType, data)!;
    await (Task)Call(store, "SaveAsync", settings, CancellationToken.None)!;
    // Reload the same normalized settings that CheckAccessAsync will read after approval.
    var loading = (Task)Call(store, "LoadAsync", CancellationToken.None)!;
    await loading;
    settings = Get(loading, "Result")!;
    var fake = DispatchProxy.Create(transportType, typeof(RecordingTransportProxy));
    var request = Model(requestType, new
    {
        Provider = "slack", ChannelId = "C_TEST", ThreadId = "123.456", UserId = "U_TEST",
        Text = operation == "workspace" ? "!cab workspace " + proposed : "!cab agent claude fresh", MessageId = "M_TEST"
    });
    var item = Activator.CreateInstance(itemType, request, fake, settings)!;
    Set(item, "SessionLabel", "Original session details");
    var vm = Activator.CreateInstance(vmType)!;
    var sessions = vmType.GetField("_sessions", Methods)!.GetValue(vm)!;
    var key = vmType.GetMethod("CreateSessionKey", Methods)!.Invoke(null, [item])!;
    var binding = Model(bindingType, new
    {
        Key = key, WorkspaceRoot = workspace, AgentExecutable = "codex",
        AgentArguments = "exec --json -", SessionId = Guid.NewGuid().ToString(), UpdatedAt = DateTimeOffset.UtcNow,
        Model = "baseline-model"
    });
    await (Task)Call(sessions, "SaveAsync", binding, CancellationToken.None)!;
    var profiles = Activator.CreateInstance(connectionStoreType, data)!;
    var profile = Model(profileType, new
    {
        Provider = "claude", Executable = "claude", Arguments = "", Mode = "exec",
        Model = "target-model", PermissionMode = "inherit", AllowNetwork = false,
        CliVersion = "synthetic-test-version", CheckedAt = DateTimeOffset.UtcNow
    });
    await (Task)Call(profiles, "SaveVerifiedAsync", profile, CancellationToken.None)!;
    return new(vm, item, sessions, key, data, workspace, proposed, (RecordingTransportProxy)fake, Disk(data));
}
Task Invoke(Fixture fixture, string operation)
{
    if (operation == "workspace")
        return (Task)Call(fixture.Vm, "ChangeWorkspaceAsync", fixture.Item, fixture.Proposed, false, CancellationToken.None)!;
    var command = Model(commandType, new { Provider = "claude", IncludeSummary = false, IsValid = true });
    return (Task)Call(fixture.Vm, "ChangeAgentAsync", fixture.Item, command, false, CancellationToken.None)!;
}
void OriginalDisplay(Fixture fixture)
{
    Assert((string)Get(fixture.Item, "Workspace")! == fixture.Workspace, "Rejected workspace preview remains visible");
    Assert((string)Get(fixture.Item, "SessionLabel")! == "Original session details", "Rejected session/agent preview remains visible");
}
async Task Run(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.GetBaseException().Message }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
}

foreach (var culture in new[] { "en", "ja" })
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    foreach (var operation in new[] { "workspace", "agent" })
    {
        foreach (var decision in new[] { "reject", "missing", "cancel-token" })
            await Run($"{culture}-{operation}-{decision}", async () =>
            {
                var fixture = await Prepare($"{culture}-{operation}-{decision}", operation);
                var confirmations = 0;
                if (decision != "missing") Confirm(fixture.Vm, () =>
                {
                    confirmations++;
                    Assert((string)Get(fixture.Item, "SessionLabel")! != "Original session details", "Confirmation did not receive its preview");
                    return decision == "cancel-token"
                        ? Task.FromException<bool>(new OperationCanceledException("Synthetic dialog cancellation"))
                        : Task.FromResult(false);
                });
                try
                {
                    await Invoke(fixture, operation);
                    Assert(decision != "cancel-token", "Dialog cancellation was swallowed");
                }
                catch (OperationCanceledException) when (decision == "cancel-token") { }
                Assert(confirmations == (decision == "missing" ? 0 : 1), "Unexpected confirmation count");
                OriginalDisplay(fixture);
                Unchanged(fixture);
            });
        foreach (var newerField in new[] { "Workspace", "SessionLabel" })
            await Run($"{culture}-{operation}-preserve-newer-{newerField}", async () =>
            {
                var fixture = await Prepare($"{culture}-{operation}-newer-{newerField}", operation);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Confirm(fixture.Vm, () => { entered.SetResult(); return answer.Task; });
                var pending = Invoke(fixture, operation);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var newer = newerField == "Workspace" ? Path.Combine(fixture.Data, "newer-display") : "Newer session details";
                Set(fixture.Item, newerField, newer);
                answer.SetResult(false);
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert((string)Get(fixture.Item, newerField)! == newer, "Cancel overwrote a later display update");
                var other = newerField == "Workspace" ? "SessionLabel" : "Workspace";
                var expected = other == "Workspace" ? fixture.Workspace : "Original session details";
                Assert((string)Get(fixture.Item, other)! == expected, "Cancel did not restore the untouched preview field");
                Unchanged(fixture);
            });
        await Run($"{culture}-{operation}-accept", async () =>
        {
            var fixture = await Prepare($"{culture}-{operation}-accept", operation);
            Confirm(fixture.Vm, () => Task.FromResult(true));
            await Invoke(fixture, operation);
            Assert(fixture.Transport.Runs == 0 && fixture.Transport.Replies == 1, "Accepted operation did not use only the recording reply stub");
            Assert((string)Get(fixture.Item, "State")! == (operation == "workspace" ? "フォルダー変更済み" : "エージェント変更済み"), "Accepted operation did not complete");
            Assert((string)Get(fixture.Item, "SessionLabel")! != "Original session details", "Successful preview was incorrectly rolled back");
            var persisted = (Task)Call(fixture.Sessions, operation == "workspace" ? "FindWorkspaceAsync" : "FindAgentSelectionAsync", fixture.Key, CancellationToken.None)!;
            await persisted;
            var value = Get(persisted, "Result");
            if (operation == "workspace")
            {
                Assert((string?)value == fixture.Proposed, "Accepted workspace was not persisted");
                Assert((string)Get(fixture.Item, "Workspace")! == fixture.Proposed, "Accepted workspace display was rolled back");
            }
            else Assert(value is not null && (string)Get(value, "Provider")! == "claude", "Accepted agent was not persisted");
        });
    }
}
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; all transport replies were recorded in memory; no CLI, WinUI window, or chat connection started.");
return failures == 0 ? 0 : 1;

internal sealed record Fixture(object Vm, object Item, object Sessions, object Key, string Data, string Workspace,
    string Proposed, RecordingTransportProxy Transport, Dictionary<string, byte[]> Disk);

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
