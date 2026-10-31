#:project ../ChatAgentBridge.Core/ChatAgentBridge.Core.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Writes sample conversations and execution records into a demo data folder so the History and usage pages have
// something to show. Every conversation, token count and duration here is generated sample data, not a measurement.
// Usage: dotnet run SeedActivity.cs -- <data dir> <mattermost-demo.json> <ja|en> <weather-cli dir> <todo-cli dir>
using System.Text.Json;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Sessions;

if (args.Length < 5) { Console.Error.WriteLine("Usage: SeedActivity <data dir> <mattermost-demo.json> <ja|en> <weather dir> <todo dir>"); return 2; }
var dataDir = Path.GetFullPath(args[0]);
var state = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
var serverUrl = state.GetProperty("serverUrl").GetString()!;
var channelId = state.GetProperty("channelId").GetString()!;
var userId = state.GetProperty("alice").GetProperty("id").GetString()!;
var ja = args[2] == "ja";
var weather = Path.GetFullPath(args[3]);
var todo = Path.GetFullPath(args[4]);

// Agents that appear in the demo; models come from the bundled price catalog so estimated costs are shown.
var claude = new Agent("claude", "", "claude.stream-json", "turn", ["claude-sonnet-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5"]);
var codex = new Agent("codex", "exec --json -", "codex.exec-json", "turn", ["gpt-5.4"]);
var agy = new Agent("agy.exe", "--output-format stream-json", "", "turn", [null]);

var conversations = new[]
{
    new Conversation(ja ? "天気CLIの概要を教えて" : "Explain the weather CLI", weather, claude, 17),
    new Conversation(ja ? "大阪のテストを追加" : "Add an Osaka test", weather, claude, 11),
    new Conversation(ja ? "ToDo CLIの使い方" : "How the todo CLI works", todo, claude, 13),
    new Conversation(ja ? "未完了タスクの一覧表示" : "List unfinished tasks", todo, codex, 9),
    new Conversation(ja ? "READMEの誤字を確認" : "Proofread the README", weather, agy, 12),
    new Conversation(ja ? "テストの失敗原因を調査" : "Find why a test fails", todo, agy, 8),
};

var random = new Random(20261026);
var today = DateTimeOffset.Now.Date;
var sessions = new SessionStore(dataDir);
var history = new ExecutionHistoryStore(dataDir);
var total = 0;
var audit = new List<AuditEvent>();
foreach (var (conversation, index) in conversations.Select((item, index) => (item, index)))
{
    var thread = Convert.ToHexString(random.NextInt64().ToString().Select(c => (byte)c).Take(13).ToArray()).ToLowerInvariant()[..26];
    var key = ChatSessionScope.Create("mattermost", channelId, thread, userId, serverUrl);
    var sessionId = Guid.NewGuid().ToString();
    var started = new List<DateTimeOffset>();
    for (var n = 0; n < conversation.Runs; n++)
    {
        // Busier on weekdays, quiet on weekends, spread over the last 28 days.
        DateTimeOffset when;
        do { when = today.AddDays(-random.Next(0, 28)).AddHours(9 + random.NextDouble() * 11); }
        while (when.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && random.NextDouble() < 0.7 || when > DateTimeOffset.Now);
        started.Add(when);
    }
    foreach (var start in started.Order())
    {
        var model = conversation.Agent.Models[random.Next(conversation.Agent.Models.Length)];
        var input = random.NextInt64(4_000, 90_000);
        var duration = random.Next(6_000, 48_000);
        var roll = random.NextDouble();
        var status = roll < 0.88 ? "completed" : roll < 0.95 ? "failed" : "cancelled";
        var hasUsage = conversation.Agent.Source.Length > 0 && status != "cancelled";
        var executionId = Guid.NewGuid();
        // Same event kinds the app writes: approval, start, then the reply (or its failure).
        audit.Add(new AuditEvent(start.AddSeconds(-3).ToUniversalTime(), "approved", executionId, "mattermost", channelId, thread));
        audit.Add(new AuditEvent(start.ToUniversalTime(), "started", executionId, "mattermost", channelId, thread));
        if (status != "cancelled")
            audit.Add(new AuditEvent(start.AddMilliseconds(duration).ToUniversalTime(),
                status == "completed" ? "reply_sent" : "reply_failed", executionId, "mattermost", channelId, thread));
        await history.RecordAsync(new ExecutionRecord
        {
            Id = executionId,
            StartedAt = start.ToUniversalTime(),
            FinishedAt = start.AddMilliseconds(duration).ToUniversalTime(),
            Source = "mattermost", ChannelId = channelId, ThreadId = thread, UserId = userId,
            WorkspaceRoot = conversation.Workspace, Status = status, ExitCode = status == "completed" ? 0 : 1,
            SessionId = sessionId, AgentModel = model,
            Usage = hasUsage ? new AgentUsage
            {
                InputTokens = input, OutputTokens = random.NextInt64(300, 4_500),
                CachedInputTokens = (long)(input * (0.35 + random.NextDouble() * 0.5)),
                // Usage that excludes cache (Claude) counts only when read and write tokens are both reported.
                CacheWriteInputTokens = conversation.Agent.Source == "claude.stream-json" ? random.NextInt64(200, Math.Max(201, input / 12)) : null,
                InputIncludesCache = conversation.Agent.Source != "claude.stream-json",
                Scope = conversation.Agent.Scope, Source = conversation.Agent.Source, Model = model
            } : null,
            Metrics = new AgentExecutionMetrics
            {
                ProcessStartMilliseconds = random.Next(180, 900), InitializeMilliseconds = random.Next(300, 2_400),
                TotalMilliseconds = duration, QueueMilliseconds = random.Next(0, 400)
            }
        });
        total++;
    }
    await sessions.SaveAsync(new SessionBinding(key, conversation.Workspace, conversation.Agent.Executable, conversation.Agent.Arguments,
        sessionId, started.Max().ToUniversalTime(), conversation.Agent.Models[0], null, null, null, null, null, conversation.Title));
}
await new AuditStore(dataDir).AppendManyAsync(audit.OrderBy(item => item.Timestamp));
Console.WriteLine($"seeded {total} executions, {audit.Count} audit events, {conversations.Length} conversations");
return 0;

internal sealed record Agent(string Executable, string Arguments, string Source, string Scope, string?[] Models);
internal sealed record Conversation(string Title, string Workspace, Agent Agent, int Runs);
