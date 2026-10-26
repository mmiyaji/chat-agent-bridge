using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Exercise the published Inbox ViewModel without starting a WinUI window, CLI, or network transport.
if (args.Length != 2) throw new ArgumentException("Usage: InboxReconnectProbe <published-app-directory> <new-test-data-directory>");
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
const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var results = new List<object>();
var failures = 0;

object Model(Type type, object values) => JsonSerializer.Deserialize(JsonSerializer.Serialize(values), type)!;
object? Get(object value, string name) => value.GetType().GetProperty(name, Methods)!.GetValue(value);
void Set(object value, string name, object? propertyValue) => value.GetType().GetProperty(name, Methods)!.SetValue(value, propertyValue);
void SetField(object value, string name, object? fieldValue) => value.GetType().GetField(name, Methods)!.SetValue(value, fieldValue);
object? Call(object value, string name, params object?[] values) => value.GetType().GetMethod(name, Methods)!.Invoke(value, values);
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
Task ExecuteCommand(object vm, string name) => (Task)Get(vm, name)!.GetType().GetMethod("ExecuteAsync")!.Invoke(Get(vm, name), [null])!;
bool CanExecute(object vm, string name) => (bool)Get(vm, name)!.GetType().GetMethod("CanExecute")!.Invoke(Get(vm, name), [null])!;
void Confirm(object vm, Func<CancellationToken, Task<bool>> callback)
{
    var property = vmType.GetProperty("ConfirmReconnectAsync")!;
    var parameters = property.PropertyType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
    property.SetValue(vm, Expression.Lambda(property.PropertyType,
        Expression.Invoke(Expression.Constant(callback), parameters), parameters).Compile());
}
void FlushConversationUpdates(object vm)
{
    SetField(vm, "_conversationRefreshQueued", true);
    Call(vm, "FlushConversationRefresh");
    // Keep subsequent headless collection changes from touching App.DispatcherQueue.
    SetField(vm, "_conversationRefreshQueued", true);
}
int Count(object value) => (int)value.GetType().GetProperty("Count")!.GetValue(value)!;

async Task<Fixture> Prepare(string name, bool seedPending)
{
    var data = Path.Combine(root, name);
    var workspace = Path.Combine(data, "workspace");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(data);
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", data);
    var offlineExecutable = Path.Combine(data, "offline-probe-agent.exe");
    Assert(!File.Exists(offlineExecutable), "Probe agent executable must remain nonexistent.");
    var initialSettings = Model(settingsType, new
    {
        WorkspaceRoot = workspace,
        TrustedWorkspaceRoots = workspace,
        AgentProvider = "custom",
        AgentExecutable = offlineExecutable,
        AgentArguments = "offline-probe-initial-args",
        AgentModel = (string?)null,
        AgentMode = "exec",
        ChatApprovalMode = "always",
        SlackAllowedUserIds = "U_TEST",
        PostConnectionNotice = false
    });
    var settingsStore = Activator.CreateInstance(storeType, data)!;
    await (Task)Call(settingsStore, "SaveAsync", initialSettings, CancellationToken.None)!;
    var loadedTask = (Task)Call(settingsStore, "LoadAsync", CancellationToken.None)!;
    await loadedTask;
    var loadedSettings = loadedTask.GetType().GetProperty("Result")!.GetValue(loadedTask)!;
    Assert((string)Get(loadedSettings, "ChatApprovalMode")! == "always", "Probe must require approval for every request.");
    var factory = new RecordingTransportFactory(transportType);
    var ctor = vmType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(constructor => constructor.GetParameters().Length == 1);
    var factoryType = ctor.GetParameters()[0].ParameterType;
    var providerParameter = Expression.Parameter(typeof(string), "provider");
    var build = typeof(RecordingTransportFactory).GetMethod(nameof(RecordingTransportFactory.Build))!;
    var factoryDelegate = Expression.Lambda(factoryType,
        Expression.Convert(Expression.Call(Expression.Constant(factory), build, providerParameter), factoryType.GetMethod("Invoke")!.ReturnType),
        providerParameter).Compile();
    var vm = ctor.Invoke([factoryDelegate]);
    // ConnectAsync clears Requests and queues a refresh. In a headless process there is no
    // DispatcherQueue, so set the existing queue flag and flush the refresh synchronously.
    SetField(vm, "_conversationRefreshQueued", true);
    Assert(!CanExecute(vm, "ReconnectCommand"), "Reconnect should start disabled before a provider has connected.");
    await ExecuteCommand(vm, "ConnectSlackCommand").WaitAsync(TimeSpan.FromSeconds(10));
    await factory.Transports[0].RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Set(vm, "IsConnectionConfirmed", true); // The fake implements no connection-event interface.
    Assert(CanExecute(vm, "ReconnectCommand"), "Reconnect should become available after the initial connection.");
    FlushConversationUpdates(vm);

    object? pendingItem = null;
    if (seedPending)
    {
        var request = Model(requestType, new
        {
            Provider = "slack", ChannelId = "C_TEST", ThreadId = "T_TEST", UserId = "U_TEST",
            Text = "pending approval probe", MessageId = "M_TEST"
        });
        pendingItem = Activator.CreateInstance(itemType, request, factory.Transports[0], loadedSettings)!;
        Set(pendingItem, "ResultText", "synthetic result for action-guard probe");
        Set(pendingItem, "AllowsResultSharing", true);
        var requests = Get(vm, "Requests")!;
        SetField(vm, "_conversationRefreshQueued", true);
        requests.GetType().GetMethod("Add")!.Invoke(requests, [pendingItem]);
        FlushConversationUpdates(vm);
        Set(vm, "Selected", pendingItem);
        Assert(Count(Get(vm, "Conversations")!) > 0, "Seeded approval did not produce a conversation row.");
        Assert(CanExecute(vm, "ApproveCommand") && CanExecute(vm, "SendResultCommand"),
            "Connected approval and result actions should start enabled.");
    }
    return new(vm, factory, settingsStore, data, workspace, pendingItem);
}

