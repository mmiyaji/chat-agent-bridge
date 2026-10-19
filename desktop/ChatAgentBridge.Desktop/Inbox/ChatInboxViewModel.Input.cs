using System.Collections.Concurrent;
using System.Text;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;
using ChatAgentBridge.Transports;
using ChatAgentBridge_Desktop.Localization;

namespace ChatAgentBridge_Desktop.Inbox;

public sealed partial class ChatInboxViewModel
{
    private readonly ChatInputBroker _inputBroker = new(FormatChatInputRequest);
    private readonly ConcurrentDictionary<InboxItem, byte> _waitingForInput = new();

    private async Task<IReadOnlyDictionary<string, string[]>?> HandleChatInputAsync(
        InboxItem item, AppServerInput input, CancellationToken token)
    {
        if (!await CheckAccessAsync(item, token)) return null;
        _waitingForInput.TryAdd(item, 0);
        try
        {
            var current = Volatile.Read(ref _accessSettings);
            var answers = await _inputBroker.RequestAsync(item.Transport, item.Request,
                CreateSessionKey(item).Origin ?? string.Empty, input,
                incoming => Volatile.Read(ref _accessSettings) is { } settings &&
                    CanHandleControl(item.Transport, item.Settings, incoming, settings),
                ServerInputHandler, token, publishToThread: current?.ShareAgentQuestionsInChat == true);
            return answers is not null && await CheckAccessAsync(item, token) ? answers : null;
        }
        finally { _waitingForInput.TryRemove(item, out _); }
    }

    private async Task<bool> TryHandleChatAnswerAsync(IChatTransport transport, BridgeSettings received,
        ChatRequest request, CancellationToken token)
    {
        var kind = ChatControlCommand.Parse(request.Text).Kind;
        if (kind is not (ChatControlCommandKind.Answer or ChatControlCommandKind.ReservedInvalid)) return false;
        var current = await _store.LoadAsync(token);
        Volatile.Write(ref _accessSettings, current);
        if (CanHandleControl(transport, received, request, current))
            _inputBroker.TryHandle(request, CreateControlScope(request, received).Origin ?? string.Empty);
        return true;
    }

    private static string FormatChatInputRequest(AppServerInput input, string code)
    {
        var text = new StringBuilder(DesktopText.Get("ChatInputTitle"));
        for (int i = 0; i < input.Questions.Count; i++)
        {
            var question = input.Questions[i];
            text.AppendLine().AppendLine().Append(i + 1).Append(". ")
                .Append(SafeQuestionText(question.Header)).Append(": ").Append(SafeQuestionText(question.Question));
            for (int j = 0; j < question.Options.Count; j++)
                text.AppendLine().Append("  ").Append(j + 1).Append(") ")
                    .Append(SafeQuestionText(question.Options[j].Label)).Append(" — ")
                    .Append(SafeQuestionText(question.Options[j].Description));
            text.AppendLine().Append(DesktopText.Get(question.Options.Count == 0 ? "ChatInputTextReply" : "ChatInputOptionReply",
                code, i + 1));
            if (question.IsOther && question.Options.Count > 0)
                text.AppendLine().Append(DesktopText.Get("ChatInputOtherReply", code, i + 1));
        }
        return text.ToString();
    }

    private static string SafeQuestionText(string value) => value.Replace('@', '＠').Replace('<', '＜')
        .Replace('>', '＞').Replace('`', '｀');
}
