namespace ChatAgentBridge.Core.Sessions;

public sealed record SessionKey(string Provider, string ChannelId, string ThreadId);

public sealed record SessionBinding(
    SessionKey Key,
    string WorkspaceRoot,
    string AgentExecutable,
    string AgentArguments,
    string SessionId,
    DateTimeOffset UpdatedAt);