async Task SaveLatestSettings(Fixture fixture, string arguments)
{
    var latest = Model(settingsType, new
    {
        WorkspaceRoot = fixture.Workspace,
        TrustedWorkspaceRoots = fixture.Workspace,
        AgentProvider = "custom",
        AgentExecutable = Path.Combine(fixture.Data, "offline-probe-agent.exe"),
        AgentArguments = arguments,
        AgentModel = (string?)null,
        AgentMode = "exec",
        ChatApprovalMode = "always",
        SlackAllowedUserIds = "U_TEST",
        PostConnectionNotice = false
    });
    await (Task)Call(fixture.SettingsStore, "SaveAsync", latest, CancellationToken.None)!;
}

async Task Cleanup(Fixture fixture)
{
    fixture.Factory.ReleaseFactory();
    SetField(fixture.Vm, "_conversationRefreshQueued", true);
    Call(fixture.Vm, "Close");
    try { await (Task)Call(fixture.Vm, "WaitForConnectionShutdownAsync")!; }
    catch (Exception) { }
    foreach (var transport in fixture.Factory.Transports)
        try { await transport.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
}

async Task Run(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.GetBaseException().Message }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
}

await Run("connect-reconnect-latest-settings-serialization", async () =>
{
    var fixture = await Prepare("connect-reconnect", seedPending: false);
    try
    {
        await SaveLatestSettings(fixture, "offline-probe-latest-args");
        var reconnect = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        Assert((bool)Get(fixture.Vm, "IsConnectionActionInProgress")!, "Reconnect action state was not raised while reconnecting.");
        var second = await fixture.Factory.WaitForTransportAsync(2);
        try { await second.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new InvalidOperationException($"Replacement RunAsync did not start. status={Get(fixture.Vm, "Status")}, error={Get(fixture.Vm, "HasConnectionError")}, connected={Get(fixture.Vm, "IsConnected")}, factory={fixture.Factory.Transports.Count}"); }
        await reconnect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Providers.SequenceEqual(new[] { "slack", "slack" }), "Reconnect did not create another transport for the previous provider.");
        Assert(second.RunSettingsArguments == "offline-probe-latest-args", "Reconnect did not pass the latest saved settings to RunAsync.");
        var events = fixture.Factory.Events.ToArray();
        Assert(Array.IndexOf(events, "transport-0-dispose-complete") < Array.IndexOf(events, "transport-1-run"),
            "New RunAsync started before the prior transport finished disposing.");
        Assert(second.Replies == 0, "Reconnect unexpectedly posted a chat message.");
        Assert(!(bool)Get(fixture.Vm, "IsExecuting")!, "Reconnect interrupted or started request execution.");
    }
    finally { await Cleanup(fixture); }
});

