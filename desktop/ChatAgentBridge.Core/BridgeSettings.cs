namespace ChatAgentBridge.Core;

public sealed record BridgeSettings
{
    public string WorkspaceRoot { get; init; } = "";
    public string AgentExecutable { get; init; } = "codex";
    public string AgentArguments { get; init; } = "exec --json -";
    public string AgentMode { get; init; } = "exec";
    public string AgentProvider { get; init; } = AgentProfile.Codex;
    public string? AgentModel { get; init; }
    public string AutoConnectProvider { get; init; } = "none";
    public string MattermostServerUrl { get; init; } = "";
    public string MattermostChannelId { get; init; } = "";
    public string MattermostToken { get; init; } = "";
    public string SlackBotToken { get; init; } = "";
    public string SlackAppToken { get; init; } = "";
    public bool RequireApproval { get; init; } = true;
}
