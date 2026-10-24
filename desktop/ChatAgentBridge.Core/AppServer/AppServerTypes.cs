using System.Text.Json;

namespace ChatAgentBridge.Core.AppServer;

public sealed record AppServerApproval(
    string Method,
    string ThreadId,
    string TurnId,
    string ItemId,
    string Description,
    string? Command,
    string? WorkingDirectory);

public enum AppServerDecision { Accept, Decline }

public sealed record AppServerInputOption(string Label, string Description);

public sealed record AppServerQuestion(
    string Id,
    string Header,
    string Question,
    bool IsOther,
    bool IsSecret,
    IReadOnlyList<AppServerInputOption> Options);

public sealed record AppServerInput(
    string ThreadId,
    string TurnId,
    string ItemId,
    IReadOnlyList<AppServerQuestion> Questions);

public sealed record AppServerRunOptions(
    string? SessionId = null,
    Func<AppServerApproval, CancellationToken, Task<AppServerDecision>>? ApprovalHandler = null,
    Func<AppServerInput, CancellationToken, Task<IReadOnlyDictionary<string, string[]>?>>? InputHandler = null,
    IProgress<AgentRunProgress>? Progress = null,
    Action<IActiveAgentTurn?>? ActiveTurnChanged = null);

public interface IActiveAgentTurn
{
    Task<AgentTurnInputResult> AddInputAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>A live turn that can confirm message delivery after its input has been submitted.</summary>
public interface IQueuedActiveAgentTurn : IActiveAgentTurn
{
    /// <summary>Returns null only when no user input was written; otherwise confirmation may arrive later.</summary>
    Task<AgentTurnInputSubmission?> SubmitInputAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record AgentTurnInputSubmission(Task<AgentTurnInputResult> Confirmation);

public enum AgentTurnInputResult { Accepted, NotAccepted, DeliveryUnknown }

/// <param name="PromptAccepted">The server confirmed a turn for the prompt, rather than only creating an empty thread.</param>
public sealed record AppServerRunResult(AgentRunResult Result, string? SessionId, bool PromptAccepted = false);

public interface IAppServerRunner
{
    Task<AppServerRunResult> RunAsync(BridgeSettings settings, string prompt, AppServerRunOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record AppServerRpcError(int Code, string Message, object? Data = null);

public sealed class AppServerRpcException(int code, string message) : InvalidOperationException($"Codex App Server error {code}: {message}")
{
    public int Code { get; } = code;
    public string ServerMessage { get; } = message;
}

public sealed record AppServerServerRequest(JsonElement Id, string Method, JsonElement Params);

public sealed record AppServerServerResponse(object? Result = null, AppServerRpcError? Error = null)
{
    public static AppServerServerResponse FromResult(object? result) => new(result);
    public static AppServerServerResponse FromError(int code, string message) => new(null, new(code, message));
}

public interface IAppServerRpcClient : IAsyncDisposable
{
    event Action<string, JsonElement>? NotificationReceived;
    Func<AppServerServerRequest, CancellationToken, Task<AppServerServerResponse>>? ServerRequestHandler { get; set; }
    Task<Exception?> Closed { get; }
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);
    Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
}

public interface IAppServerProcess : IAsyncDisposable
{
    /// <summary>Native worker identity for attachment crash recovery; synthetic processes may omit it.</summary>
    System.Diagnostics.Process? NativeProcess => null;
    IAppServerRpcClient Client { get; }
    Task<int?> ExitCode { get; }
    Task<string> StandardError { get; }
    void KillTree();
}

/// <summary>Optional live process diagnostics without changing existing process fakes.</summary>
public interface IAppServerProgressSource
{
    event Action<string>? StandardErrorReceived;
}

public interface IAppServerProcessFactory
{
    Task<IAppServerProcess> StartAsync(string executable, string workspace, CancellationToken cancellationToken = default);
}