await Run("rapid-reconnect-and-pending-approval-confirmation", async () =>
{
    var fixture = await Prepare("confirm-reconnect", seedPending: true);
    try
    {
        var originalRequests = Count(Get(fixture.Vm, "Requests")!);
        var originalConversations = Count(Get(fixture.Vm, "Conversations")!);
        var old = fixture.Factory.Transports[0];
        var rejectCalls = 0;
        var enteredConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Confirm(fixture.Vm, _ => { rejectCalls++; enteredConfirmation.TrySetResult(); return answer.Task; });
        var canceledReconnect = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        await enteredConfirmation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert((bool)Get(fixture.Vm, "IsConnectionActionInProgress")!, "Reconnect confirmation did not hold the action gate.");
        Assert(!CanExecute(fixture.Vm, "ApproveCommand") && !CanExecute(fixture.Vm, "SendResultCommand"),
            "Approval/result commands stayed enabled while reconnect confirmation was open.");
        var notificationTask = (Task)Call(fixture.Vm, "ApproveNotificationAsync", fixture.PendingItem)!;
        await notificationTask.WaitAsync(TimeSpan.FromSeconds(10));
        var notificationResult = notificationTask.GetType().GetProperty("Result")!.GetValue(notificationTask)!.ToString();
        Assert(notificationResult == "Busy", "Notification approval was not rejected as busy during reconnect confirmation.");
        Assert((string)Get(fixture.PendingItem!, "State")! == "承認待ち", "Busy notification handling changed the pending request.");
        answer.SetResult(false);
        await canceledReconnect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(rejectCalls == 1 && fixture.Factory.Transports.Count == 1, "Cancel confirmation replaced the current connection.");
        Assert(old.Runs == 1 && !old.RunToken.IsCancellationRequested, "Cancel confirmation interrupted the active transport.");
        Assert((string)Get(fixture.PendingItem!, "State")! == "承認待ち", "Cancel confirmation changed the pending request state.");
        Assert(Count(Get(fixture.Vm, "Requests")!) == originalRequests && Count(Get(fixture.Vm, "Conversations")!) == originalConversations,
            "Cancel confirmation changed the pending request or conversation list.");

        // A completed request can still hold results or attachment actions that
        // reconnect would discard. It must receive the same confirmation guard.
        Set(fixture.PendingItem!, "State", "完了");
        var completedResult = (string)Get(fixture.PendingItem!, "ResultText")!;
        var completedConfirmationCalls = 0;
        Confirm(fixture.Vm, _ => { completedConfirmationCalls++; return Task.FromResult(false); });
        await ExecuteCommand(fixture.Vm, "ReconnectCommand").WaitAsync(TimeSpan.FromSeconds(10));
        Assert(completedConfirmationCalls == 1, "Reconnect did not confirm before discarding a completed request with an unsent result.");
        Assert(fixture.Factory.Transports.Count == 1 && !old.RunToken.IsCancellationRequested,
            "Canceling completed-result confirmation replaced or interrupted the active connection.");
        Assert((string)Get(fixture.PendingItem!, "State")! == "完了" &&
            (string)Get(fixture.PendingItem!, "ResultText")! == completedResult &&
            Count(Get(fixture.Vm, "Requests")!) == originalRequests && Count(Get(fixture.Vm, "Conversations")!) == originalConversations,
            "Canceling completed-result confirmation changed its state, result, request, or conversation.");
        Set(fixture.PendingItem!, "State", "承認待ち");

        Set(fixture.Vm, "ConfirmReconnectAsync", null);
        await ExecuteCommand(fixture.Vm, "ReconnectCommand").WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Transports.Count == 1 && old.Runs == 1, "Reconnect without a confirmation handler did not fail closed.");

        Confirm(fixture.Vm, _ => Task.FromResult(true));
        var first = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        // A repeated command while confirmation/teardown is in progress must not create a second connection.
        var second = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Transports.Count == 2,
            $"Accepted reconnect did not create a replacement; status={Get(fixture.Vm, "Status")}, error={Get(fixture.Vm, "HasConnectionError")}, connected={Get(fixture.Vm, "IsConnected")}, pending={Get(fixture.Vm, "PendingApprovalCount")}, inProgress={Get(fixture.Vm, "IsConnectionActionInProgress")}");
        var replacement = await fixture.Factory.WaitForTransportAsync(2);
        await replacement.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Transports.Count == 2, "Rapid reconnect clicks created more than one replacement transport.");
        Assert(old.Runs == 1 && replacement.Runs == 1 && old.Replies == 0 && replacement.Replies == 0,
            "Reconnect re-executed the pending request or sent a chat reply.");
        FlushConversationUpdates(fixture.Vm);
        Assert(Count(Get(fixture.Vm, "Conversations")!) == originalConversations, "Reconnect discarded the conversation list.");
        Assert(Count(Get(fixture.Vm, "Requests")!) == 0, "Reconnect left the old approval request actionable.");
    }
    finally { await Cleanup(fixture); }
});

