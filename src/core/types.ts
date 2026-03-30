export type ChatProvider = "slack" | "mattermost";

export type AgentKind = "codex" | "claude-code" | "cursor";

export type ApprovalPolicy = "manual" | "on-failure" | "never";

export type NetworkAccess = "enabled" | "disabled";

export type ChatIngress = "slack" | "mattermost";

export type SlackMode = "http" | "socket";

export type MattermostMode = "http" | "websocket";

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
  repoAliases: Record<string, string>;
  defaultApprovalPolicy: ApprovalPolicy;
  networkAccess: NetworkAccess;
  slackMode: SlackMode;
  mattermostMode: MattermostMode;
  port: number;
  host: string;
  defaultWorkingDirectory: string;
  slackBotToken?: string;
  slackAppToken?: string;
  slackSigningSecret?: string;
  mattermostServerUrl?: string;
  mattermostBotToken?: string;
  mattermostWebhookToken?: string;
  mattermostDmOnly?: boolean;
  stateFile: string;
  auditLogFile: string;
  codexSessionIndexFile: string;
  claudeSessionIndexFile: string;
  codexModel: string;
  codexMaxTurns: number;
  codexCommandTimeoutMs: number;
  codexMaxRuntimeMs: number;
  codexMaxOutputLength: number;
  claudeModel: string;
  claudeMaxTurns: number;
  claudeCommandTimeoutMs: number;
  claudeMaxRuntimeMs: number;
}

export interface StateSnapshot {
  sessions: SessionBinding[];
  executionHistory: ExecutionRecord[];
}
