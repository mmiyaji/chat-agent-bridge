using System.Globalization;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly ChannelMemoryStore _channelMemory = new();

    private async Task<string> BuildChannelMemoryReplyAsync(ChatRequest request, BridgeSettings received,
        ChatControlCommand command, CancellationToken token)
    {
        var scope = CreateControlScope(request, received);
        switch (command.Kind)
        {
            case ChatControlCommandKind.Remember:
            {
                var (result, count) = await _channelMemory.AddAsync(scope, command.TextArgument ?? "", token);
                return result switch
                {
                    ChannelMemoryAddResult.Added => DesktopText.Get("ChatMemorySaved", count, ChannelMemoryStore.MaximumNotes),
                    ChannelMemoryAddResult.TooLong => DesktopText.Get("ChatMemoryTooLong", ChannelMemoryStore.MaximumNoteLength),
                    ChannelMemoryAddResult.Full => DesktopText.Get("ChatMemoryFull", ChannelMemoryStore.MaximumNotes),
                    _ => DesktopText.Get("ChatMemoryRememberUsage")
                };
            }
            case ChatControlCommandKind.Forget:
            {
                var argument = command.TextArgument ?? "";
                if (argument.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var removed = await _channelMemory.ClearAsync(scope, token);
                    return removed == 0 ? DesktopText.Get("ChatMemoryEmpty") : DesktopText.Get("ChatMemoryForgotAll", removed);
                }
                if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    return DesktopText.Get("ChatMemoryForgetUsage");
                return await _channelMemory.ForgetAsync(scope, number, token)
                    ? DesktopText.Get("ChatMemoryForgot", number) : DesktopText.Get("ChatMemoryNoSuchNote", number);
            }
            default:
            {
                var notes = await _channelMemory.ListAsync(scope, token);
                if (notes.Count == 0) return DesktopText.Get("ChatMemoryEmpty");
                var lines = notes.Select((note, index) =>
                    DesktopText.Get("ChatMemoryItem", index + 1, CleanStatusField(note, ChannelMemoryStore.MaximumNoteLength)));
                return DesktopText.Get("ChatMemoryHeader", notes.Count, ChannelMemoryStore.MaximumNotes) + "\n" + string.Join("\n", lines);
            }
        }
    }

    /// <summary>Prepends this channel's saved notes to an agent prompt. A store failure never blocks the run.</summary>
    private async Task<string> WithChannelMemoryAsync(InboxItem item, string prompt, CancellationToken token)
    {
        try
        {
            var notes = await _channelMemory.ListAsync(CreateSessionKey(item), token);
            return ChannelMemoryStore.FormatPromptBlock(notes) + prompt;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return prompt; }
    }

    private async Task<string> BuildChatUsageReplyAsync(ChatRequest request, CancellationToken token)
    {
        IReadOnlyList<ExecutionRecord> records = await _history.ListAsync(token);
        var thread = ChatUsageSummary.Summarize(ChatUsageSummary.ForThread(records, request.Provider,
            request.ChannelId, request.ThreadId, request.UserId));
        var today = ChatUsageSummary.Summarize(ChatUsageSummary.ForRequesterOnLocalDay(records, request.Provider,
            request.UserId, DateTime.Now));
        return DesktopText.Get("ChatUsageReply", FormatUsageLine(thread), FormatUsageLine(today));
    }

    private static string FormatUsageLine(ChatUsageSummary summary)
    {
        var unknown = DesktopText.Get("ChatUsageUnknown");
        static string Count(long? value, string fallback) =>
            value is long count ? count.ToString("N0", CultureInfo.CurrentCulture) : fallback;
        return DesktopText.Get("ChatUsageLine", summary.Runs, Count(summary.InputTokens, unknown),
            Count(summary.OutputTokens, unknown), summary.Cost is { } cost ? FormatProgressCost(cost) : unknown);
    }
}