await Run("execution-disables-reconnect-without-canceling-connection", async () =>
{
    var fixture = await Prepare("executing", seedPending: false);
    try
    {
        Set(fixture.Vm, "IsExecuting", true);
        Assert(!CanExecute(fixture.Vm, "ReconnectCommand"), "Reconnect stayed enabled during execution.");
        await ExecuteCommand(fixture.Vm, "ReconnectCommand").WaitAsync(TimeSpan.FromSeconds(5));
        Assert(fixture.Factory.Transports.Count == 1 && fixture.Factory.Transports[0].Runs == 1 &&
            !fixture.Factory.Transports[0].RunToken.IsCancellationRequested, "Disabled reconnect interrupted the current connection.");
    }
    finally { await Cleanup(fixture); }
});

await Run("disconnect-command-shuts-down-only-current-transport", async () =>
{
    var fixture = await Prepare("disconnect", seedPending: false);
    try
    {
        Assert(CanExecute(fixture.Vm, "DisconnectCommand"), "Disconnect should be enabled while connected.");
        await ExecuteCommand(fixture.Vm, "DisconnectCommand").WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Factory.Transports[0].DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(!(bool)Get(fixture.Vm, "IsConnected")!, "Disconnect left the VM connected.");
        Assert(fixture.Factory.Transports.Count == 1 && fixture.Factory.Transports[0].Runs == 1,
            "Disconnect created or restarted a transport.");
        Assert(CanExecute(fixture.Vm, "ReconnectCommand"), "Previously connected provider should remain reconnectable after disconnect.");
    }
    finally { await Cleanup(fixture); }
});

await Run("close-during-confirmation-cannot-start-replacement", async () =>
{
    var fixture = await Prepare("close-confirm", seedPending: true);
    try
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Confirm(fixture.Vm, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        var action = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Call(fixture.Vm, "Close");
        await action.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Transports.Count == 1, "Closing during confirmation created a new transport.");
        Assert(fixture.Factory.Transports[0].RunToken.IsCancellationRequested, "Closing did not stop the existing connection.");
    }
    finally { await Cleanup(fixture); }
});

await Run("close-during-factory-disposes-unstarted-transport", async () =>
{
    var fixture = await Prepare("close-factory", seedPending: false);
    try
    {
        fixture.Factory.BlockNextFactory();
        var action = ExecuteCommand(fixture.Vm, "ReconnectCommand");
        await fixture.Factory.FactoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Call(fixture.Vm, "Close");
        fixture.Factory.ReleaseFactory();
        await action.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(fixture.Factory.Transports.Count == 2, "Blocked factory did not produce the expected disposable fake.");
        Assert(fixture.Factory.Transports[1].Runs == 0 && fixture.Factory.Transports[1].DisposeCount == 1,
            "A transport returned after close was run or not disposed.");
    }
    finally { await Cleanup(fixture); }
});

await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; all transport activity stayed in memory; no CLI, WinUI window, or network connection started.");
return failures == 0 ? 0 : 1;

