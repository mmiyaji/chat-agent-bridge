using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Localization;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class InboxItem
{
    private const int MaxRecentActivity = 4;
    private readonly object _activityGate = new();
    private readonly List<string> _recentActivity = new();
    private int _activityCount;

    // Set before the terminal progress edit; both stay null when the run produced nothing to report.
    internal string? ProgressUsage { get; set; }
    internal string? ProgressPermissionText { get; set; }

    internal void ResetActivity()
    {
        lock (_activityGate) { _recentActivity.Clear(); _activityCount = 0; }
    }

    internal void AddActivity(string line)
    {
        lock (_activityGate)
        {
            // Claude reports a tool by name when it starts and again with its input; keep only the detailed line.
            if (_recentActivity.Count > 0 && _recentActivity[^1] is var last &&
                (line == last || line.StartsWith(last + ":", StringComparison.Ordinal)))
            {
                _recentActivity[^1] = line;
                return;
            }
            _activityCount++;
            if (_recentActivity.Count == MaxRecentActivity) _recentActivity.RemoveAt(0);
            _recentActivity.Add(line);
        }
    }

    internal (int Count, string[] Recent) SnapshotActivity()
    {
        lock (_activityGate) return (_activityCount, _recentActivity.ToArray());
    }
}

public sealed partial class ChatInboxViewModel
{
    private void InitializeProgress(InboxItem item)
    {
        if (item.Transport is not IChatProgressTransport) return;
        item.Progress = new ChatProgressReporter(item.Transport, item.Request,
            (state, elapsed) => FormatProgress(item, state, elapsed, item.Transport is MattermostWebSocketTransport),
            token => CanPublishProgressAsync(item, token),
            _ => App.DispatcherQueue.TryEnqueue(() => ProgressNotice = DesktopText.Get("ProgressNoticeFailed")));
        item.ReceiptNotification = item.Progress.ReceiveAsync();
    }

    private async Task<bool> CanPublishProgressAsync(InboxItem item, CancellationToken token)
    {
        if (!ReferenceEquals(item.Transport, _transport)) return false;
        var current = await _store.LoadAsync(token).ConfigureAwait(false);
        return HasAccess(current, item.Request, item.Settings);
    }

