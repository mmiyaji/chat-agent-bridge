using System.Security.Cryptography;
using ChatAgentBridge.Core;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Transports;

/// <summary>Races a local input dialog against an explicitly shared, scoped chat answer.</summary>
public sealed class ChatInputBroker
{
    private const int MaximumAnswerLength = 2000;
    private const int MaximumQuestions = 10;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingInput> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<AppServerInput, string, string> _formatRequest;
    private readonly TimeSpan _timeout;
    private readonly int _maximumPending;

    public ChatInputBroker(
        Func<AppServerInput, string, string> formatRequest,
        TimeSpan? timeout = null,
        int maximumPending = 20)
    {
        _formatRequest = formatRequest ?? throw new ArgumentNullException(nameof(formatRequest));
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _maximumPending = maximumPending;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (_maximumPending <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPending));
    }

    public async Task<IReadOnlyDictionary<string, string[]>?> RequestAsync(
        IChatTransport transport,
        ChatRequest origin,
        string originId,
        AppServerInput input,
        Func<ChatRequest, bool> isAuthorized,
        Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? localHandler,
        CancellationToken cancellationToken = default,
        bool publishToThread = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(isAuthorized);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(originId) || !TrySnapshotInput(input, out var safeInput))
            return null;
        if (!IsAuthorized(isAuthorized, origin))
            return null;

        // Secret-bearing questions are always answered locally, even when chat sharing is enabled.
        bool share = publishToThread && !safeInput.Questions.Any(question => question.IsSecret);
        if (!share)
            return await RequestLocallyAsync(safeInput, localHandler, cancellationToken).ConfigureAwait(false);

        var pending = new PendingInput(origin, originId, safeInput, isAuthorized);
        bool atCapacity;
        lock (_sync)
        {
            atCapacity = _pending.Count >= _maximumPending;
            if (!atCapacity)
            {
                do { pending.Code = RandomNumberGenerator.GetHexString(12); }
                while (_pending.ContainsKey(pending.Code));
                _pending.Add(pending.Code, pending);
            }
        }
        if (atCapacity)
            return await RequestLocallyAsync(safeInput, localHandler, cancellationToken).ConfigureAwait(false);

        using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            string? message = FormatPrompt(safeInput, pending.Code);
            bool published = false;
            if (message is not null && IsAuthorized(isAuthorized, origin))
            {
                try
                {
                    await transport.ReplyAsync(origin, message, cancellationToken).ConfigureAwait(false);
                    published = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The local UI remains available when remote publication fails.
                }
            }

            if (!published)
            {
                RemovePending(pending);
                return await RequestLocallyAsync(safeInput, localHandler, cancellationToken).ConfigureAwait(false);
            }

            Task<IReadOnlyDictionary<string, string[]>?>? localTask = localHandler is null
                ? null
                : InvokeLocalAsync(localHandler, safeInput, callbackCancellation.Token);
            Task timeoutTask = Task.Delay(_timeout, timeoutCancellation.Token);
            Task winner = localTask is null
                ? await Task.WhenAny(pending.Completion.Task, timeoutTask).ConfigureAwait(false)
                : await Task.WhenAny(pending.Completion.Task, localTask, timeoutTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (winner == pending.Completion.Task)
                return await pending.Completion.Task.ConfigureAwait(false);
            if (winner == localTask)
                return await localTask!.ConfigureAwait(false);
            return null;
        }
        finally
        {
            callbackCancellation.Cancel();
            timeoutCancellation.Cancel();
            RemovePending(pending);
        }
    }

    /// <summary>Consumes answer commands, including malformed, stale, and wrongly scoped commands.</summary>
    public bool TryHandle(ChatRequest incoming, string originId)
    {
        if (incoming is null) return false;
        var command = ChatControlCommand.Parse(incoming.Text);
        if (command.Kind is not (ChatControlCommandKind.Answer or ChatControlCommandKind.ReservedInvalid))
            return false;
        if (command.Kind == ChatControlCommandKind.ReservedInvalid || string.IsNullOrWhiteSpace(originId))
            return true;

        PendingInput? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(command.Code!, out pending))
                return true;
        }

        bool authorized = IsAuthorized(pending.IsAuthorized, incoming);
        if (!authorized || !SameScope(pending, incoming, originId))
            return true;

        lock (_sync)
        {
            if (!_pending.TryGetValue(command.Code!, out var current) || !ReferenceEquals(current, pending))
                return true;
            int questionIndex = command.QuestionNumber!.Value - 1;
            if (questionIndex < 0 || questionIndex >= pending.Input.Questions.Count)
                return true;

            var question = pending.Input.Questions[questionIndex];
            if (pending.Answers.ContainsKey(question.Id) ||
                !TryParseAnswer(question, command.Answer!, out var answer))
                return true;

            pending.Answers.Add(question.Id, answer);
            if (pending.Answers.Count == pending.Input.Questions.Count)
            {
                var result = pending.Answers.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
                pending.Completion.TrySetResult(result);
            }
        }
        return true;
    }

    private async Task<IReadOnlyDictionary<string, string[]>?> RequestLocallyAsync(
        AppServerInput input,
        Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? localHandler,
        CancellationToken cancellationToken)
    {
        if (localHandler is null) return null;
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyDictionary<string, string[]>?> localTask = InvokeLocalAsync(localHandler, input, localCancellation.Token);
        Task timeoutTask = Task.Delay(_timeout, timeoutCancellation.Token);
        Task winner = await Task.WhenAny(localTask, timeoutTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        localCancellation.Cancel();
        timeoutCancellation.Cancel();
        return winner == localTask ? await localTask.ConfigureAwait(false) : null;
    }

    private string? FormatPrompt(AppServerInput input, string code)
    {
        try
        {
            // The formatter needs numbered question content but never needs raw App Server IDs.
            var publicInput = input with
            {
                ThreadId = string.Empty,
                TurnId = string.Empty,
                ItemId = string.Empty,
                Questions = input.Questions.Select((question, index) =>
                    question with { Id = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray()
            };
            string formatted = _formatRequest(publicInput, code);
            return !string.IsNullOrWhiteSpace(formatted) && formatted.Length <= 3000 ? formatted : null;
        }
        catch
        {
            return null;
        }
    }

    private void RemovePending(PendingInput pending)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(pending.Code, out var current) && ReferenceEquals(current, pending))
                _pending.Remove(pending.Code);
        }
    }

    private static async Task<IReadOnlyDictionary<string, string[]>?> InvokeLocalAsync(
        Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>> handler,
        AppServerInput input,
        CancellationToken cancellationToken)
    {
        try { return await handler(input, cancellationToken).ConfigureAwait(false); }
        catch { return null; }
    }

    private static bool IsAuthorized(Func<ChatRequest, bool> authorize, ChatRequest request)
    {
        try { return authorize(request); }
        catch { return false; }
    }

    private static bool SameScope(PendingInput pending, ChatRequest incoming, string originId) =>
        string.Equals(pending.Origin.Provider, incoming.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(pending.OriginId, originId, StringComparison.Ordinal) &&
        string.Equals(pending.Origin.ChannelId, incoming.ChannelId, StringComparison.Ordinal) &&
        string.Equals(pending.Origin.GuildId, incoming.GuildId, StringComparison.Ordinal) &&
        string.Equals(pending.Origin.ThreadId, incoming.ThreadId, StringComparison.Ordinal) &&
        string.Equals(pending.Origin.UserId, incoming.UserId, StringComparison.Ordinal);

    private static bool TrySnapshotInput(AppServerInput input, out AppServerInput snapshot)
    {
        snapshot = input;
        var questions = input.Questions;
        if (questions is null || questions.Count is < 1 or > MaximumQuestions)
            return false;
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        var copies = new AppServerQuestion[questions.Count];
        for (int index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            if (question is null || string.IsNullOrWhiteSpace(question.Id) || question.Id.Length > 256 ||
                !questionIds.Add(question.Id) || question.Options is null || question.Options.Count > 50 ||
                question.Question is null || question.Question.Length > 4096 ||
                question.Header is null || question.Header.Length > 256 ||
                question.Options.Any(option => option is null || option.Label is null || option.Label.Length > 256 ||
                    option.Description is null || option.Description.Length > 2048))
                return false;
            copies[index] = question with { Options = question.Options.ToArray() };
        }
        snapshot = input with { Questions = copies };
        return true;
    }

    private static bool TryParseAnswer(AppServerQuestion question, string submitted, out string[] answer)
    {
        answer = [];
        var value = submitted.Trim();
        if (value.Length == 0 || value.Length > MaximumAnswerLength)
            return false;

        if (question.IsOther && value.StartsWith("other ", StringComparison.OrdinalIgnoreCase))
        {
            var freeText = value[6..].Trim();
            if (freeText.Length == 0) return false;
            answer = [freeText];
            return true;
        }

        if (question.Options.Count == 0)
        {
            answer = [value];
            return true;
        }

        string[] indexes = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (indexes.Length == 0 || indexes.Length > question.Options.Count)
            return false;
        var selected = new HashSet<int>();
        var values = new List<string>(indexes.Length);
        foreach (var indexText in indexes)
        {
            if (!int.TryParse(indexText, out int index) || index is < 1 || index > question.Options.Count || !selected.Add(index))
                return false;
            values.Add(question.Options[index - 1].Label);
        }
        answer = values.ToArray();
        return true;
    }

    private sealed class PendingInput(
        ChatRequest origin,
        string originId,
        AppServerInput input,
        Func<ChatRequest, bool> isAuthorized)
    {
        public ChatRequest Origin { get; } = origin;
        public string OriginId { get; } = originId;
        public AppServerInput Input { get; } = input;
        public Func<ChatRequest, bool> IsAuthorized { get; } = isAuthorized;
        public string Code { get; set; } = string.Empty;
        public Dictionary<string, string[]> Answers { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<IReadOnlyDictionary<string, string[]>?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