internal sealed record Fixture(object Vm, RecordingTransportFactory Factory, object SettingsStore, string Data, string Workspace, object? PendingItem);

public sealed class RecordingTransportFactory(Type transportType)
{
    private readonly Type _transportType = transportType;
    private readonly object _sync = new();
    private object? _blockedCompletion;
    private MethodInfo? _setResult;
    private readonly List<RecordingTransportProxy> _transports = [];
    public List<string> Providers { get; } = [];
    public ConcurrentQueue<string> Events { get; } = new();
    public List<RecordingTransportProxy> Transports { get { lock (_sync) return _transports.ToList(); } }
    public TaskCompletionSource FactoryEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public object Build(string provider)
    {
        var proxy = (RecordingTransportProxy)DispatchProxy.Create(_transportType, typeof(RecordingTransportProxy));
        proxy.Initialize(Transports.Count, Events);
        lock (_sync)
        {
            _transports.Add(proxy);
            Providers.Add(provider);
        }
        var tcsType = typeof(TaskCompletionSource<>).MakeGenericType(_transportType);
        var source = Activator.CreateInstance(tcsType, TaskCreationOptions.RunContinuationsAsynchronously)!;
        var task = tcsType.GetProperty("Task")!.GetValue(source)!;
        lock (_sync)
        {
            if (_blockedCompletion is not null)
            {
                _blockedCompletion = source;
                _setResult = tcsType.GetMethod("SetResult", [_transportType]);
                FactoryEntered.TrySetResult();
                return task;
            }
        }
        tcsType.GetMethod("SetResult", [_transportType])!.Invoke(source, [proxy]);
        return task;
    }

    public void BlockNextFactory()
    {
        lock (_sync)
        {
            FactoryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockedCompletion = new object();
        }
    }

    public void ReleaseFactory()
    {
        object? completion;
        MethodInfo? setter;
        RecordingTransportProxy? proxy;
        lock (_sync)
        {
            completion = _blockedCompletion;
            setter = _setResult;
            proxy = _transports.LastOrDefault();
            _blockedCompletion = null;
            _setResult = null;
        }
        if (completion is not null && setter is not null && proxy is not null) setter.Invoke(completion, [proxy]);
    }

    public async Task<RecordingTransportProxy> WaitForTransportAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var transports = Transports;
            if (transports.Count >= count) return transports[count - 1];
            await Task.Delay(10, timeout.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        throw new TimeoutException("Transport factory was not called.");
    }
}

public class RecordingTransportProxy : DispatchProxy
{
    private int _index;
    private ConcurrentQueue<string> _events = new();
    private CancellationToken _runToken;
    private int _runCount;
    private int _disposeCount;
    private int _replyCount;
    public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs => Volatile.Read(ref _runCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int Replies => Volatile.Read(ref _replyCount);
    public CancellationToken RunToken => _runToken;
    public string? RunSettingsModel { get; private set; }
    public string? RunSettingsArguments { get; private set; }

    internal void Initialize(int index, ConcurrentQueue<string> events) { _index = index; _events = events; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "RunAsync":
                Interlocked.Increment(ref _runCount);
                RunSettingsModel = args![0]!.GetType().GetProperty("AgentModel")!.GetValue(args[0]) as string;
                RunSettingsArguments = args[0]!.GetType().GetProperty("AgentArguments")!.GetValue(args[0]) as string;
                _runToken = (CancellationToken)args[2]!;
                _events.Enqueue($"transport-{_index}-run");
                RunStarted.TrySetResult();
                return WaitUntilCancelledAsync(_runToken);
            case "ReplyAsync":
                Interlocked.Increment(ref _replyCount);
                return Task.FromException(new InvalidOperationException("Fake transport must not send chat replies."));
            case "DisposeAsync":
                return DisposeAsync();
            default:
                throw new InvalidOperationException("Unexpected fake transport call: " + targetMethod?.Name);
        }
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _events.Enqueue($"transport-{_index}-dispose-start");
        await Task.Delay(40);
        _events.Enqueue($"transport-{_index}-dispose-complete");
        DisposeCompleted.TrySetResult();
    }
}
