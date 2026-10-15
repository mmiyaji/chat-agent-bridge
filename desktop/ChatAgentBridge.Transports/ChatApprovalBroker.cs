using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Transports;

/// <summary>Races a local approval dialog against a scoped, one-time chat reply.</summary>
public sealed class ChatApprovalBroker
{
    private const int MaximumPending = 20;
    private static readonly Regex ControlPattern = new(
        @"^(?:!cab\s+)?(?<decision>approve|reject)\s+(?<code>[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _timeout;

    public ChatApprovalBroker(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<AppServerDecision> RequestAsync(
        IChatTransport transport,
        ChatRequest origin,
        AppServerApproval approval,
        Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? localHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        var pending = new PendingApproval(origin);
        lock (_sync)
        {
            if (_pending.Count >= MaximumPending)
                return AppServerDecision.Decline;

            do
            {
                pending.Code = RandomNumberGenerator.GetHexString(12);
            } while (_pending.ContainsKey(pending.Code));
            _pending.Add(pending.Code, pending);
        }

        using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await transport.ReplyAsync(origin, FormatRequest(approval, pending.Code), cancellationToken).ConfigureAwait(false);
            Task<AppServerDecision>? localTask = localHandler is null
                ? null
                : InvokeLocalAsync(localHandler, approval, callbackCancellation.Token);
            Task timeoutTask = Task.Delay(_timeout, timeoutCancellation.Token);
            Task winner = localTask is null
                ? await Task.WhenAny(pending.Completion.Task, timeoutTask).ConfigureAwait(false)
                : await Task.WhenAny(pending.Completion.Task, localTask, timeoutTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (winner == pending.Completion.Task)
                return await pending.Completion.Task.ConfigureAwait(false);

            if (winner == localTask)
                return await localTask!.ConfigureAwait(false);

            return AppServerDecision.Decline;
        }
        finally
        {
            callbackCancellation.Cancel();
            timeoutCancellation.Cancel();
            lock (_sync)
            {
                if (_pending.TryGetValue(pending.Code, out PendingApproval? current) && ReferenceEquals(current, pending))
                    _pending.Remove(pending.Code);
            }
        }
    }

    /// <summary>Consumes well-formed control messages, including stale or wrongly scoped ones.</summary>
    public bool TryHandle(ChatRequest incoming)
    {
        if (incoming is null) return false;
        Match match = ControlPattern.Match(incoming.Text?.Trim() ?? string.Empty);
        if (!match.Success) return false;

        string code = match.Groups["code"].Value;
        lock (_sync)
        {
            if (_pending.TryGetValue(code, out PendingApproval? pending) && SameScope(pending.Origin, incoming))
            {
                AppServerDecision decision = match.Groups["decision"].Value.Equals("approve", StringComparison.OrdinalIgnoreCase)
                    ? AppServerDecision.Accept
                    : AppServerDecision.Decline;
                pending.Completion.TrySetResult(decision);
            }
        }
        return true;
    }

    private static async Task<AppServerDecision> InvokeLocalAsync(
        Func<AppServerApproval, CancellationToken, Task<AppServerDecision>> handler,
        AppServerApproval approval,
        CancellationToken cancellationToken)
    {
        try
        {
            return await handler(approval, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return AppServerDecision.Decline;
        }
    }

    private static bool SameScope(ChatRequest origin, ChatRequest incoming) =>
        string.Equals(origin.Provider, incoming.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(origin.ChannelId, incoming.ChannelId, StringComparison.Ordinal) &&
        string.Equals(origin.ThreadId, incoming.ThreadId, StringComparison.Ordinal) &&
        string.Equals(origin.UserId, incoming.UserId, StringComparison.Ordinal);

    private static string FormatRequest(AppServerApproval approval, string code)
    {
        string footer = $"\n許可: !cab approve {code}\n拒否: !cab reject {code}";
        var builder = new StringBuilder();
        builder.AppendLine("Codex が操作の許可を求めています。");
        int remaining = 3000 - builder.Length - footer.Length;
        Append("メソッド", approval.Method);
        Append("説明", approval.Description);
        Append("コマンド", approval.Command);
        Append("作業フォルダー", approval.WorkingDirectory);
        builder.Append(footer);
        return builder.ToString();

        void Append(string label, string? value)
        {
            if (remaining <= 0 || string.IsNullOrWhiteSpace(value)) return;
            string prefix = $"{label}: ";
            int allowance = remaining - prefix.Length - 1;
            if (allowance <= 0) return;
            string content = value.Length <= allowance ? value : value[..Math.Max(0, allowance - 1)] + "…";
            string line = prefix + content + "\n";
            builder.Append(line);
            remaining -= line.Length;
        }
    }

    private sealed class PendingApproval(ChatRequest origin)
    {
        public ChatRequest Origin { get; } = origin;
        public string Code { get; set; } = string.Empty;
        public TaskCompletionSource<AppServerDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
