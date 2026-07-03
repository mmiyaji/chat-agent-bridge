export type ChatProvider = "slack" | "mattermost";

export type AgentKind = "codex" | "claude-code" | "cursor" | "gemini";

export type ApprovalPolicy = "manual" | "on-failure" | "never";

export type NetworkAccess = "enabled" | "disabled";

export type ChatIngress = "slack" | "mattermost";

export type SlackMode = "http" | "socket";

export type MattermostMode = "http" | "websocket";
export type MattermostActorMode = "bot" | "self";

export type JsonPrimitive = string | number | boolean | null;

export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };

export interface ChatThreadRef {
  provider: ChatProvider;
  channelId: string;
  threadId: string;
}

export interface SessionBinding {
  sessionId: string;
  thread: ChatThreadRef;
  agent: AgentKind;
  workingDirectory: string;
  createdAt: string;
  updatedAt: string;
  agentState?: Record<string, JsonValue>;
  bridgeState?: Record<string, JsonValue>;
}

export type ExecutionStatus =
  | "running"
  | "completed"
  | "failed"
  | "waiting_for_approval"
  | "cancelled"
  | "rejected";

export interface ExecutionRecord {
  executionId: string;
  sessionId: string;
  thread: ChatThreadRef;
  agent: AgentKind;
  prompt: string;
  workingDirectory: string;
  approvalPolicy: ApprovalPolicy;
  networkAccess: NetworkAccess;
  status: ExecutionStatus;
  summary?: string;
  error?: string;
  createdAt: string;
  updatedAt: string;
  completedAt?: string;
  rerunOfExecutionId?: string;
}

export interface IncomingChatMessage {
  thread: ChatThreadRef;
  userId: string;
  text: string;
  requestedWorkingDirectory: string;
}

export interface AgentExecutionRequest {
  session: SessionBinding;
  prompt: string;
  approvalPolicy: ApprovalPolicy;
  networkAccess: NetworkAccess;
  allowedDirectories: string[];
  onActivity?: (update?: AgentProgressUpdate) => void | Promise<void>;
}

export interface AgentProgressUpdate {
  stream: "stdout" | "stderr";
  text: string;
  urgent?: boolean;
}

export interface AgentCapabilities {
  manualApproval: boolean;
  writeApprovalRerun: boolean;
  networkAccessControl: boolean;
}

export interface AgentExecutionResult {
  sessionId: string;
  summary: string;
  rawOutput?: string;
  agentState?: Record<string, JsonValue>;
}

export interface AppConfig {
  chatProvider: ChatIngress;
  agentKind: AgentKind;
  allowedDirectories: string[];
  discoverRepos?: boolean;
  repoAliases: Record<string, string>;
  slackAllowedUserIds: string[];
  mattermostAllowedUserIds: string[];
  defaultApprovalPolicy: ApprovalPolicy;
  networkAccess: NetworkAccess;
  slackMode: SlackMode;
  mattermostMode: MattermostMode;
  mattermostActorMode?: MattermostActorMode;
  port: number;
  host: string;
  adminPort?: number;
  adminHost?: string;
  defaultWorkingDirectory: string;
  slackBotToken?: string;
  slackAppToken?: string;
  slackSigningSecret?: string;
  mattermostServerUrl?: string;
  mattermostBotToken?: string;
  mattermostWebhookToken?: string;
  mattermostActionBaseUrl?: string;
  mattermostDmOnly?: boolean;
  settingsFile: string;
  stateFile: string;
  auditLogFile: string;
  codexSessionIndexFile: string;
  claudeSessionIndexFile: string;
  claudeProjectsDir: string;
  codexModel: string;
  codexMaxTurns: number;
  codexCommandTimeoutMs: number;
  codexMaxRuntimeMs: number;
  codexMaxOutputLength: number;
  cursorCommand?: string;
  cursorModel?: string;
  cursorCommandTimeoutMs?: number;
  cursorMaxRuntimeMs?: number;
  geminiCommand?: string;
  geminiModel?: string;
  geminiCommandTimeoutMs?: number;
  geminiMaxRuntimeMs?: number;
  claudeModel: string;
  claudeMaxTurns: number;
  claudeCommandTimeoutMs: number;
  claudeMaxRuntimeMs: number;
  autoSplitThreadUserMessageCount: number;
  agentRetryCount: number;
}

export interface StateSnapshot {
  sessions: SessionBinding[];
  executionHistory: ExecutionRecord[];
}
