using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Exercise the shipped ViewModel without starting WinUI, a CLI, or a chat connection.
if (args.Length != 2) throw new ArgumentException("Usage: SettingsCancellationProbe <published-app-directory> <new-test-data-directory>");
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
var vmType = desktop.GetType("ChatAgentBridge_Desktop.ViewModels.MainPageViewModel", true)!;
var settingsType = core.GetType("ChatAgentBridge.Core.BridgeSettings", true)!;
var storeType = core.GetType("ChatAgentBridge.Core.SettingsStore", true)!;
const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var results = new List<object>();
var failures = 0;
object? Get(object vm, string name) => vmType.GetProperty(name)!.GetValue(vm);
void Set(object vm, string name, object? value) => vmType.GetProperty(name)!.SetValue(vm, value);
object? Call(object vm, string name, params object?[] values) => vmType.GetMethod(name, Methods)!.Invoke(vm, values);
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string Snapshot(object vm) => JsonSerializer.Serialize(Call(vm, "CreateSettingsSnapshot"), settingsType);
async Task<bool> Save(object vm) => await (Task<bool>)Call(vm, "SavePendingSettingsAsync", false)!;
void Confirm(object vm, Func<Task<bool>> callback)
{
    var property = vmType.GetProperty("ConfirmSensitiveSettingsAsync")!;
    var parameters = property.PropertyType.GetMethod("Invoke")!.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
    property.SetValue(vm, Expression.Lambda(property.PropertyType,
        Expression.Invoke(Expression.Constant(callback)), parameters).Compile());
}
async Task<(object Vm, string Data, string Baseline, byte[] Disk)> Prepare(string name)
{
    var data = Path.Combine(root, name);
    Directory.CreateDirectory(data);
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", data);
    var baseline = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
    {
        WorkspaceRoot = data, TrustedWorkspaceRoots = data,
        AgentProvider = "codex", AgentExecutable = "codex", AgentArguments = "exec --json -",
        AgentModel = "baseline-model", AgentReasoningEffort = "high", AgentMode = "app-server"
    }), settingsType)!;
    var store = Activator.CreateInstance(storeType, data)!;
    await (Task)storeType.GetMethod("SaveAsync")!.Invoke(store, [baseline, CancellationToken.None])!;
    var vm = Activator.CreateInstance(vmType)!;
    await (Task)Call(vm, "LoadAsync")!;
    Set(vm, "IsOnboardingActive", false);
    Assert((bool)Get(vm, "IsSettingsLoaded")!, "Fixture settings did not load");
    Confirm(vm, () => Task.FromResult(false));
    return (vm, data, Snapshot(vm), File.ReadAllBytes(Path.Combine(data, "settings.json")));
}
void Unchanged((object Vm, string Data, string Baseline, byte[] Disk) fixture)
{
    Assert(Snapshot(fixture.Vm) == fixture.Baseline, "Rejected values remain in the editor");
    Assert(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json")).SequenceEqual(fixture.Disk), "Rejected settings were written");
    Assert(!(bool)Get(fixture.Vm, "HasUnsavedSettings")!, "Canceled transaction remains dirty");
    Assert(!(bool)Get(fixture.Vm, "IsSensitiveConfirmationPending")!, "Canceled transaction remains pending");
}
async Task Run(string name, Func<Task> test)
{
    try { await test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.GetBaseException().Message }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
}

foreach (var culture in new[] { "en", "ja" })
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    foreach (var provider in new[] { "claude", "gemini", "github-copilot" })
        await Run(culture + "-cancel-provider-" + provider, async () =>
        {
            var fixture = await Prepare(culture + "-provider-" + provider);
            Set(fixture.Vm, "AgentProvider", provider);
            Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
            Unchanged(fixture);
            Set(fixture.Vm, "PostConnectionNotice", true);
            Assert(await Save(fixture.Vm), "Unrelated edit did not save");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Data, "settings.json")));
            Assert(saved.RootElement.GetProperty("AgentProvider").GetString() == "codex", "Canceled provider was saved by a later edit");
        });

    var cases = new (string Name, Action<object> Edit)[]
    {
        ("permissions", vm => { Set(vm, "AgentPermissionMode", "full-access"); Set(vm, "AllowAgentNetwork", true); Set(vm, "RequireApproval", false); }),
        ("chat-access", vm => { Set(vm, "ChatApprovalMode", "auto"); Set(vm, "SlackAllowedUserIds", "U_TEST"); Set(vm, "MattermostAllowedUserIds", "fixtureuser"); Set(vm, "SlackAllowedChannelIds", "C_TEST"); Set(vm, "SlackAllowedThreadIds", "123.456"); Set(vm, "MattermostAllowedThreadIds", "fixturethread"); }),
        ("sharing", vm => { Set(vm, "ChatResultMode", "auto"); Set(vm, "ShareApprovalsInChat", true); Set(vm, "ShareAgentQuestionsInChat", true); }),
        ("attachments", vm => { Set(vm, "FileAttachmentApprovalMode", "auto"); Set(vm, "FileAttachmentMaxSizeMb", 15d); Set(vm, "FileAttachmentMaxCount", 4d); Set(vm, "FileAttachmentMaxTotalSizeMb", 30d); }),
        ("credentials", vm => { Call(vm, "SetMattermostToken", "synthetic-mattermost-token"); Set(vm, "SlackBotToken", "synthetic-bot-token"); Set(vm, "SlackAppToken", "synthetic-app-token"); Set(vm, "MattermostServerUrl", "https://example.invalid"); Set(vm, "MattermostChannelId", "fixturechannel"); }),
        ("cli-command", vm => { Set(vm, "AgentExecutable", "codex.exe"); Set(vm, "AgentArguments", "exec --json --skip-git-repo-check -"); }),
        ("trusted-folders", vm => { var nested = Path.Combine((string)Get(vm, "WorkspaceRoot")!, "nested"); Directory.CreateDirectory(nested); Set(vm, "TrustedWorkspaceRoots", Get(vm, "TrustedWorkspaceRoots") + Environment.NewLine + nested); }),
        ("workspace-with-sensitive-change", vm => { Set(vm, "WorkspaceRoot", Path.Combine((string)Get(vm, "WorkspaceRoot")!, "nested")); Directory.CreateDirectory((string)Get(vm, "WorkspaceRoot")!); Set(vm, "AgentPermissionMode", "full-access"); Set(vm, "AutoConnectProvider", "slack"); })
    };
    foreach (var test in cases)
        await Run(culture + "-cancel-" + test.Name, async () =>
        {
            var fixture = await Prepare(culture + "-" + test.Name);
            test.Edit(fixture.Vm);
            Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
            Unchanged(fixture);
        });
    await Run(culture + "-retry-canceled-selection", async () =>
    {
        var fixture = await Prepare(culture + "-retry");
        Set(fixture.Vm, "AgentProvider", "gemini");
        Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
        Unchanged(fixture);
        var confirmations = 0;
        Confirm(fixture.Vm, () => { confirmations++; return Task.FromResult(true); });
        Set(fixture.Vm, "AgentProvider", "gemini");
        Assert(await Save(fixture.Vm) && confirmations == 1, "Selecting the same provider again cannot be confirmed");
    });
    await Run(culture + "-cancel-codex-from-claude", async () =>
    {
        var fixture = await Prepare(culture + "-codex-from-claude");
        Confirm(fixture.Vm, () => Task.FromResult(true));
        Set(fixture.Vm, "AgentProvider", "claude");
        Set(fixture.Vm, "AgentModel", "sonnet");
        Assert(await Save(fixture.Vm), "Could not prepare Claude baseline");
        fixture = (fixture.Vm, fixture.Data, Snapshot(fixture.Vm), File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json")));
        Confirm(fixture.Vm, () => Task.FromResult(false));
        Set(fixture.Vm, "AgentProvider", "codex");
        Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
        Unchanged(fixture);
    });
    await Run(culture + "-cancel-registered-profile", async () =>
    {
        var fixture = await Prepare(culture + "-registered-profile");
        var summary = Get(fixture.Vm, "SavedAgentSummary");
        var profileType = core.GetType("ChatAgentBridge.Core.AgentConnectionProfile", true)!;
        var profile = Activator.CreateInstance(profileType, ["claude", "claude", "", "exec", "sonnet", null, "inherit", false, "fixture", DateTimeOffset.UtcNow])!;
        Call(fixture.Vm, "ApplyRegisteredConnection", profile);
        Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
        Unchanged(fixture);
        Assert(Equals(summary, Get(fixture.Vm, "SavedAgentSummary")), "Registered profile summary was not restored");
        Assert((string)Get(fixture.Vm, "ConnectionRegistrationStatus")! == "", "Canceled profile still reports loaded");
    });
    foreach (var unavailable in new[] { "missing", "throws" })
        await Run(culture + "-confirmation-" + unavailable, async () =>
        {
            var fixture = await Prepare(culture + "-" + unavailable);
            if (unavailable == "missing") Set(fixture.Vm, "ConfirmSensitiveSettingsAsync", null);
            else Confirm(fixture.Vm, () => throw new InvalidOperationException("Synthetic confirmation failure"));
            Set(fixture.Vm, "AgentProvider", "claude");
            Assert(!await Save(fixture.Vm), "Unavailable confirmation unexpectedly succeeded");
            Unchanged(fixture);
        });
    await Run(culture + "-preserve-later-edit", async () =>
    {
        var fixture = await Prepare(culture + "-later-edit");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Confirm(fixture.Vm, () => { entered.SetResult(); return answer.Task; });
        Set(fixture.Vm, "AgentPermissionMode", "full-access");
        var pending = Save(fixture.Vm);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Set(fixture.Vm, "PostConnectionNotice", true);
        Set(fixture.Vm, "FileAttachmentMaxCount", 2.5d);
        answer.SetResult(false);
        Assert(!await pending, "Cancel unexpectedly succeeded");
        Assert((string)Get(fixture.Vm, "AgentPermissionMode")! == "inherit", "Rejected permission remains");
        Assert((bool)Get(fixture.Vm, "PostConnectionNotice")!, "Later ordinary edit was lost");
        Assert((double)Get(fixture.Vm, "FileAttachmentMaxCount")! == 2.5d, "Later raw number edit was lost");
        Assert(File.ReadAllBytes(Path.Combine(fixture.Data, "settings.json")).SequenceEqual(fixture.Disk), "Pending edits were persisted");
    });
    await Run(culture + "-confirm-only-later-sensitive-edit", async () =>
    {
        var fixture = await Prepare(culture + "-later-sensitive");
        Confirm(fixture.Vm, () => { Set(fixture.Vm, "ChatApprovalMode", "auto"); return Task.FromResult(false); });
        Set(fixture.Vm, "AgentPermissionMode", "full-access");
        Assert(!await Save(fixture.Vm), "Cancel unexpectedly succeeded");
        Assert((string)Get(fixture.Vm, "AgentPermissionMode")! == "inherit", "Rejected permission remains");
        Assert((string)Get(fixture.Vm, "ChatApprovalMode")! == "auto", "Later sensitive edit was lost");
        Confirm(fixture.Vm, () => Task.FromResult(true));
        Assert(await Save(fixture.Vm), "Later sensitive edit cannot be confirmed");
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Data, "settings.json")));
        Assert(saved.RootElement.GetProperty("AgentPermissionMode").GetString() == "inherit", "Canceled permission was resurrected");
    });
}
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; no CLI or chat processes started.");
return failures == 0 ? 0 : 1;
