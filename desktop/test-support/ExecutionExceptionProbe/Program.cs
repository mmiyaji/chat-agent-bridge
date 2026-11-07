using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Invoke the real published inbox exception handler with isolated synthetic data.
if (args.Length != 2) throw new ArgumentException("Usage: ExecutionExceptionProbe <publish-directory> <new-data-directory>");
var publish = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new ArgumentException("Use a new evidence directory.");
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
var recordType = core.GetType("ChatAgentBridge.Core.History.ExecutionRecord", true)!;
var handler = vmType.GetMethod("HandleExecutionExceptionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
object Model(Type type, object values) => JsonSerializer.Deserialize(JsonSerializer.Serialize(values), type)!;
object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
void Set(object value, string name, object? data) => value.GetType().GetProperty(name)!.SetValue(value, data);
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
var results = new List<object>();
var boundaryFailures = 0;
foreach (var language in new[] { "ja-JP", "en-US" })
foreach (var status in new string?[] { null, "running", "failed", "completed", "cancelled", "long-output", "audit-blocked" })
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
    var data = Path.Combine(root, language, status ?? "no-record");
    Directory.CreateDirectory(data);
    if (status == "audit-blocked") File.WriteAllText(Path.Combine(data, "audit-manifest.json"), "block");
    Environment.SetEnvironmentVariable("CAB_WINUI_DATA_DIR", data);
    var vm = Activator.CreateInstance(vmType)!;
    var settings = Activator.CreateInstance(core.GetType("ChatAgentBridge.Core.BridgeSettings", true)!)!;
    var request = Model(transports.GetType("ChatAgentBridge.Transports.ChatRequest", true)!, new
    {
        Provider = "slack", ChannelId = "C_TEST", ThreadId = "1.1", UserId = "U_TEST", Text = "synthetic prompt"
    });
    var item = Activator.CreateInstance(itemType, request, null, settings)!;
    var expectedExecutionId = Guid.NewGuid();
    Set(item, "ExecutionId", expectedExecutionId);
    var initialState = status switch { "failed" => "失敗 (7)", "completed" => "完了", "cancelled" => "キャンセル", _ => "実行中" };
    Set(item, "State", initialState);
    Set(item, "FullOutput", "existing CLI output");
    var isLongOutput = status == "long-output";
    if (isLongOutput)
    {
        var limit = (int)vmType.GetField("MaxDisplayedOutputCharacters", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Set(item, "FullOutput", new string('x', limit));
    }
    var terminalRecorded = status is "failed" or "completed" or "cancelled";
    var record = status is null || isLongOutput ? null : Model(recordType, new
    {
        Status = status == "audit-blocked" ? "running" : status,
        ExitCode = status == "failed" ? 7 : (int?)null
    });
    var task = (Task)handler.Invoke(vm, [item, new UnauthorizedAccessException("PRIVATE_TOKEN C:\\private\\prompt.txt"), record, terminalRecorded])!;
    await task;
    var updated = Get(task, "Result");
    var output = (string)Get(item, "FullOutput")!;
    Assert(output.Contains("UnauthorizedAccessException") && output.Contains("80070005"), "Missing diagnostic type/code");
    Assert(output.IndexOf("UnauthorizedAccessException", StringComparison.Ordinal) >= 0 &&
        (isLongOutput || output.IndexOf("UnauthorizedAccessException", StringComparison.Ordinal) < output.IndexOf("existing CLI output", StringComparison.Ordinal)), "Diagnostic was not prepended");
    Assert(!output.Contains("PRIVATE_TOKEN") && !output.Contains("private") && !output.Contains("synthetic prompt"), "Exception secret leaked");
    if (isLongOutput)
    {
        var limit = (int)vmType.GetField("MaxDisplayedOutputCharacters", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        if (output.Length > limit || output.IndexOf("UnauthorizedAccessException", StringComparison.Ordinal) < 0) boundaryFailures++;
    }
    Assert((string)Get(item, "State")! == (status is null or "running" or "long-output" or "audit-blocked" ? "実行／記録エラー" : initialState), "Incorrect terminal state");
    if (status is "running" or "audit-blocked")
    {
        Assert((string)Get(updated!, "Status")! == "failed" && Get(updated!, "FinishedAt") is not null, "History did not finish");
        Assert(Get(updated!, "HistorySaveDiagnostic") is null, "An execution exception was mislabelled as a history save failure");
        Assert(File.Exists(Path.Combine(data, "history.json")), "History missing");
    }
    else if (status != "long-output") Assert(ReferenceEquals(record, updated), "Existing terminal record replaced");
    var disk = string.Join("\n", Directory.GetFiles(data, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith("audit-manifest.json", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
    Assert(!disk.Contains("PRIVATE_TOKEN") && !disk.Contains("private") && !disk.Contains("synthetic prompt"), "Persisted secret");
    var auditLines = Directory.GetFiles(data, "audit-segment-*.jsonl", SearchOption.AllDirectories)
        .SelectMany(path => File.ReadLines(path)).Where(line => line.Contains("execution_exception", StringComparison.Ordinal)).ToArray();
    var diagnosticPersisted = auditLines.Any(line =>
    {
        using var json = JsonDocument.Parse(line);
        var rootElement = json.RootElement;
        if (!rootElement.TryGetProperty("ExecutionDiagnostic", out var diagnostic)) return false;
        Assert(rootElement.GetProperty("Kind").GetString() == "execution_exception", "Incorrect audit kind");
        var expectedId = record is null ? expectedExecutionId : (Guid)Get(record, "Id")!;
        Assert(rootElement.GetProperty("ExecutionId").GetGuid() == expectedId, "Incorrect execution id");
        Assert(diagnostic.GetProperty("Cause").GetInt32() == 0 &&
            diagnostic.GetProperty("ExceptionType").GetString() == "UnauthorizedAccessException" &&
            diagnostic.GetProperty("HResult").GetString() == "80070005", "Incorrect diagnostic audit payload");
        Assert(!line.Contains("PRIVATE_TOKEN") && !line.Contains("private") && !line.Contains("synthetic prompt"), "Audit secret leaked");
        return true;
    });
    if (status != "audit-blocked") Assert(diagnosticPersisted, $"Safe diagnostic missing from audit log (status={Get(vm, "Status")}; files={string.Join(",", Directory.GetFiles(data).Select(Path.GetFileName))})");
    else Assert(!diagnosticPersisted, "Blocked audit unexpectedly persisted diagnostic");
    if (status == "audit-blocked")
    {
        var textType = desktop.GetType("ChatAgentBridge_Desktop.Localization.DesktopText", true)!;
        var warning = (string)textType.GetMethod("Get")!.Invoke(null, ["InboxAuditSaveFailed", Array.Empty<object>()])!;
        Assert(((string)Get(vm, "Status")!).Contains(warning, StringComparison.Ordinal), "Audit failure warning missing");
    }
    results.Add(new { language, status, outputLength = output.Length, state = Get(item, "State"), diagnosticPersisted });
}
await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {results.Count} inbox handler scenarios. Safe diagnostics displayed and persisted when audit storage is available.");
if (boundaryFailures > 0)
{
    Console.Error.WriteLine($"FAIL: {boundaryFailures} long-output scenarios lost the exception diagnostic at the display limit.");
    Environment.ExitCode = 1;
}
