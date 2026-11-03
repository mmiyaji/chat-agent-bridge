using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;
using Microsoft.UI.Dispatching;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private static readonly TimeSpan ScheduleTick = TimeSpan.FromSeconds(30);
    private readonly ChatScheduleStore _schedules = new();
    private DispatcherQueueTimer? _scheduleTimer;
    private bool _scheduleChecking;

    /// <summary>Starts the due-schedule check. Runs on the UI thread, where requests are accepted.</summary>
    internal void StartScheduleTimer()
    {
        if (_scheduleTimer is not null || App.DispatcherQueue is not { } queue) return;
        _scheduleTimer = queue.CreateTimer();
        _scheduleTimer.Interval = ScheduleTick;
        _scheduleTimer.Tick += async (_, _) => await RunDueSchedulesAsync();
        _scheduleTimer.Start();
    }

    private async Task RunDueSchedulesAsync()
    {
        // Only while connected and idle on this check, so a slow disk never stacks overlapping passes.
        if (_scheduleChecking || _closing || !IsConnectionConfirmed || _transport is not { } transport ||
            _connectionSettings is not { } connected || Volatile.Read(ref _accessSettings) is not { } access ||
            ConnectionProvider is not { } provider || _connection is not { IsCancellationRequested: false } connection) return;
        _scheduleChecking = true;
        try
        {
            var token = connection.Token;
            var now = DateTimeOffset.UtcNow;
            foreach (var schedule in await _schedules.ListAsync(token))
            {
                if (!schedule.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) ||
                    !schedule.IsDue(now, TimeZoneInfo.Local)) continue;
                // Mark first: a crash or rejection must not turn into a run on every tick.
                await _schedules.MarkRunAsync(schedule.Id, now, token);
                var request = new ChatRequest(schedule.Provider, schedule.ChannelId, schedule.ThreadId, schedule.UserId,
                    schedule.Prompt, $"schedule:{schedule.Id}:{now.ToUnixTimeSeconds()}") { GuildId = schedule.GuildId };
                // Access is checked again now, so a revoked user or channel stops its schedules.
                if (!CanReceive(access, request, connected))
                {
                    await AuditScheduleAsync("schedule_skipped", request, token);
                    continue;
                }
                await AuditScheduleAsync("schedule_fired", request, token);
                await AcceptReceivedRequestAsync(request, transport, access, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* A broken schedule file or audit write must not take the inbox down; the next tick retries. */ }
        finally { _scheduleChecking = false; }
    }

    private async Task AuditScheduleAsync(string kind, ChatRequest request, CancellationToken token)
    {
        try { await AuditAsync(kind, request, null, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
    }

    private async Task<string> BuildChatScheduleReplyAsync(ChatRequest request, BridgeSettings received,
        ChatControlCommand command, CancellationToken token)
    {
        var scope = CreateControlScope(request, received);
        var all = await _schedules.ListAsync(token);
        var mine = all.Where(item => ChatScheduleStore.SameOwner(item, request.Provider, scope.Origin, request.ChannelId, request.UserId))
            .OrderBy(item => item.CreatedAt).ToArray();
        var parsed = ChatScheduleCommand.Parse(command.TextArgument);
        switch (parsed.Kind)
        {
            case ChatScheduleCommandKind.Add:
            {
                var (result, schedule) = await _schedules.AddAsync(new ChatSchedule
                {
                    Provider = request.Provider, Origin = scope.Origin, ChannelId = request.ChannelId, ThreadId = request.ThreadId,
                    GuildId = request.GuildId, UserId = request.UserId, Prompt = parsed.Prompt, Kind = parsed.Schedule!.Value,
                    MinuteOfDay = parsed.MinuteOfDay, EveryMinutes = parsed.EveryMinutes, CreatedAt = DateTimeOffset.UtcNow
                }, token);
                if (result == ChatScheduleAddResult.TooManyForUser) return DesktopText.Get("ChatScheduleFull", ChatScheduleStore.MaximumPerUser);
                if (result == ChatScheduleAddResult.TooManyTotal) return DesktopText.Get("ChatScheduleFullTotal");
                await AuditScheduleAsync("schedule_created", request, token);
                return DesktopText.Get("ChatScheduleAdded", schedule!.Id, schedule.Describe(),
                    FormatNextRun(schedule));
            }
            case ChatScheduleCommandKind.Remove:
            {
                var removed = await _schedules.RemoveAsync(parsed.Target, mine, token);
                if (removed is null) return DesktopText.Get("ChatScheduleNotFound", CleanStatusField(parsed.Target, 12));
                await AuditScheduleAsync("schedule_removed", request, token);
                return DesktopText.Get("ChatScheduleRemoved", removed.Id);
            }
            case ChatScheduleCommandKind.List:
            {
                if (mine.Length == 0) return DesktopText.Get("ChatScheduleEmpty");
                var lines = mine.Select((item, index) => DesktopText.Get("ChatScheduleItem", index + 1, item.Id, item.Describe(),
                    FormatNextRun(item), CleanStatusField(item.Prompt, 80)));
                return DesktopText.Get("ChatScheduleHeader", mine.Length, ChatScheduleStore.MaximumPerUser) + "\n" + string.Join("\n", lines);
            }
            default:
                return DesktopText.Get("ChatScheduleUsage", ChatSchedule.MinimumEveryMinutes);
        }
    }

    private static string FormatNextRun(ChatSchedule schedule) =>
        TimeZoneInfo.ConvertTime(schedule.NextRun(TimeZoneInfo.Local), TimeZoneInfo.Local).ToString("MM/dd HH:mm");
}