    private string FormatProgress(InboxItem item, ChatProgressState state, TimeSpan elapsed, bool reactionCancellation)
    {
        var key = state switch
        {
            ChatProgressState.Received => "ProgressReceived",
            ChatProgressState.Running => "ProgressRunning",
            ChatProgressState.Completed => "ProgressCompleted",
            ChatProgressState.Failed => "ProgressFailed",
            ChatProgressState.Cancelled => "ProgressCancelled",
            ChatProgressState.Rejected => "ProgressRejected",
            ChatProgressState.Disconnected => "ProgressDisconnected",
            ChatProgressState.FollowUpApplied => "ProgressFollowUpApplied",
            ChatProgressState.FollowUpPending => "ProgressFollowUpPending",
            ChatProgressState.FollowUpUncertain => "ProgressFollowUpUncertain",
            _ => "ProgressReceived"
        };
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        var duration = $"{seconds / 60:00}:{seconds % 60:00}";
        var lines = new List<string> { DesktopText.Get(key, duration) };
        if (item.EffectiveSettings is { } effective && state is ChatProgressState.Running or ChatProgressState.Completed
                or ChatProgressState.Failed or ChatProgressState.Cancelled)
        {
            lines.Add(DesktopText.Get("ProgressDetails", CleanStatusField(AgentDisplayName(effective), 60),
                CleanStatusField(effective.AgentModel ?? DesktopText.Get("CliDefault"), 100),
                item.ProgressPermissionText ??= PermissionDisplay(effective)));
            var (count, recent) = item.SnapshotActivity();
            // Recent tool and command lines are only echoed where results are already shared in chat.
            if (state == ChatProgressState.Running && count > 0 && effective.ChatResultMode == "auto")
                lines.AddRange(recent.Select(line => "• " + line));
            else if (count > 0)
                lines.Add(DesktopText.Get("ProgressActivityCount", count));
            if (state is ChatProgressState.Completed or ChatProgressState.Failed && !string.IsNullOrEmpty(item.ProgressUsage))
                lines.Add(item.ProgressUsage);
            if (state == ChatProgressState.Running)
            {
                var scope = CreateSessionKey(item);
                lines.Add(DesktopText.Get(ChatFollowUpPolicy.CanSteer(item.Settings, effective, scope, scope)
                    ? "ProgressFollowUpSteerHint" : "ProgressFollowUpQueueHint"));
            }
        }
        if (reactionCancellation && state is ChatProgressState.Received or ChatProgressState.Running)
            lines.Add(DesktopText.Get("ProgressReactionCancelHint"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Describes the permission an agent run really gets, resolving "inherit" against the provider's own settings.</summary>
    private static string PermissionDisplay(BridgeSettings settings)
    {
        EffectivePermissionInfo info;
        try { info = EffectivePermission.Resolve(settings); }
        catch (Exception) { return PermissionLabel(settings.AgentPermissionMode); }
        var text = PermissionLabel(info.ConfiguredMode);
        if (info is { ConfiguredMode: "inherit", NativeMode: { } native })
        {
            var agent = CleanStatusField(AgentDisplayName(settings), 60);
            var source = info.Source switch
            {
                PermissionSource.ProjectLocalSettings => DesktopText.Get("PermissionSourceProjectLocal", agent),
                PermissionSource.ProjectSettings => DesktopText.Get("PermissionSourceProject", agent),
                PermissionSource.UserSettings => DesktopText.Get("PermissionSourceUser", agent),
                _ => DesktopText.Get("CliDefault")
            };
            text = DesktopText.Get("PermissionInheritResolved", source, CleanStatusField(native, 64));
            if (info.Unattended) text += DesktopText.Get("PermissionUnattendedNote");
        }
        return info.Unattended ? DesktopText.Get("PermissionUnattendedMarker", text) : text;
    }

    private static string? FormatProgressUsage(AgentUsage? usage)
    {
        // Thread totals would be cumulative, not what this chat request used.
        if (usage is null || usage.Scope is not ("turn" or "last-request") ||
            usage.TotalInputTokens is null && usage.OutputTokens is null) return null;
        static string Count(long? value) => value is long count ? count.ToString("N0", CultureInfo.CurrentCulture) : CoreText.Get("NotReported");
        var text = DesktopText.Get("ProgressUsage", Count(usage.TotalInputTokens), Count(usage.OutputTokens));
        return AgentCostEstimator.Estimate(usage) is { } cost
            ? text + DesktopText.Get("ProgressUsageCost", FormatProgressCost(cost)) : text;
    }

    private static string FormatProgressCost(AgentCostEstimate estimate)
    {
        static string Usd(decimal amount) => amount == 0 ? "$0" : Math.Abs(amount) < 0.000001m ? "<$0.000001"
            : "$" + amount.ToString("0.######", CultureInfo.InvariantCulture);
        var minimum = Usd(estimate.MinUsd);
        var maximum = Usd(estimate.MaxUsd);
        if (estimate.MinUsd == estimate.MaxUsd) return minimum;
        if (minimum == maximum) return minimum == "<$0.000001" ? minimum : "≈" + maximum;
        return DesktopText.Get("UsageCostRange", minimum, maximum);
    }

    private static IProgress<AgentRunProgress> TapActivity(InboxItem item, IProgress<AgentRunProgress> inner)
    {
        item.ResetActivity();
        return new ActivityTap(item, inner);
    }

    private sealed class ActivityTap(InboxItem item, IProgress<AgentRunProgress> inner) : IProgress<AgentRunProgress>
    {
        public void Report(AgentRunProgress value)
        {
            inner.Report(value);
            try
            {
                if (value is not { Stream: "activity", Text: { Length: > 0 } text }) return;
                var line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
                if (line is null || !IsToolOrCommandActivity(line)) return;
                item.AddActivity(CleanStatusField(line, 120));
                item.Progress?.RequestUpdate();
            }
            catch (Exception) { /* Chat progress must never disturb the agent run. */ }
        }
    }

    /// <summary>Keeps only "Using tool" and "Running command" lines, not assistant prose or tool output.</summary>
    private static bool IsToolOrCommandActivity(string line)
    {
        const string marker = "\u0001";
        foreach (var key in new[] { "ProgressUsingTool", "ProgressRunningCommand" })
        {
            var template = CoreText.Get(key, marker);
            var at = template.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var prefix = template[..at];
            var suffix = template[(at + 1)..];
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                (suffix.Length == 0 || line.IndexOf(suffix, prefix.Length, StringComparison.Ordinal) >= 0))
                return true;
        }
        return line == CoreText.Get("ProgressUsingGenericTool");
    }

    private static async Task StartProgressAsync(InboxItem item)
    {
        if (item.Progress is null) return;
        await item.ReceiptNotification;
        await item.Progress.StartAsync();
    }

    private static async Task FinishProgressAsync(InboxItem item, ChatProgressState state,
        CancellationToken cancellationToken = default)
    {
        if (item.Progress is null) return;
        await item.ReceiptNotification;
        await item.Progress.FinishAsync(state, cancellationToken);
        if (state is ChatProgressState.FollowUpApplied or ChatProgressState.FollowUpUncertain)
            await item.Progress.DisposeAsync();
    }

    private async Task FinishConnectionProgressAsync(IChatTransport transport)
    {
        var pending = Requests.Where(item => ReferenceEquals(item.Transport, transport) && item.Progress is not null).ToArray();
        foreach (var item in Requests.Where(item => ReferenceEquals(item.Transport, transport)).ToArray())
            await CompleteRequestActionsAsync(item);
        await FinishProgressBatchAsync(pending, ChatProgressState.Disconnected, dispose: true);
    }

    private static async Task FinishProgressBatchAsync(IEnumerable<InboxItem> items, ChatProgressState state,
        bool dispose = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = CancellationToken.None
        }, async (item, _) =>
        {
            await FinishProgressAsync(item, state, timeout.Token);
            if (dispose && item.Progress is not null) await item.Progress.DisposeAsync();
        });
    }
}
