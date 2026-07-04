import { Agent, AgentExecutionCancelledError, AgentRegistry } from "../agents/base.js";
import { ChatAction, ChatAdapter, ChatConnectionStatus } from "../adapters/chat.js";
import { readLatestCodexSession, readRecentCodexSessions } from "../agents/codex/session-index.js";
import { readLatestClaudeSession, readRecentClaudeSessions } from "../agents/claude/session-index.js";
import { readClaudeProjectSessions } from "../agents/claude/project-sessions.js";
import crypto from "node:crypto";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { StateStore } from "../state/state-store.js";
import {
  isApprovalMessage,
  isRejectionMessage,
  readPendingApproval,
  shouldRequestApproval,
} from "./approval.js";
import { AuditLogger } from "./audit.js";
import { logger } from "./logger.js";
import {
  BridgeCommand,
  formatRepoAliases,
  parseBridgeCommand,
  resolveRequestedWorkingDirectory,
} from "./commands.js";
import { JobQueue } from "./job-queue.js";
import { assertPathAllowed } from "./paths.js";
import { computeRepoAliases } from "../config/env.js";
import { updatePersistedSettings } from "../config/settings-io.js";
import { SessionManager } from "./session-manager.js";
import {
  AgentProgressUpdate,
  ApprovalPolicy,
  AppConfig,
  ExecutionRecord,
  ExecutionStatus,
  IncomingChatMessage,
  JsonValue,
  SessionBinding,
} from "./types.js";

interface BridgeServiceDeps {
  config: AppConfig;
  stateStore: StateStore;
  sessionManager: SessionManager;
  jobQueue: JobQueue;
  chatAdapter: ChatAdapter;
  agents: AgentRegistry;
  auditLogger: AuditLogger;
  launchDetachedRestart?: (input: {
    channelId?: string;
    threadId?: string;
    rebuild?: boolean;
    actor?: string;
    reason?: string;
  }) => void;
}

interface SessionPickerState {
  sessionIds: string[];
  pendingPrompt?: string;
}

interface WorkdirPickerState {
  aliases: string[];
  pendingPrompt?: string;
}

type SessionConnectionMode = "attached" | "bridge-owned";

interface StoredAgentSession {
  id: string;
  updatedAt: string;
  label?: string;
}

const EXECUTION_ENV_AUDIT_KEYS = [
  "HTTP_PROXY",
  "HTTPS_PROXY",
  "NO_PROXY",
  "NPM_CONFIG_PROXY",
  "NPM_CONFIG_HTTPS_PROXY",
  "NPM_CONFIG_REGISTRY",
  "NPM_CONFIG_USERCONFIG",
  "NPM_CONFIG_CACHE",
  "NODE_OPTIONS",
  "npm_config_proxy",
  "npm_config_https_proxy",
  "npm_config_registry",
  "npm_config_userconfig",
  "npm_config_cache",
  "PATH",
  "Path",
  "ComSpec",
  "SystemRoot",
] as const;

function readApprovalPolicyOverride(
  bridgeState: Record<string, JsonValue> | undefined,
): AppConfig["defaultApprovalPolicy"] | undefined {
  const value = bridgeState?.approvalPolicyOverride;
  if (value === "manual" || value === "never" || value === "on-failure") {
    return value;
  }

  return undefined;
}

function readSessionConnectionMode(session: SessionBinding): SessionConnectionMode {
  const value = session.bridgeState?.connectionMode;
  if (value === "attached" || value === "bridge-owned") {
    return value;
  }

  return readAgentSessionId(session) ? "attached" : "bridge-owned";
}

function readThreadUserMessageCount(
  bridgeState: Record<string, JsonValue> | undefined,
): number {
  const value = bridgeState?.threadUserMessageCount;
  return typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : 0;
}

function readAgentSessionId(session: SessionBinding): string | undefined {
  if (session.agent === "codex") {
    return typeof session.agentState?.codexThreadId === "string"
      ? session.agentState.codexThreadId
      : undefined;
  }

  if (session.agent === "claude-code") {
    return typeof session.agentState?.claudeSessionId === "string"
      ? session.agentState.claudeSessionId
      : undefined;
  }

  if (session.agent === "cursor") {
    return typeof session.agentState?.cursorSessionId === "string"
      ? session.agentState.cursorSessionId
      : undefined;
  }

  if (session.agent === "gemini") {
    return typeof session.agentState?.geminiSessionId === "string"
      ? session.agentState.geminiSessionId
      : undefined;
  }

  return undefined;
}

function createAttachedAgentState(
  session: SessionBinding,
  agentSessionId: string,
): Record<string, JsonValue> {
  if (session.agent === "codex") {
    return {
      ...(session.agentState ?? {}),
      codexThreadId: agentSessionId,
    };
  }

  if (session.agent === "claude-code") {
    return {
      ...(session.agentState ?? {}),
      claudeSessionId: agentSessionId,
    };
  }

  if (session.agent === "cursor") {
    return {
      ...(session.agentState ?? {}),
      cursorSessionId: agentSessionId,
    };
  }

  if (session.agent === "gemini") {
    return {
      ...(session.agentState ?? {}),
      geminiSessionId: agentSessionId,
    };
  }

  return {
    ...(session.agentState ?? {}),
  };
}

function readSessionPicker(
  bridgeState: Record<string, JsonValue> | undefined,
): SessionPickerState | undefined {
  const picker = bridgeState?.sessionPicker;
  if (!picker || typeof picker !== "object" || Array.isArray(picker)) {
    return undefined;
  }

  const sessionIds = picker.sessionIds;
  if (!Array.isArray(sessionIds) || sessionIds.some((id) => typeof id !== "string")) {
    return undefined;
  }

  const pendingPrompt = picker.pendingPrompt;
  return {
    sessionIds: sessionIds as string[],
    ...(typeof pendingPrompt === "string" && pendingPrompt.length > 0
      ? { pendingPrompt }
      : {}),
  };
}

function readWorkdirPicker(
  bridgeState: Record<string, JsonValue> | undefined,
): WorkdirPickerState | undefined {
  const picker = bridgeState?.workdirPicker;
  if (!picker || typeof picker !== "object" || Array.isArray(picker)) {
    return undefined;
  }

  const aliases = picker.aliases;
  if (!Array.isArray(aliases) || aliases.some((alias) => typeof alias !== "string")) {
    return undefined;
  }

  const pendingPrompt = picker.pendingPrompt;
  return {
    aliases: aliases as string[],
    ...(typeof pendingPrompt === "string" && pendingPrompt.length > 0
      ? { pendingPrompt }
      : {}),
  };
}

const PROGRESS_UPDATE_INTERVAL_MS = 20_000;
const MAX_PROGRESS_LINES = 4;
const RESTART_COMPLETION_FLUSH_INTERVAL_MS = 2_000;
const PROGRESS_COPY = {
  heading: {
    running: "Received your request for session",
    completed: "Completed work for session",
    failed: "Stopped work for session",
    cancelled: "Cancelled work for session",
    waitingForApproval: "Paused work for session",
  },
  state: {
    running: "Running",
    completed: "Completed",
    failed: "Failed",
    cancelled: "Cancelled",
    waitingForApproval: "Waiting for approval",
  },
  detail: {
    taskStarted: "Request received. Starting work.",
    stillRunning: "Work is still in progress.",
    completed: "Work finished. Final response posted separately.",
    cancelled: "Work was cancelled.",
    cancellationRequested: "Cancellation requested. Waiting for the process to stop.",
    writeApprovalRequired: "Waiting for write approval to continue.",
    approvalAcceptedRerunning: "Approval received. Re-running the request.",
    executionFailed: "Work stopped because of an error.",
  },
  recentActivityLabel: "Recent activity:",
} as const;

function formatExecutionStatus(status: ExecutionStatus): string {
  switch (status) {
    case "running":
      return "Running";
    case "completed":
      return "Completed";
    case "failed":
      return "Failed";
    case "cancelled":
      return "Cancelled";
    case "waiting_for_approval":
      return "Waiting for approval";
    case "rejected":
      return "Rejected";
    default:
      return status;
  }
}

function formatProgressStatus(status: string): string {
  switch (status) {
    case "running":
      return PROGRESS_COPY.state.running;
    case "completed":
      return PROGRESS_COPY.state.completed;
    case "failed":
      return PROGRESS_COPY.state.failed;
    case "cancelled":
      return PROGRESS_COPY.state.cancelled;
    case "waiting for approval":
      return PROGRESS_COPY.state.waitingForApproval;
    default:
      return status;
  }
}

function formatProgressHeading(session: SessionBinding, status: string): string {
  switch (status) {
    case "completed":
      return `${PROGRESS_COPY.heading.completed} \`${session.sessionId}\` in \`${session.workingDirectory}\`.`;
    case "failed":
      return `${PROGRESS_COPY.heading.failed} \`${session.sessionId}\` in \`${session.workingDirectory}\`.`;
    case "cancelled":
      return `${PROGRESS_COPY.heading.cancelled} \`${session.sessionId}\` in \`${session.workingDirectory}\`.`;
    case "waiting for approval":
      return `${PROGRESS_COPY.heading.waitingForApproval} \`${session.sessionId}\` in \`${session.workingDirectory}\`.`;
    default:
      return `${PROGRESS_COPY.heading.running} \`${session.sessionId}\` in \`${session.workingDirectory}\`.`;
  }
}

function formatProgressDetail(detail: string): string {
  switch (detail) {
    case "task started":
      return PROGRESS_COPY.detail.taskStarted;
    case "still running":
      return PROGRESS_COPY.detail.stillRunning;
    case "completed":
      return PROGRESS_COPY.detail.completed;
    case "cancelled":
      return PROGRESS_COPY.detail.cancelled;
    case "cancellation requested":
      return PROGRESS_COPY.detail.cancellationRequested;
    case "write approval required":
      return PROGRESS_COPY.detail.writeApprovalRequired;
    case "approval accepted; rerunning":
      return PROGRESS_COPY.detail.approvalAcceptedRerunning;
    case "execution failed":
      return PROGRESS_COPY.detail.executionFailed;
    default:
      return detail;
  }
}

function buildApprovalActions(): ChatAction[] {
  return [
    {
      id: "cab_approve",
      label: "Approve and rerun",
      value: "approve",
      style: "primary",
    },
    {
      id: "cab_reject",
      label: "Reject",
      value: "reject",
      style: "danger",
    },
  ];
}

function buildApprovalBlocks(): unknown[] {
  return [
    {
      type: "section",
      text: {
        type: "mrkdwn",
        text:
          "*Approval required*\nThis request needs write access. Approve to rerun, or reject to clear the pending request.",
      },
    },
    {
      type: "actions",
      elements: buildApprovalActions().map((action) => ({
        type: "button",
        action_id: action.id,
        text: {
          type: "plain_text",
          text: action.label,
          emoji: true,
        },
        style: action.style,
        value: action.value,
      })),
    },
  ];
}

function buildCancelActions(): ChatAction[] {
  return [
    {
      id: "cab_cancel",
      label: "Cancel",
      value: "cancel",
      style: "danger",
    },
  ];
}

function buildCancelBlocks(): unknown[] {
  return [
    {
      type: "actions",
      elements: buildCancelActions().map((action) => ({
        type: "button",
        action_id: action.id,
        text: {
          type: "plain_text",
          text: action.label,
          emoji: true,
        },
        style: action.style,
        value: action.value,
      })),
    },
  ];
}

function buildProgressBlocks(text: string, status: string): unknown[] {
  const blocks: unknown[] = [
    {
      type: "section",
      text: {
        type: "mrkdwn",
        text,
      },
    },
  ];

  if (status === "running") {
    blocks.push(...buildCancelBlocks());
  }

  return blocks;
}

function formatAgentDisplayName(agentKind: SessionBinding["agent"]): string {
  switch (agentKind) {
    case "claude-code":
      return "Claude Code";
    case "codex":
      return "Codex";
    case "cursor":
      return "Cursor";
    case "gemini":
      return "Gemini CLI";
    default:
      return agentKind;
  }
}

function supportsReusableSessions(agentKind: SessionBinding["agent"]): boolean {
  return agentKind === "codex" || agentKind === "claude-code";
}

function appendRestartLog(orchestratorLog: string, line: string): void {
  try {
    fs.appendFileSync(orchestratorLog, line, "utf8");
  } catch (error) {
    const errorCode =
      error instanceof Error && "code" in error ? String((error as NodeJS.ErrnoException).code) : "";
    if (errorCode === "EBUSY" || errorCode === "EPERM") {
      return;
    }

    throw error;
  }
}

function toPowerShellSingleQuoted(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

interface PendingRestartCompletion {
  channelId: string;
  threadId: string;
  /** Legacy field — used when the PS1 script pre-composed the message. */
  text?: string;
  /** ISO timestamp of when the restart was initiated. */
  startedAt?: string;
  /** Wall-clock duration of the restart in milliseconds. */
  durationMs?: number;
  /** True if the restart script itself failed. */
  failed?: boolean;
  /** Error message when failed === true. */
  errorMessage?: string;
  /** Number of times posting this entry has been attempted (transient failures). */
  attempts?: number;
}

/** Maximum number of transient post attempts before a pending entry is dropped. */
const MAX_RESTART_COMPLETION_ATTEMPTS = 30;

/**
 * True when a failed restart-completion post should never be retried because the
 * target thread/channel is permanently unreachable (e.g. deleted channel, bad
 * credentials, malformed request). Detection is by message pattern only so it
 * works across the Slack and Mattermost adapters:
 *   - Mattermost throws `Mattermost API HTTP error: <status>`
 *   - Slack throws `Slack API error: <code>`
 */
export function isPermanentChatPostError(message: string): boolean {
  if (typeof message !== "string" || message.length === 0) {
    return false;
  }

  const normalized = message.toLowerCase();
  const permanentSubstrings = [
    "channel_not_found",
    "is_archived",
    "not_in_channel",
    "channel_id",
    "invalid",
  ];
  if (permanentSubstrings.some((needle) => normalized.includes(needle))) {
    return true;
  }

  // Permanent HTTP status codes (client errors). Match on word boundaries so we
  // do not treat e.g. "5400" as a 400.
  return /\b(400|401|403|404)\b/.test(normalized);
}

export class BridgeService {
  private readonly startedAt = new Date().toISOString();
  private readonly restartCompletionFilePath =
    process.env.CAB_RESTART_COMPLETION_FILE ??
    path.resolve(process.cwd(), "data", "runtime", "restart-completion.jsonl");
  private restartCompletionFlushTimer?: NodeJS.Timeout;
  private restartCompletionFlushInFlight = false;

  constructor(private readonly deps: BridgeServiceDeps) {}

  async start(): Promise<void> {
    await this.deps.stateStore.initialize();
    await this.reconcileInterruptedExecutions();
    await this.deps.chatAdapter.start(async (message) => this.handleMessage(message));
    await this.flushPendingRestartCompletions();
    this.restartCompletionFlushTimer = setInterval(() => {
      void this.flushPendingRestartCompletions();
    }, RESTART_COMPLETION_FLUSH_INTERVAL_MS);
    this.restartCompletionFlushTimer.unref();
  }

  async dispatchAdminAction(
    sessionId: string,
    action: "cancel" | "approve" | "reject",
  ): Promise<{ accepted: boolean; reason?: string }> {
    return this.dispatchAdminCommand(sessionId, action);
  }

  async dispatchAdminCommand(
    sessionId: string,
    text: string,
  ): Promise<{ accepted: boolean; reason?: string }> {
    const session = await this.findSessionById(sessionId);
    if (!session) {
      return {
        accepted: false,
        reason: "session-not-found",
      };
    }

    let commandText = text;
    if (text.trim().toLowerCase() === "retry-last") {
      const latestRecord = (await this.deps.stateStore.listExecutionRecords({
        sessionId,
        limit: 1,
      }))[0];
      if (!latestRecord?.prompt) {
        return {
          accepted: false,
          reason: "no-execution-to-retry",
        };
      }
      commandText = latestRecord.prompt;
    }

    const message: IncomingChatMessage = {
      thread: session.thread,
      userId: "admin-ui",
      text: commandText,
      requestedWorkingDirectory: session.workingDirectory,
    };
    await this.deps.auditLogger.log({
      type: "admin.action.dispatched",
      sessionId,
      action: commandText,
    });
    queueMicrotask(() => {
      void this.handleMessage(message).catch((error: unknown) => {
        console.error("Admin action dispatch failed.", error);
      });
    });

    return {
      accepted: true,
    };
  }

  async requestAdminRestart(): Promise<void> {
    await this.deps.auditLogger.log({
      type: "admin.restart.requested",
    });
    this.scheduleDetachedRestart(undefined, undefined, {
      actor: "admin-ui",
      reason: "admin-request",
    });
  }

  private async reconcileInterruptedExecutions(): Promise<void> {
    const records = await this.deps.stateStore.listExecutionRecords();
    const interrupted = records.filter((record) => record.status === "running");
    if (interrupted.length === 0) {
      return;
    }

    const summary = "Execution was interrupted because the bridge restarted while work was in progress.";
    for (const record of interrupted) {
      await this.deps.stateStore.upsertExecutionRecord(
        this.completeExecutionRecord(record, "cancelled", summary),
      );
      await this.deps.auditLogger.log({
        type: "bridge.execution.recovered-as-cancelled",
        sessionId: record.sessionId,
        executionId: record.executionId,
      });
    }
  }

  getChatConnectionStatus(): ChatConnectionStatus | undefined {
    return this.deps.chatAdapter.getConnectionStatus?.();
  }

  /**
   * Read-only view of the environment a session actually runs in, resolved the
   * same way the execution path resolves it. Consumed by the admin UI so it can
   * answer "where and how does this session run" without reading code.
   */
  describeSessionEnvironment(session: SessionBinding): {
    workingDirectory: string;
    agent: SessionBinding["agent"];
    connectionMode: SessionConnectionMode;
    agentSessionId: string | null;
    approvalPolicy: ApprovalPolicy;
    workdirConfirmed: boolean;
    pendingWorkdirPicker: boolean;
    pendingSessionPicker: boolean;
  } {
    return {
      workingDirectory: session.workingDirectory,
      agent: session.agent,
      connectionMode: readSessionConnectionMode(session),
      agentSessionId: readAgentSessionId(session) ?? null,
      approvalPolicy: this.resolveApprovalPolicy(session),
      workdirConfirmed: session.bridgeState?.workdirConfirmed === true,
      pendingWorkdirPicker: Boolean(readWorkdirPicker(session.bridgeState)),
      pendingSessionPicker: Boolean(readSessionPicker(session.bridgeState)),
    };
  }

  /**
   * Static, non-secret description of the bridge process environment exposed to
   * agents. Returns variable NAMES only — never values — so the admin UI never
   * surfaces tokens or secrets.
   */
  getAgentEnvironmentVariableNames(): string[] {
    return [
      "CHAT_AGENT_BRIDGE",
      "CHAT_AGENT_BRIDGE_SESSION_ID",
      "CHAT_AGENT_BRIDGE_AGENT",
      "CHAT_AGENT_BRIDGE_CHAT_PROVIDER",
      "CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID",
      "CHAT_AGENT_BRIDGE_CHAT_THREAD_ID",
      "CHAT_AGENT_BRIDGE_WORKDIR",
    ];
  }

  private resolveAgent(kind: SessionBinding["agent"]): Agent {
    const agent = this.deps.agents[kind];
    if (!agent) {
      throw new Error(`Agent \`${kind}\` is not configured in this bridge instance.`);
    }

    return agent;
  }

  private resolveSessionAgent(session: SessionBinding): Agent {
    return this.resolveAgent(session.agent);
  }

  private async findSessionById(sessionId: string): Promise<SessionBinding | undefined> {
    const sessions = await this.deps.stateStore.listSessions();
    return sessions.find((entry) => entry.sessionId === sessionId);
  }

  private buildSessionBridgeState(
    session: SessionBinding,
    overrides?: Record<string, JsonValue>,
  ): Record<string, JsonValue> {
    const nextBridgeState: Record<string, JsonValue> = {
      connectionMode: readSessionConnectionMode(session),
      threadUserMessageCount: readThreadUserMessageCount(session.bridgeState),
    };
    const approvalPolicyOverride = readApprovalPolicyOverride(session.bridgeState);
    if (approvalPolicyOverride) {
      nextBridgeState.approvalPolicyOverride = approvalPolicyOverride;
    }
    if (session.bridgeState?.resumeOffered === true) {
      nextBridgeState.resumeOffered = true;
    }
    if (session.bridgeState?.workdirConfirmed === true) {
      nextBridgeState.workdirConfirmed = true;
    }
    return {
      ...nextBridgeState,
      ...(overrides ?? {}),
    };
  }

  private async maybeRotateThreadBeforeExecution(
    session: SessionBinding,
    incomingThread: IncomingChatMessage["thread"],
  ): Promise<{ session: SessionBinding; thread: IncomingChatMessage["thread"] }> {
    const threshold = this.deps.config.autoSplitThreadUserMessageCount;
    if (threshold < 1) {
      return { session, thread: incomingThread };
    }

    const currentCount = readThreadUserMessageCount(session.bridgeState);
    if (currentCount < threshold) {
      return { session, thread: incomingThread };
    }

    const newThread = await this.deps.chatAdapter.startNewThread(
      incomingThread.channelId,
      [
        `Auto-split continuation for session \`${session.sessionId}\`.`,
        `Agent: \`${session.agent}\``,
        `Working directory: \`${session.workingDirectory}\``,
        `The previous thread reached the user-message limit of \`${threshold}\`.`,
        "Reply in this thread to continue.",
      ].join("\n"),
    );
    if (!newThread) {
      return { session, thread: incomingThread };
    }

    const nextSession: SessionBinding = {
      ...session,
      thread: newThread,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session, {
        threadUserMessageCount: 0,
      }),
    };
    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.thread.auto-split",
      sessionId: session.sessionId,
      fromThreadId: incomingThread.threadId,
      toThreadId: newThread.threadId,
      threshold,
    });
    await this.deps.chatAdapter.postThreadMessage(
      incomingThread,
      [
        `Session \`${session.sessionId}\` moved to a new thread automatically after \`${threshold}\` user messages.`,
        "Continue in the newly created thread. This message will be handled there.",
      ].join("\n"),
    );

    return {
      session: nextSession,
      thread: newThread,
    };
  }

  private async incrementThreadUserMessageCount(session: SessionBinding): Promise<SessionBinding> {
    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session, {
        threadUserMessageCount: readThreadUserMessageCount(session.bridgeState) + 1,
      }),
    };
    await this.deps.stateStore.upsertSession(nextSession);
    return nextSession;
  }

  private async resolveNewSessionWorkingDirectory(
    message: IncomingChatMessage,
  ): Promise<string> {
    const channelDefault = await this.deps.stateStore.getChannelDefaultWorkingDirectory(
      message.thread.provider,
      message.thread.channelId,
    );
    if (!channelDefault) {
      return message.requestedWorkingDirectory;
    }

    try {
      // A config change may have narrowed the allowed roots since the default
      // was persisted; if so, ignore it rather than start outside the sandbox.
      return assertPathAllowed(channelDefault, this.deps.config.allowedDirectories);
    } catch (error) {
      await this.deps.auditLogger.log({
        type: "bridge.channel-default.invalid",
        provider: message.thread.provider,
        channelId: message.thread.channelId,
        workingDirectory: channelDefault,
        error: error instanceof Error ? error.message : String(error),
      });
      return message.requestedWorkingDirectory;
    }
  }

  private async handleMessage(message: IncomingChatMessage): Promise<void> {
    if (!(await this.isMessageUserAllowed(message))) {
      await this.deps.auditLogger.log({
        type: "bridge.message.rejected",
        provider: message.thread.provider,
        channelId: message.thread.channelId,
        threadId: message.thread.threadId,
        userId: message.userId,
        reason: "user-not-allowed",
      });
      return;
    }

    const existingSession = await this.deps.sessionManager.getByThread(message.thread);
    const newSessionWorkingDirectory = existingSession
      ? existingSession.workingDirectory
      : await this.resolveNewSessionWorkingDirectory(message);
    const session = await this.deps.sessionManager.getOrCreate({
      agent: existingSession?.agent ?? this.deps.config.agentKind,
      thread: message.thread,
      workingDirectory: newSessionWorkingDirectory,
      allowedDirectories: this.deps.config.allowedDirectories,
    });
    const immediateBridgeCommand = parseBridgeCommand(message.text);
    if (immediateBridgeCommand?.kind === "cancel") {
      await this.handleImmediateBridgeCommand(session, message, immediateBridgeCommand);
      return;
    }

    await this.deps.jobQueue.enqueue(session.sessionId, async () => {
      let currentSession = session;
      let responseThread = message.thread;
      currentSession = await this.clearPendingApprovalIfBypassed(currentSession);

      await this.deps.auditLogger.log({
        type: "bridge.message.received",
        sessionId: currentSession.sessionId,
        provider: message.thread.provider,
        channelId: message.thread.channelId,
        threadId: message.thread.threadId,
        userId: message.userId,
        text: message.text,
        workingDirectory: currentSession.workingDirectory,
      });

      const trimmedText = message.text.trim();
      // workdirPicker replies (a number or `keep`) must route before the
      // sessionPicker check and before bridge-command parsing, since `keep`
      // and bare numbers are not bridge commands.
      const looksLikeWorkdirReply =
        /^\d+$/.test(trimmedText) || /^keep$/i.test(trimmedText);
      if (looksLikeWorkdirReply && readWorkdirPicker(currentSession.bridgeState)) {
        const handled = await this.handleWorkdirPickerSelection(currentSession, message);
        if (handled) {
          return;
        }
      }

      const looksLikePickerReply =
        /^\d+$/.test(trimmedText) || /^(new|start)$/i.test(trimmedText);
      if (looksLikePickerReply && readSessionPicker(currentSession.bridgeState)) {
        const handled = await this.handleSessionPickerSelection(currentSession, message);
        if (handled) {
          return;
        }
      }

      const bridgeCommand = immediateBridgeCommand ?? parseBridgeCommand(message.text);
      if (bridgeCommand) {
        await this.handleBridgeCommand(currentSession, message, bridgeCommand);
        return;
      }

      if (message.text.trim().length === 0) {
        await this.handleEmptyMention(currentSession, message);
        return;
      }

      if (isApprovalMessage(message.text)) {
        await this.handleApprovalMessage(currentSession, message);
        return;
      }

      if (isRejectionMessage(message.text)) {
        await this.handleRejectionMessage(currentSession, message);
        return;
      }

      const workdirResult = await this.maybeOfferWorkdir(currentSession, message);
      if (workdirResult.offered) {
        return;
      }
      currentSession = workdirResult.session;

      // maybeOfferWorkdir may strip an inline directory token from the first
      // mention, leaving only the remaining instruction to execute.
      const promptText = workdirResult.text;

      const resumeResult = await this.maybeOfferResume(currentSession, {
        ...message,
        text: promptText,
      });
      if (resumeResult.offered) {
        return;
      }
      currentSession = resumeResult.session;

      const rotated = await this.maybeRotateThreadBeforeExecution(currentSession, message.thread);
      currentSession = rotated.session;
      responseThread = rotated.thread;
      currentSession = await this.incrementThreadUserMessageCount(currentSession);

      let progress: Awaited<ReturnType<BridgeService["createProgressReporter"]>> | undefined;
      let executionRecord: ExecutionRecord | undefined;
      try {
        const sessionAgent = this.resolveSessionAgent(currentSession);
        const approvalPolicy = this.resolveApprovalPolicy(currentSession);
        executionRecord = this.createExecutionRecord({
          session: currentSession,
          prompt: promptText,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
        });
        await this.deps.stateStore.upsertExecutionRecord(executionRecord);
        progress = await this.createProgressReporter(
          currentSession,
          responseThread,
          "running",
          "task started",
        );
        await this.deps.auditLogger.log({
          type: "bridge.execution.started",
          sessionId: currentSession.sessionId,
          workingDirectory: currentSession.workingDirectory,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
          prompt: promptText,
        });
        await this.logExecutionEnvironment(currentSession, approvalPolicy);

        const result = await sessionAgent.execute({
          session: currentSession,
          prompt: promptText,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
          allowedDirectories: this.deps.config.allowedDirectories,
          onActivity: progress.onActivity,
        });

        const persistInput: Parameters<BridgeService["persistSessionAfterRun"]>[1] = {
          prompt: promptText,
          resultSummary: result.summary,
        };

        if (result.agentState) {
          persistInput.agentState = result.agentState;
        }

        const needsApproval =
          approvalPolicy !== "never" &&
          sessionAgent.capabilities.writeApprovalRerun &&
          shouldRequestApproval(result.summary);
        if (executionRecord) {
          executionRecord = this.completeExecutionRecord(
            executionRecord,
            needsApproval ? "waiting_for_approval" : "completed",
            result.summary,
          );
          await this.deps.stateStore.upsertExecutionRecord(executionRecord);
        }
        persistInput.allowPendingApproval = approvalPolicy !== "never";
        persistInput.pendingApprovalExecutionId = executionRecord?.executionId;
        if (currentSession.bridgeState?.retryCount) {
          currentSession = {
            ...currentSession,
            bridgeState: { ...currentSession.bridgeState, retryCount: 0 },
          };
        }
        currentSession = await this.persistSessionAfterRun(currentSession, persistInput);
        await progress.finish(
          needsApproval ? "waiting for approval" : "completed",
          needsApproval ? "write approval required" : "completed",
        );
        await this.deps.chatAdapter.postThreadMessage(responseThread, result.summary);
        await this.deps.auditLogger.log({
          type: "bridge.execution.completed",
          sessionId: currentSession.sessionId,
          workingDirectory: currentSession.workingDirectory,
          summary: result.summary,
        });

        if (needsApproval) {
          await this.deps.auditLogger.log({
            type: "bridge.approval.requested",
            sessionId: currentSession.sessionId,
            prompt: promptText,
            reason: result.summary,
          });
        await this.deps.chatAdapter.postThreadMessage(
            responseThread,
            [
              "Approval required.",
              `Pending request: \`${this.truncateForCode(promptText, 120)}\``,
              "Reply `@Chat Agent Bridge approve` to rerun it with write access, or `@Chat Agent Bridge reject` to clear it.",
            ].join("\n"),
            {
              blocks: buildApprovalBlocks(),
              actions: buildApprovalActions(),
            },
          );
        }
      } catch (error) {
        if (error instanceof AgentExecutionCancelledError) {
          await progress?.finish("cancelled", "cancelled").catch(() => undefined);
          if (executionRecord) {
            await this.deps.stateStore.upsertExecutionRecord(
              this.completeExecutionRecord(executionRecord, "cancelled", error.message),
            );
          }
          await this.deps.auditLogger.log({
            type: "bridge.execution.cancelled",
            sessionId: currentSession.sessionId,
            workingDirectory: currentSession.workingDirectory,
          });
          await this.deps.chatAdapter.postThreadMessage(
            responseThread,
            `Execution cancelled for session \`${currentSession.sessionId}\`.`,
          );
          return;
        }

        const text = error instanceof Error ? error.message : "Unknown bridge error.";
        await progress?.finish("failed", "execution failed").catch(() => undefined);
        if (executionRecord) {
          await this.deps.stateStore.upsertExecutionRecord(
            this.failExecutionRecord(executionRecord, text),
          );
        }
        await this.deps.auditLogger.log({
          type: "bridge.execution.failed",
          sessionId: currentSession.sessionId,
          workingDirectory: currentSession.workingDirectory,
          error: text,
        });

        const retryCount = Number(currentSession.bridgeState?.retryCount ?? 0);
        const maxRetries = this.deps.config.agentRetryCount;
        if (maxRetries > 0 && retryCount < maxRetries) {
          const nextRetry = retryCount + 1;
          currentSession = {
            ...currentSession,
            bridgeState: {
              ...currentSession.bridgeState,
              retryCount: nextRetry,
            },
            updatedAt: new Date().toISOString(),
          };
          await this.deps.stateStore.upsertSession(currentSession);
          await this.deps.auditLogger.log({
            type: "bridge.execution.retry",
            sessionId: currentSession.sessionId,
            attempt: nextRetry,
            maxRetries,
          });
          await this.deps.chatAdapter.postThreadMessage(
            responseThread,
            `Execution failed (attempt ${nextRetry}/${maxRetries}). Retrying...`,
          );
          await this.handleMessage({ ...message, text: promptText });
          return;
        }

        await this.deps.chatAdapter.postThreadMessage(
          responseThread,
          `Execution failed for session \`${currentSession.sessionId}\`: ${text}`,
        );
      }
    });
  }

  private async isMessageUserAllowed(message: IncomingChatMessage): Promise<boolean> {
    if (message.userId === "admin-ui") {
      return true;
    }

    if (message.thread.provider === "slack") {
      if (this.deps.config.slackAllowedUserIds.length === 0) {
        return true;
      }

      return this.deps.config.slackAllowedUserIds.includes(message.userId);
    }

    if (message.thread.provider === "mattermost") {
      if (this.deps.config.mattermostActorMode !== "bot") {
        return true;
      }

      if (this.deps.config.mattermostAllowedUserIds.length === 0) {
        return true;
      }

      return this.deps.config.mattermostAllowedUserIds.includes(message.userId);
    }

    return true;
  }

  private async handleBridgeCommand(
    session: SessionBinding,
    message: IncomingChatMessage,
    command: BridgeCommand,
  ): Promise<void> {
    if (command.kind === "help") {
      await this.deps.auditLogger.log({
        type: "bridge.command.help",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          "Bridge commands:",
          "- `@Chat Agent Bridge status`",
          "- `@Chat Agent Bridge history [count]`",
          "- `@Chat Agent Bridge cancel`",
          "- `@Chat Agent Bridge new-thread`",
          "- `@Chat Agent Bridge fork [codex|claude|cursor|gemini] [allow|manual]`",
          "- `@Chat Agent Bridge sessions`",
          "- `@Chat Agent Bridge approvals`",
          "- `@Chat Agent Bridge failures [count]`",
          "- `@Chat Agent Bridge repos`",
          "- `@Chat Agent Bridge allow-dirs`",
          "- `@Chat Agent Bridge allow-dir <absolute-path>`",
          "- `@Chat Agent Bridge allow-dir remove <absolute-path>`",
          "- `@Chat Agent Bridge repo <alias>`",
          "- `@Chat Agent Bridge cwd <path>`",
          "- `@Chat Agent Bridge channel-default <alias|path|clear>`",
          "- `@Chat Agent Bridge agent <codex|claude|cursor|gemini>`",
          "- `@Chat Agent Bridge mode <allow|manual>`",
          "- `@Chat Agent Bridge new [codex|claude|cursor|gemini] [allow|manual]`",
          "- `@Chat Agent Bridge attach <session-id>`",
          "- `@Chat Agent Bridge attach-last`",
          "- `@Chat Agent Bridge detach`",
          "- `@Chat Agent Bridge restart bridge`",
          "- `@Chat Agent Bridge approve`",
          "- `@Chat Agent Bridge reject`",
          "First message of a thread:",
          "- `@Chat Agent Bridge <repo-alias|absolute-path> <instruction>` starts in that directory",
          "- A fresh thread with no directory shows a numbered directory picker before running",
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "cancel") {
      await this.handleImmediateBridgeCommand(session, message, command);
      return;
    }

    if (command.kind === "new-thread") {
      const newThread = await this.deps.chatAdapter.startNewThread(
        message.thread.channelId,
        [
          `Continuation for session \`${session.sessionId}\`.`,
          `Agent: \`${session.agent}\``,
          `Working directory: \`${session.workingDirectory}\``,
          "Reply in this thread to continue.",
        ].join("\n"),
      );
      if (!newThread) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          "Thread split failed: the chat adapter could not create a new thread.",
        );
        return;
      }

      const nextSession: SessionBinding = {
        ...session,
        thread: newThread,
        updatedAt: new Date().toISOString(),
        bridgeState: this.buildSessionBridgeState(session, {
          threadUserMessageCount: 0,
        }),
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.new-thread",
        sessionId: session.sessionId,
        fromThreadId: session.thread.threadId,
        toThreadId: newThread.threadId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Session \`${session.sessionId}\` moved to a new thread in this channel. Continue in the newly created thread.`,
      );
      return;
    }

    if (command.kind === "fork-session") {
      const nextAgent = command.agent ?? session.agent;
      try {
        this.resolveAgent(nextAgent);
      } catch (error) {
        const text = error instanceof Error ? error.message : "Requested agent is unavailable.";
        await this.deps.chatAdapter.postThreadMessage(message.thread, `Fork failed: ${text}`);
        return;
      }

      const newThread = await this.deps.chatAdapter.startNewThread(
        message.thread.channelId,
        [
          `Forked from session \`${session.sessionId}\`.`,
          `Agent: \`${nextAgent}\``,
          `Working directory: \`${session.workingDirectory}\``,
          "Reply in this thread to start the forked session.",
        ].join("\n"),
      );
      if (!newThread) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          "Fork failed: the chat adapter could not create a new thread.",
        );
        return;
      }

      let forkedSession = await this.deps.sessionManager.getOrCreate({
        agent: nextAgent,
        thread: newThread,
        workingDirectory: session.workingDirectory,
        allowedDirectories: this.deps.config.allowedDirectories,
      });
      const approvalPolicy =
        command.approvalPolicy ?? readApprovalPolicyOverride(session.bridgeState);
      if (approvalPolicy) {
        forkedSession = {
          ...forkedSession,
          updatedAt: new Date().toISOString(),
          bridgeState: {
            ...(forkedSession.bridgeState ?? {}),
            connectionMode: "bridge-owned",
            threadUserMessageCount: 0,
            approvalPolicyOverride: approvalPolicy,
          },
        };
        await this.deps.stateStore.upsertSession(forkedSession);
      }

      await this.deps.auditLogger.log({
        type: "bridge.command.fork-session",
        sessionId: session.sessionId,
        forkedSessionId: forkedSession.sessionId,
        agent: nextAgent,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Forked session \`${forkedSession.sessionId}\` into a new thread using \`${nextAgent}\`. Continue in the newly created thread.`,
      );
      return;
    }

    if (command.kind === "status") {
      const sessionAgent = this.resolveSessionAgent(session);
      const pendingApproval = readPendingApproval(session.bridgeState);
      const approvalPolicyOverride = readApprovalPolicyOverride(session.bridgeState);
      const connectionMode = readSessionConnectionMode(session);
      const threadUserMessageCount = readThreadUserMessageCount(session.bridgeState);
      const latestExecution = (
        await this.deps.stateStore.listExecutionRecords({
          sessionId: session.sessionId,
          limit: 1,
        })
      )[0];
      const agentThreadId = readAgentSessionId(session) ?? "none";
      const channelDefault = await this.deps.stateStore.getChannelDefaultWorkingDirectory(
        message.thread.provider,
        message.thread.channelId,
      );
      await this.deps.auditLogger.log({
        type: "bridge.command.status",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `Session \`${session.sessionId}\``,
          `Agent: \`${session.agent}\``,
          `Slack mode: \`${this.deps.config.slackMode}\``,
          `Bridge started at: \`${this.startedAt}\``,
          `Session connection: \`${connectionMode}\``,
          `Working directory: \`${session.workingDirectory}\``,
          `Channel default cwd: \`${channelDefault ?? "none"}\``,
          `Approval policy: \`${this.resolveApprovalPolicy(session)}\``,
          `Approval override: \`${approvalPolicyOverride ?? "default"}\``,
          `Network access: \`${this.deps.config.networkAccess}\``,
          `Agent approval support: \`${sessionAgent.capabilities.manualApproval}\``,
          `Agent write rerun support: \`${sessionAgent.capabilities.writeApprovalRerun}\``,
          `Agent session id: \`${agentThreadId}\``,
          `Latest execution: \`${latestExecution?.executionId ?? "none"}\``,
          `Latest status: \`${latestExecution ? formatExecutionStatus(latestExecution.status) : "none"}\``,
          `Pending approval: \`${pendingApproval ? "yes" : "no"}\``,
          `Thread user messages: \`${threadUserMessageCount}/${this.deps.config.autoSplitThreadUserMessageCount}\``,
          `Updated at: \`${session.updatedAt}\``,
          pendingApproval
            ? `Pending request: \`${this.truncateForCode(pendingApproval.prompt, 120)}\``
            : "Pending request: `none`",
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "history") {
      await this.deps.auditLogger.log({
        type: "bridge.command.history",
        sessionId: session.sessionId,
        limit: command.limit,
      });
      const records = await this.deps.stateStore.listExecutionRecords({
        sessionId: session.sessionId,
        limit: command.limit,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        this.formatExecutionHistory(session, records, command.limit),
      );
      return;
    }

    if (command.kind === "list-sessions") {
      const sessions = (await this.deps.stateStore.listSessions()).sort((left, right) =>
        right.updatedAt.localeCompare(left.updatedAt),
      );
      await this.deps.auditLogger.log({
        type: "bridge.command.list-sessions",
        sessionId: session.sessionId,
        count: sessions.length,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        await this.formatSessionList(sessions),
      );
      return;
    }

    if (command.kind === "list-approvals") {
      const sessions = (await this.deps.stateStore.listSessions())
        .map((candidate) => ({
          session: candidate,
          pendingApproval: readPendingApproval(candidate.bridgeState),
        }))
        .filter((candidate) => Boolean(candidate.pendingApproval))
        .sort((left, right) => right.session.updatedAt.localeCompare(left.session.updatedAt));
      await this.deps.auditLogger.log({
        type: "bridge.command.list-approvals",
        sessionId: session.sessionId,
        count: sessions.length,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        this.formatApprovalList(
          sessions.map((candidate) => ({
            session: candidate.session,
            pendingApproval: candidate.pendingApproval!,
          })),
        ),
      );
      return;
    }

    if (command.kind === "list-failures") {
      const failures = (await this.deps.stateStore.listExecutionRecords())
        .filter((record) => record.status === "failed")
        .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
        .slice(0, command.limit);
      await this.deps.auditLogger.log({
        type: "bridge.command.list-failures",
        sessionId: session.sessionId,
        count: failures.length,
        limit: command.limit,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        this.formatFailureList(failures, command.limit),
      );
      return;
    }

    if (command.kind === "set-mode") {
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...(session.bridgeState ?? {}),
          approvalPolicyOverride: command.approvalPolicy,
        },
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.set-mode",
        sessionId: session.sessionId,
        approvalPolicy: command.approvalPolicy,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Session \`${session.sessionId}\` approval mode is now \`${command.approvalPolicy}\`.`,
      );
      return;
    }

    if (command.kind === "set-agent") {
      try {
        this.resolveAgent(command.agent);
      } catch (error) {
        const text = error instanceof Error ? error.message : "Requested agent is unavailable.";
        await this.deps.chatAdapter.postThreadMessage(message.thread, `Agent switch failed: ${text}`);
        return;
      }
      const nextBridgeState: Record<string, JsonValue> = {
        ...this.buildSessionBridgeState(session, {
          connectionMode: "bridge-owned",
        }),
      };

      const nextSession: SessionBinding = {
        ...session,
        agent: command.agent,
        updatedAt: new Date().toISOString(),
        agentState: {},
        bridgeState: nextBridgeState,
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.set-agent",
        sessionId: session.sessionId,
        agent: command.agent,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Session \`${session.sessionId}\` now uses \`${command.agent}\`. Send the next instruction to start a fresh ${formatAgentDisplayName(command.agent)} session.`,
      );
      return;
    }

    if (command.kind === "new-session") {
      const nextAgent = command.agent ?? session.agent;
      try {
        this.resolveAgent(nextAgent);
      } catch (error) {
        const text = error instanceof Error ? error.message : "Requested agent is unavailable.";
        await this.deps.chatAdapter.postThreadMessage(message.thread, `New session failed: ${text}`);
        return;
      }
      const nextBridgeState: Record<string, JsonValue> = this.buildSessionBridgeState(session, {
        connectionMode: "bridge-owned",
      });
      const approvalPolicy =
        command.approvalPolicy ?? readApprovalPolicyOverride(session.bridgeState);
      if (approvalPolicy) {
        nextBridgeState.approvalPolicyOverride = approvalPolicy;
      }

      const nextSession: SessionBinding = {
        ...session,
        agent: nextAgent,
        updatedAt: new Date().toISOString(),
        agentState: {},
        bridgeState: nextBridgeState,
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.new-session",
        sessionId: session.sessionId,
        approvalPolicy: approvalPolicy ?? "default",
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `Bridge session \`${session.sessionId}\` is reset for a fresh ${formatAgentDisplayName(nextAgent)} session.`,
          `Approval mode: \`${approvalPolicy ?? this.deps.config.defaultApprovalPolicy}\`.`,
          "Send the next instruction to begin.",
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "attach-last") {
      if (!supportsReusableSessions(session.agent)) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `${formatAgentDisplayName(session.agent)} session reuse is not supported yet. Use \`new\` to start a fresh session.`,
        );
        return;
      }
      try {
        const latest = await this.readLatestReusableSession(session);
        if (!latest) {
          await this.deps.chatAdapter.postThreadMessage(
            message.thread,
            `No ${formatAgentDisplayName(session.agent)} session was found in the local session index.`,
          );
          return;
        }

        await this.attachAgentSession(session, message, latest.id, "latest");
      } catch (error) {
        const text = error instanceof Error ? error.message : "Failed to read Codex session index.";
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Attach failed: ${text}`,
        );
      }
      return;
    }

    if (command.kind === "attach") {
      if (!supportsReusableSessions(session.agent)) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `${formatAgentDisplayName(session.agent)} does not support manual attach yet.`,
        );
        return;
      }
      await this.attachAgentSession(session, message, command.sessionId, "manual");
      return;
    }

    if (command.kind === "detach") {
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: this.buildSessionBridgeState(session, {
          connectionMode: "bridge-owned",
        }),
        agentState: {},
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.detach",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Detached bridge session \`${session.sessionId}\` from any saved agent session.`,
      );
      return;
    }

    if (command.kind === "restart-bridge") {
      await this.deps.auditLogger.log({
        type: "bridge.command.restart",
        sessionId: session.sessionId,
        rebuild: command.rebuild,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        command.rebuild
          ? "Bridge restart with rebuild requested. I will rebuild and restart the bridge process now. This usually takes longer than a normal restart."
          : "Bridge restart requested. I will restart the bridge process now and should be available again in a few seconds.",
      );
      this.scheduleDetachedRestart(message.thread.channelId, message.thread.threadId, {
        rebuild: command.rebuild,
        actor: "chat-command",
        reason: command.rebuild ? "chat-restart-rebuild" : "chat-restart",
      });
      return;
    }

    if (command.kind === "list-roots") {
      await this.deps.auditLogger.log({
        type: "bridge.command.list-roots",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          this.formatAllowedDirectories(),
          "",
          "Repo aliases:",
          formatRepoAliases(this.deps.config.repoAliases),
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "list-allowed-dirs") {
      await this.deps.auditLogger.log({
        type: "bridge.command.list-allowed-dirs",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        this.formatAllowedDirectories(),
      );
      return;
    }

    if (command.kind === "allow-dir") {
      await this.handleAllowDirCommand(session, message, command);
      return;
    }

    if (command.kind === "channel-default") {
      await this.handleChannelDefaultCommand(session, message, command);
      return;
    }

    if (command.kind === "set-repo") {
      const workingDirectory = this.deps.config.repoAliases[command.alias];
      if (!workingDirectory) {
        await this.deps.auditLogger.log({
          type: "bridge.command.set-repo.failed",
          sessionId: session.sessionId,
          alias: command.alias,
          error: "unknown-alias",
        });
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Unknown repo alias \`${command.alias}\`. Run \`@Chat Agent Bridge repos\` to list available aliases.`,
        );
        return;
      }

      await this.updateSessionWorkingDirectory(session, message, {
        workingDirectory,
        source: `repo ${command.alias}`,
        extraBridgeState: { workdirConfirmed: true },
      });
      return;
    }

    try {
      const workingDirectory = resolveRequestedWorkingDirectory({
        rawPath: command.rawPath,
        session,
        defaultWorkingDirectory: this.deps.config.defaultWorkingDirectory,
        allowedDirectories: this.deps.config.allowedDirectories,
      });

      await this.updateSessionWorkingDirectory(session, message, {
        workingDirectory,
        source: command.rawPath,
        extraBridgeState: { workdirConfirmed: true },
      });
    } catch (error) {
      const text =
        error instanceof Error ? error.message : "Failed to change the working directory.";
      await this.deps.auditLogger.log({
        type: "bridge.command.set-workdir.failed",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
        rawPath: command.rawPath,
        error: text,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Working directory update failed: ${text}`,
      );
    }
  }

  private async handleImmediateBridgeCommand(
    session: SessionBinding,
    message: IncomingChatMessage,
    command: Extract<BridgeCommand, { kind: "cancel" }>,
  ): Promise<void> {
    const cancelled = await this.resolveSessionAgent(session).cancelExecution(session.sessionId);
    await this.deps.auditLogger.log({
      type: "bridge.command.cancel",
      sessionId: session.sessionId,
      requested: true,
      result: cancelled ? "accepted" : "missing-active-execution",
    });

    if (!cancelled) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `No running execution was found for session \`${session.sessionId}\`.`,
      );
      return;
    }

    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Cancellation requested for session \`${session.sessionId}\`. I will stop it best-effort and post the final state here.`,
    );
  }

  private async handleEmptyMention(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<void> {
    const existingThreadId =
      readAgentSessionId(session) ?? "";
    if (existingThreadId) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `This thread is already attached to ${formatAgentDisplayName(session.agent)} session \`${existingThreadId}\`.`,
          "Use `@Chat Agent Bridge status` to inspect it, or send an instruction to continue.",
        ].join("\n"),
      );
      return;
    }

    try {
      if (!supportsReusableSessions(session.agent)) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Reusable ${formatAgentDisplayName(session.agent)} sessions are not supported yet. Reply \`new\` to start a fresh session.`,
        );
        return;
      }

      const sessions = await this.readRecentReusableSessions(session, 5);
      if (sessions.length === 0) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `No reusable ${formatAgentDisplayName(session.agent)} sessions were found. Reply \`new\` to start a fresh session.`,
        );
        return;
      }

      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...this.buildSessionBridgeState(session, {
            connectionMode: "bridge-owned",
          }),
          sessionPicker: {
            sessionIds: sessions.map((candidate) => candidate.id),
          },
        },
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.session-picker.opened",
        sessionId: session.sessionId,
        candidateCount: sessions.length,
      });
      const lines = [
        `Choose a ${formatAgentDisplayName(session.agent)} session to attach, or reply \`new\` to start a fresh one:`,
        ...sessions.map((candidate, index) => {
          const label = candidate.label ? ` - ${candidate.label}` : "";
          return `${index + 1}. \`${candidate.id}\`${label}`;
        }),
      ];
      await this.deps.chatAdapter.postThreadMessage(message.thread, lines.join("\n"));
    } catch (error) {
      const text =
        error instanceof Error
          ? error.message
          : `Failed to read local ${formatAgentDisplayName(session.agent)} sessions.`;
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Failed to list reusable ${formatAgentDisplayName(session.agent)} sessions: ${text}`,
      );
    }
  }

  private async handleSessionPickerSelection(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<boolean> {
    const picker = readSessionPicker(session.bridgeState);
    if (!picker) {
      return false;
    }

    const pendingPrompt = picker.pendingPrompt;
    const normalized = message.text.trim().toLowerCase();
    if (normalized === "new" || normalized === "start") {
      const nextBridgeState = { ...(session.bridgeState ?? {}) };
      delete nextBridgeState.sessionPicker;
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...this.buildSessionBridgeState(session),
          ...nextBridgeState,
        },
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.session-picker.new",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Starting a fresh ${formatAgentDisplayName(session.agent)} session for bridge session \`${session.sessionId}\`. ${
          pendingPrompt ? "Running your pending instruction now." : "Send the next instruction to begin."
        }`,
      );
      this.dispatchPendingPrompt(message, pendingPrompt);
      return true;
    }

    const selectedIndex = Number.parseInt(normalized, 10);
    if (!Number.isFinite(selectedIndex) || selectedIndex < 1 || selectedIndex > picker.sessionIds.length) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Invalid selection. Reply with a number from 1 to ${picker.sessionIds.length}, or \`new\`.`,
      );
      return true;
    }

    await this.attachAgentSession(
      session,
      message,
      picker.sessionIds[selectedIndex - 1] ?? "",
      "picker",
    );
    this.dispatchPendingPrompt(message, pendingPrompt);
    return true;
  }

  private dispatchPendingPrompt(
    message: IncomingChatMessage,
    pendingPrompt: string | undefined,
  ): void {
    if (!pendingPrompt) {
      return;
    }

    queueMicrotask(() => {
      void this.handleMessage({ ...message, text: pendingPrompt }).catch((error: unknown) => {
        console.error("Pending prompt dispatch failed.", error);
      });
    });
  }

  private formatAllowedDirectories(): string {
    const roots = this.deps.config.allowedDirectories;
    if (roots.length === 0) {
      return "Allowed working-directory roots: `none`";
    }

    return [
      "Allowed working-directory roots:",
      ...roots.map((root, index) => `${index + 1}. \`${root}\``),
    ].join("\n");
  }

  /**
   * Recompute repo aliases and persist the allowed roots after the shared
   * config.allowedDirectories array has been mutated in place. The array is
   * mutated (not reassigned) so path checks holding a reference see the change.
   */
  private async persistAllowedDirs(): Promise<void> {
    this.deps.config.repoAliases = computeRepoAliases(
      this.deps.config.repoAliasesRaw,
      this.deps.config.allowedDirectories,
      this.deps.config.discoverRepos ?? false,
    );
    updatePersistedSettings(this.deps.config.settingsFile, {
      allowedDirs: this.deps.config.allowedDirectories.join(";"),
    });
  }

  private async handleAllowDirCommand(
    session: SessionBinding,
    message: IncomingChatMessage,
    command: Extract<BridgeCommand, { kind: "allow-dir" }>,
  ): Promise<void> {
    const resolved = path.resolve(command.rawPath);

    if (command.action === "add") {
      // Already inside an existing root? Then it is already usable; do nothing.
      let alreadyAllowed = false;
      try {
        assertPathAllowed(resolved, this.deps.config.allowedDirectories);
        alreadyAllowed = true;
      } catch {
        alreadyAllowed = false;
      }
      if (alreadyAllowed) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `\`${resolved}\` is already allowed by an existing root. No change made.`,
        );
        return;
      }

      let stat: fs.Stats | undefined;
      try {
        stat = fs.statSync(resolved);
      } catch {
        stat = undefined;
      }
      if (!stat || !stat.isDirectory()) {
        await this.deps.auditLogger.log({
          type: "bridge.command.allow-dir.failed",
          sessionId: session.sessionId,
          rawPath: command.rawPath,
          error: "not-a-directory",
        });
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Cannot add root: \`${resolved}\` does not exist or is not a directory.`,
        );
        return;
      }

      const before = [...this.deps.config.allowedDirectories];
      this.deps.config.allowedDirectories.push(resolved);
      await this.persistAllowedDirs();
      await this.deps.auditLogger.log({
        type: "bridge.command.allow-dir",
        sessionId: session.sessionId,
        rawPath: command.rawPath,
        before,
        after: [...this.deps.config.allowedDirectories],
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [`Added allowed root \`${resolved}\`.`, "", this.formatAllowedDirectories()].join("\n"),
      );
      return;
    }

    // action === "remove"
    const index = this.deps.config.allowedDirectories.findIndex(
      (root) => path.resolve(root) === resolved,
    );
    if (index === -1) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `\`${resolved}\` is not an exact allowed root, so nothing was removed.`,
          this.formatAllowedDirectories(),
        ].join("\n"),
      );
      return;
    }

    if (this.deps.config.allowedDirectories.length === 1) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        "Cannot remove the last remaining allowed root. Add another root first.",
      );
      return;
    }

    const remaining = this.deps.config.allowedDirectories.filter((_, i) => i !== index);
    try {
      assertPathAllowed(this.deps.config.defaultWorkingDirectory, remaining);
    } catch {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Cannot remove \`${resolved}\`: the default working directory \`${this.deps.config.defaultWorkingDirectory}\` would then be outside all allowed roots.`,
      );
      return;
    }

    const before = [...this.deps.config.allowedDirectories];
    this.deps.config.allowedDirectories.splice(index, 1);
    await this.persistAllowedDirs();
    await this.deps.auditLogger.log({
      type: "bridge.command.allow-dir.remove",
      sessionId: session.sessionId,
      rawPath: command.rawPath,
      before,
      after: [...this.deps.config.allowedDirectories],
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      [
        `Removed allowed root \`${resolved}\`.`,
        "Sessions already bound inside it keep working until their next working-directory change.",
        "",
        this.formatAllowedDirectories(),
      ].join("\n"),
    );
  }

  private async updateSessionWorkingDirectory(
    session: SessionBinding,
    message: IncomingChatMessage,
    input: {
      workingDirectory: string;
      source: string;
      // Extra bridgeState keys merged before persisting (e.g. workdirConfirmed).
      extraBridgeState?: Record<string, JsonValue>;
    },
  ): Promise<SessionBinding> {
    const directoryChanged =
      path.resolve(session.workingDirectory) !== path.resolve(input.workingDirectory);
    const detachClaude =
      directoryChanged &&
      session.agent === "claude-code" &&
      (readAgentSessionId(session) !== undefined || Boolean(session.bridgeState?.resumeOffered));

    const nextSession: SessionBinding = {
      ...session,
      workingDirectory: input.workingDirectory,
      updatedAt: new Date().toISOString(),
    };

    if (detachClaude) {
      const nextBridgeState = this.buildSessionBridgeState(session, {
        connectionMode: "bridge-owned",
      });
      delete nextBridgeState.resumeOffered;
      delete nextBridgeState.sessionPicker;
      nextSession.agentState = {};
      nextSession.bridgeState = { ...nextBridgeState, ...(input.extraBridgeState ?? {}) };
    } else if (input.extraBridgeState) {
      nextSession.bridgeState = {
        ...(session.bridgeState ?? {}),
        ...input.extraBridgeState,
      };
    }

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.command.set-workdir",
      sessionId: session.sessionId,
      previousWorkingDirectory: session.workingDirectory,
      workingDirectory: input.workingDirectory,
      rawPath: input.source,
      ...(detachClaude ? { detachedAgentSession: true } : {}),
    });
    const lines = [
      `Session \`${session.sessionId}\` is now bound to \`${input.workingDirectory}\`. Send your next instruction to continue.`,
    ];
    if (detachClaude) {
      lines.push(
        "The attached Claude Code session was detached because the folder changed. The next instruction will offer resume choices for the new folder.",
      );
    }
    await this.deps.chatAdapter.postThreadMessage(message.thread, lines.join("\n"));

    return nextSession;
  }

  private async attachAgentSession(
    session: SessionBinding,
    message: IncomingChatMessage,
    agentSessionId: string,
    source: "latest" | "manual" | "picker",
  ): Promise<void> {
    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: {
        ...this.buildSessionBridgeState(session, {
          connectionMode: "attached",
        }),
      },
      agentState: createAttachedAgentState(session, agentSessionId),
    };

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.command.attach",
      sessionId: session.sessionId,
      agentSessionId,
      source,
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Bridge session \`${session.sessionId}\` is now attached to ${formatAgentDisplayName(session.agent)} session \`${agentSessionId}\`.`,
    );
  }

  private async maybeOfferWorkdir(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<{ offered: boolean; session: SessionBinding; text: string }> {
    const text = message.text;
    if (session.bridgeState?.workdirConfirmed === true) {
      return { offered: false, session, text };
    }

    if (
      readWorkdirPicker(session.bridgeState) ||
      readSessionPicker(session.bridgeState)
    ) {
      return { offered: false, session, text };
    }

    if (readAgentSessionId(session)) {
      return { offered: false, session, text };
    }

    const priorExecutions = await this.deps.stateStore.listExecutionRecords({
      sessionId: session.sessionId,
      limit: 1,
    });
    if (priorExecutions.length > 0) {
      // An existing conversation already picked a working directory implicitly;
      // record that so this check short-circuits on subsequent messages.
      const nextSession = await this.persistWorkdirConfirmed(session);
      return { offered: false, session: nextSession, text };
    }

    // (a) Inline directory token as the first word of the instruction.
    const trimmed = text.trim();
    const firstToken = trimmed.split(/\s+/, 1)[0] ?? "";
    const aliasKey = firstToken.toLowerCase();
    const aliasTarget = this.deps.config.repoAliases[aliasKey];
    const looksLikePath =
      firstToken.includes("/") || firstToken.includes("\\") || /^[A-Za-z]:/.test(firstToken);

    let inlineDirectory: string | undefined;
    if (aliasTarget) {
      inlineDirectory = aliasTarget;
    } else if (looksLikePath && firstToken.length > 0) {
      let resolved: string | undefined;
      try {
        const candidate = path.resolve(firstToken);
        assertPathAllowed(candidate, this.deps.config.allowedDirectories);
        if (fs.existsSync(candidate) && fs.statSync(candidate).isDirectory()) {
          resolved = candidate;
        }
      } catch {
        resolved = undefined;
      }

      if (!resolved) {
        // The token clearly looks like a path but is not allowed or does not
        // exist. Surface an error instead of running in the wrong folder.
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          [
            `Directory not allowed or not found: \`${firstToken}\``,
            "Run `@Chat Agent Bridge repos` to see the allowed roots.",
          ].join("\n"),
        );
        return { offered: true, session, text };
      }

      inlineDirectory = resolved;
    }

    if (inlineDirectory) {
      const updated = await this.updateSessionWorkingDirectory(session, message, {
        workingDirectory: inlineDirectory,
        source: aliasTarget ? `repo ${aliasKey}` : firstToken,
        extraBridgeState: { workdirConfirmed: true },
      });
      const remainder = trimmed.slice(firstToken.length).trim();
      if (remainder.length === 0) {
        return { offered: true, session: updated, text: remainder };
      }

      return { offered: false, session: updated, text: remainder };
    }

    // The inline token wins above; otherwise, a channel default that matches the
    // session's current directory has already answered the directory question, so
    // skip the picker and confirm the working directory.
    const channelDefault = await this.deps.stateStore.getChannelDefaultWorkingDirectory(
      session.thread.provider,
      session.thread.channelId,
    );
    if (
      channelDefault &&
      path.resolve(channelDefault) === path.resolve(session.workingDirectory)
    ) {
      const nextSession = await this.persistWorkdirConfirmed(session);
      return { offered: false, session: nextSession, text };
    }

    // (b) Numbered directory picker over the allowed repo aliases.
    const aliasEntries = Object.keys(this.deps.config.repoAliases).sort((left, right) =>
      left.localeCompare(right),
    );
    if (aliasEntries.length === 0) {
      const nextSession = await this.persistWorkdirConfirmed(session);
      return { offered: false, session: nextSession, text };
    }

    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session, {
        workdirPicker: {
          aliases: aliasEntries,
          pendingPrompt: message.text,
        },
      }),
    };
    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.workdir-picker.opened",
      sessionId: session.sessionId,
      candidateCount: aliasEntries.length,
    });

    const lines = [
      `Choose a working directory for this thread (allowed roots only). Reply with a number, or \`keep\` to stay on \`${session.workingDirectory}\`.`,
      ...aliasEntries.map(
        (alias, index) =>
          `${index + 1}. \`${alias}\` -> \`${this.deps.config.repoAliases[alias]}\``,
      ),
    ];
    await this.deps.chatAdapter.postThreadMessage(message.thread, lines.join("\n"));

    return { offered: true, session: nextSession, text };
  }

  private async persistWorkdirConfirmed(session: SessionBinding): Promise<SessionBinding> {
    if (session.bridgeState?.workdirConfirmed === true) {
      return session;
    }

    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session, {
        workdirConfirmed: true,
      }),
    };
    await this.deps.stateStore.upsertSession(nextSession);
    return nextSession;
  }

  private async handleWorkdirPickerSelection(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<boolean> {
    const picker = readWorkdirPicker(session.bridgeState);
    if (!picker) {
      return false;
    }

    const pendingPrompt = picker.pendingPrompt;
    const normalized = message.text.trim().toLowerCase();

    if (normalized === "keep") {
      const nextBridgeState = { ...(session.bridgeState ?? {}) };
      delete nextBridgeState.workdirPicker;
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...nextBridgeState,
          workdirConfirmed: true,
        },
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.workdir-picker.kept",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Keeping working directory \`${session.workingDirectory}\`.`,
      );
      this.dispatchPendingPrompt(message, pendingPrompt);
      return true;
    }

    const selectedIndex = Number.parseInt(normalized, 10);
    if (
      !Number.isFinite(selectedIndex) ||
      selectedIndex < 1 ||
      selectedIndex > picker.aliases.length
    ) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Invalid selection. Reply with a number from 1 to ${picker.aliases.length}, or \`keep\`.`,
      );
      return true;
    }

    const alias = picker.aliases[selectedIndex - 1] ?? "";
    const workingDirectory = this.deps.config.repoAliases[alias];
    if (!workingDirectory) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Invalid selection. Reply with a number from 1 to ${picker.aliases.length}, or \`keep\`.`,
      );
      return true;
    }

    // updateSessionWorkingDirectory clears the workdirPicker via buildSessionBridgeState
    // (which does not preserve it) when detaching; for the non-detach path we drop it
    // by rebuilding bridgeState below through extraBridgeState + buildSessionBridgeState.
    const cleared: SessionBinding = {
      ...session,
      bridgeState: this.buildSessionBridgeState(session),
    };
    await this.updateSessionWorkingDirectory(cleared, message, {
      workingDirectory,
      source: `repo ${alias}`,
      extraBridgeState: { workdirConfirmed: true },
    });
    await this.deps.auditLogger.log({
      type: "bridge.workdir-picker.selected",
      sessionId: session.sessionId,
      alias,
      workingDirectory,
    });
    this.dispatchPendingPrompt(message, pendingPrompt);
    return true;
  }

  private async maybeOfferResume(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<{ offered: boolean; session: SessionBinding }> {
    if (session.agent !== "claude-code") {
      return { offered: false, session };
    }

    if (readAgentSessionId(session)) {
      return { offered: false, session };
    }

    if (readSessionPicker(session.bridgeState)) {
      return { offered: false, session };
    }

    if (session.bridgeState?.resumeOffered === true) {
      return { offered: false, session };
    }

    const priorExecutions = await this.deps.stateStore.listExecutionRecords({
      sessionId: session.sessionId,
      limit: 1,
    });
    if (priorExecutions.length > 0) {
      return { offered: false, session };
    }

    let candidates: StoredAgentSession[] = [];
    try {
      candidates = await this.readRecentReusableSessions(session, 5);
    } catch {
      candidates = [];
    }

    if (candidates.length === 0) {
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: this.buildSessionBridgeState(session, {
          resumeOffered: true,
        }),
      };
      await this.deps.stateStore.upsertSession(nextSession);
      return { offered: false, session: nextSession };
    }

    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session, {
        resumeOffered: true,
        sessionPicker: {
          sessionIds: candidates.map((candidate) => candidate.id),
          pendingPrompt: message.text,
        },
      }),
    };
    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.resume-picker.opened",
      sessionId: session.sessionId,
      workingDirectory: session.workingDirectory,
      candidateCount: candidates.length,
    });

    const lines = [
      `This folder (\`${session.workingDirectory}\`) has previous Claude Code sessions. Reply with a number to resume, or \`new\` to start fresh. Your instruction will run after you choose.`,
      ...candidates.map((candidate, index) => {
        const label = candidate.label ? ` - ${candidate.label}` : "";
        return `${index + 1}. \`${candidate.id}\`${label} (${candidate.updatedAt})`;
      }),
    ];
    await this.deps.chatAdapter.postThreadMessage(message.thread, lines.join("\n"));

    return { offered: true, session: nextSession };
  }

  private async readLatestReusableSession(
    session: SessionBinding,
  ): Promise<StoredAgentSession | undefined> {
    const agentKind = session.agent;
    if (agentKind === "cursor" || agentKind === "gemini") {
      return undefined;
    }

    if (agentKind === "claude-code") {
      const candidates = await this.readRecentReusableSessions(session, 1);
      return candidates[0];
    }

    const latest = await readLatestCodexSession(this.deps.config.codexSessionIndexFile);
    return latest
      ? {
          id: latest.id,
          updatedAt: latest.updatedAt,
          ...(latest.threadName
            ? {
                label: latest.threadName,
              }
            : {}),
        }
      : undefined;
  }

  private async readRecentReusableSessions(
    session: SessionBinding,
    limit: number,
  ): Promise<StoredAgentSession[]> {
    const agentKind = session.agent;
    if (agentKind === "cursor" || agentKind === "gemini") {
      return [];
    }

    if (agentKind === "claude-code") {
      const projectSessions = await readClaudeProjectSessions(
        this.deps.config.claudeProjectsDir,
        session.workingDirectory,
        limit,
      );
      if (projectSessions.length > 0) {
        return projectSessions.map((entry) => ({
          id: entry.id,
          updatedAt: entry.updatedAt,
          ...(entry.label ? { label: entry.label } : {}),
        }));
      }

      try {
        const legacy = await readRecentClaudeSessions(
          this.deps.config.claudeSessionIndexFile,
          limit,
        );
        return legacy.map((entry) => ({
          id: entry.id,
          updatedAt: entry.updatedAt,
          ...(entry.summary ? { label: entry.summary } : {}),
        }));
      } catch (error) {
        if (error instanceof Error && "code" in error) {
          const code = (error as NodeJS.ErrnoException).code;
          if (code === "ENOENT") {
            return [];
          }
        }
        throw error;
      }
    }

    const sessions = await readRecentCodexSessions(this.deps.config.codexSessionIndexFile, limit);
    return sessions.map((entry) => ({
      id: entry.id,
      updatedAt: entry.updatedAt,
      ...(entry.threadName
        ? {
            label: entry.threadName,
          }
        : {}),
    }));
  }

  private async handleApprovalMessage(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<void> {
    const sessionAgent = this.resolveSessionAgent(session);
    if (!sessionAgent.capabilities.writeApprovalRerun) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Agent \`${session.agent}\` does not support approval reruns.`,
      );
      return;
    }

    const pendingApproval = readPendingApproval(session.bridgeState);
    if (!pendingApproval) {
      await this.deps.auditLogger.log({
        type: "bridge.approval.missing",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        "No pending approval was found for this thread.",
      );
      return;
    }

    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Approval accepted. Re-running the last request for session \`${session.sessionId}\` with write access.`,
    );
    await this.deps.auditLogger.log({
      type: "bridge.approval.accepted",
      sessionId: session.sessionId,
      prompt: pendingApproval.prompt,
    });

    let progress: Awaited<ReturnType<BridgeService["createProgressReporter"]>> | undefined;
    let executionRecord = this.createExecutionRecord({
      session,
      prompt: pendingApproval.prompt,
      approvalPolicy: "never",
      networkAccess: this.deps.config.networkAccess,
      ...(pendingApproval.executionId
        ? {
            rerunOfExecutionId: pendingApproval.executionId,
          }
        : {}),
    });
    await this.deps.stateStore.upsertExecutionRecord(executionRecord);
    progress = await this.createProgressReporter(
      session,
      message.thread,
      "running",
      "approval accepted; rerunning",
    );
    try {
      await this.logExecutionEnvironment(session, "never");
      const result = await sessionAgent.execute({
        session,
        prompt: pendingApproval.prompt,
        approvalPolicy: "never",
        networkAccess: this.deps.config.networkAccess,
        allowedDirectories: this.deps.config.allowedDirectories,
        onActivity: progress.onActivity,
      });

      const persistInput: Parameters<BridgeService["persistSessionAfterRun"]>[1] = {
        prompt: pendingApproval.prompt,
        resultSummary: result.summary,
        clearPendingApproval: true,
      };

      if (result.agentState) {
        persistInput.agentState = result.agentState;
      }

      executionRecord = this.completeExecutionRecord(executionRecord, "completed", result.summary);
      await this.deps.stateStore.upsertExecutionRecord(executionRecord);
      session = await this.persistSessionAfterRun(session, persistInput);
      await progress.finish("completed", "completed");
      await this.deps.auditLogger.log({
        type: "bridge.execution.completed",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
        summary: result.summary,
        rerun: true,
      });
      await this.deps.chatAdapter.postThreadMessage(message.thread, result.summary);
    } catch (error) {
      if (error instanceof AgentExecutionCancelledError) {
        await progress?.finish("cancelled", "cancelled").catch(() => undefined);
        await this.deps.stateStore.upsertExecutionRecord(
          this.completeExecutionRecord(executionRecord, "cancelled", error.message),
        );
        await this.deps.auditLogger.log({
          type: "bridge.execution.cancelled",
          sessionId: session.sessionId,
          workingDirectory: session.workingDirectory,
          rerun: true,
        });
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Execution cancelled for session \`${session.sessionId}\`.`,
        );
        return;
      }

      const text = error instanceof Error ? error.message : "Unknown bridge error.";
      await progress?.finish("failed", "execution failed").catch(() => undefined);
      await this.deps.stateStore.upsertExecutionRecord(
        this.failExecutionRecord(executionRecord, text),
      );
      await this.deps.auditLogger.log({
        type: "bridge.execution.failed",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
        error: text,
        rerun: true,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Execution failed for session \`${session.sessionId}\`: ${text}`,
      );
    }
  }

  private async handleRejectionMessage(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<void> {
    const pendingApproval = readPendingApproval(session.bridgeState);
    if (!pendingApproval) {
      await this.deps.auditLogger.log({
        type: "bridge.approval.reject-missing",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        "No pending approval was found for this thread.",
      );
      return;
    }

    if (pendingApproval.executionId) {
      await this.updateExecutionStatusById(
        pendingApproval.executionId,
        "rejected",
        pendingApproval.reason,
      );
    }
    await this.persistSessionAfterRun(session, {
      prompt: pendingApproval.prompt,
      resultSummary: pendingApproval.reason,
      clearPendingApproval: true,
    });
    await this.deps.auditLogger.log({
      type: "bridge.approval.rejected",
      sessionId: session.sessionId,
      prompt: pendingApproval.prompt,
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Pending approval for session \`${session.sessionId}\` was cleared.`,
    );
  }

  private async createProgressReporter(
    session: SessionBinding,
    thread: IncomingChatMessage["thread"],
    status: string,
    detail: string,
  ): Promise<{
    onActivity: () => Promise<void>;
    finish: (nextStatus: string, nextDetail: string) => Promise<void>;
  }> {
    const progressText = this.formatProgressMessage(session, status, detail);
    const progressMessageId = await this.deps.chatAdapter.postThreadMessage(
      thread,
      progressText,
      {
        blocks: buildProgressBlocks(progressText, status),
        ...(status === "running"
          ? {
              actions: buildCancelActions(),
            }
          : {}),
      },
    );
    let lastUpdateAt = Date.now();
    let updateInFlight = false;
    const recentOutput: string[] = [];

    const onActivity = async (update?: AgentProgressUpdate) => {
      if (update?.text) {
        recentOutput.push(`[${update.stream}] ${update.text}`);
        while (recentOutput.length > MAX_PROGRESS_LINES) {
          recentOutput.shift();
        }
      }

      const now = Date.now();
      const shouldUpdateImmediately = Boolean(update?.urgent);
      if (
        updateInFlight ||
        (!shouldUpdateImmediately && now - lastUpdateAt < PROGRESS_UPDATE_INTERVAL_MS)
      ) {
        return;
      }

      updateInFlight = true;
      lastUpdateAt = now;
      try {
        await this.updateProgressMessage(
          session,
          thread,
          progressMessageId,
          "running",
          "still running",
          recentOutput,
        );
      } finally {
        updateInFlight = false;
      }
    };

    const finish = async (nextStatus: string, nextDetail: string) => {
      await this.updateProgressMessage(
        session,
        thread,
        progressMessageId,
        nextStatus,
        nextDetail,
        recentOutput,
      );
    };

    return {
      onActivity,
      finish,
    };
  }

  private async updateProgressMessage(
    session: SessionBinding,
    thread: IncomingChatMessage["thread"],
    messageId: string | undefined,
    status: string,
    detail: string,
    recentOutput?: string[],
  ): Promise<void> {
    if (!messageId) {
      return;
    }

    await this.deps.chatAdapter.updateThreadMessage(
      thread,
      messageId,
      this.formatProgressMessage(session, status, detail, recentOutput),
      {
        blocks: buildProgressBlocks(
          this.formatProgressMessage(session, status, detail, recentOutput),
          status,
        ),
        ...(status === "running"
          ? {
              actions: buildCancelActions(),
            }
          : {}),
      },
    );
  }

  private formatProgressMessage(
    session: SessionBinding,
    status: string,
    detail: string,
    recentOutput?: string[],
  ): string {
    const lines = [
      formatProgressHeading(session, status),
      `State: ${formatProgressStatus(status)}`,
      `Progress: ${formatProgressDetail(detail)}`,
      `Updated: ${new Date().toISOString()}`,
    ];

    if (recentOutput && recentOutput.length > 0) {
      const safeOutput = recentOutput
        .map((line) => line.replace(/```/g, "'''"))
        .join("\n");
      lines.push(PROGRESS_COPY.recentActivityLabel);
      lines.push("```text");
      lines.push(safeOutput);
      lines.push("```");
    }

    return lines.join("\n");
  }

  private createExecutionRecord(input: {
    session: SessionBinding;
    prompt: string;
    approvalPolicy: AppConfig["defaultApprovalPolicy"];
    networkAccess: AppConfig["networkAccess"];
    rerunOfExecutionId?: string;
  }): ExecutionRecord {
    const now = new Date().toISOString();
    return {
      executionId: crypto.randomUUID(),
      sessionId: input.session.sessionId,
      thread: input.session.thread,
      agent: input.session.agent,
      prompt: input.prompt,
      workingDirectory: input.session.workingDirectory,
      approvalPolicy: input.approvalPolicy,
      networkAccess: input.networkAccess,
      status: "running",
      createdAt: now,
      updatedAt: now,
      ...(input.rerunOfExecutionId
        ? {
            rerunOfExecutionId: input.rerunOfExecutionId,
          }
        : {}),
    };
  }

  private completeExecutionRecord(
    record: ExecutionRecord,
    status: Extract<
      ExecutionStatus,
      "completed" | "waiting_for_approval" | "cancelled" | "rejected"
    >,
    summary: string,
  ): ExecutionRecord {
    const now = new Date().toISOString();
    const completedAt =
      status === "completed" || status === "cancelled" || status === "rejected"
        ? now
        : record.completedAt;
    return {
      ...record,
      status,
      summary,
      updatedAt: now,
      ...(completedAt
        ? {
            completedAt,
          }
        : {}),
    };
  }

  private failExecutionRecord(record: ExecutionRecord, error: string): ExecutionRecord {
    const now = new Date().toISOString();
    return {
      ...record,
      status: "failed",
      error,
      updatedAt: now,
      completedAt: now,
    };
  }

  private async updateExecutionStatusById(
    executionId: string,
    status: Extract<
      ExecutionStatus,
      "completed" | "waiting_for_approval" | "cancelled" | "rejected"
    >,
    summary: string,
  ): Promise<void> {
    const records = await this.deps.stateStore.listExecutionRecords();
    const record = records.find((candidate) => candidate.executionId === executionId);
    if (!record) {
      return;
    }

    await this.deps.stateStore.upsertExecutionRecord(
      this.completeExecutionRecord(record, status, summary),
    );
  }

  private formatExecutionHistory(
    session: SessionBinding,
    records: ExecutionRecord[],
    requestedLimit: number,
  ): string {
    if (records.length === 0) {
      return `No execution history was found for session \`${session.sessionId}\`.`;
    }

    return [
      `Recent executions for session \`${session.sessionId}\` (${records.length}/${requestedLimit}):`,
      ...records.map((record, index) => {
        const outcome = record.summary ?? record.error ?? "No summary recorded.";
        const truncatedOutcome = this.truncateForCode(outcome, 120);
        return [
          `${index + 1}. \`${record.executionId}\``,
          `status=\`${formatExecutionStatus(record.status)}\``,
          `cwd=\`${record.workingDirectory}\``,
          `updated=\`${record.updatedAt}\``,
          `prompt=\`${this.truncateForCode(record.prompt, 80)}\``,
          `result=\`${truncatedOutcome}\``,
        ].join(" ");
      }),
    ].join("\n");
  }

  private async formatSessionList(sessions: SessionBinding[]): Promise<string> {
    if (sessions.length === 0) {
      return "No bridge sessions were found.";
    }

    const latestBySessionId = new Map<string, ExecutionRecord>();
    const records = await this.deps.stateStore.listExecutionRecords();
    for (const record of records) {
      if (!latestBySessionId.has(record.sessionId)) {
        latestBySessionId.set(record.sessionId, record);
      }
    }

    return [
      `Bridge sessions (${sessions.length}):`,
      ...sessions.map((candidate, index) => {
        const latest = latestBySessionId.get(candidate.sessionId);
        const pendingApproval = readPendingApproval(candidate.bridgeState);
        return [
          `${index + 1}. \`${candidate.sessionId}\``,
          `agent=\`${candidate.agent}\``,
          `status=\`${latest ? formatExecutionStatus(latest.status) : "idle"}\``,
          `mode=\`${readSessionConnectionMode(candidate)}\``,
          `cwd=\`${candidate.workingDirectory}\``,
          `updated=\`${candidate.updatedAt}\``,
          `pending=\`${pendingApproval ? "yes" : "no"}\``,
        ].join(" ");
      }),
    ].join("\n");
  }

  private formatApprovalList(
    entries: Array<{
      session: SessionBinding;
      pendingApproval: NonNullable<ReturnType<typeof readPendingApproval>>;
    }>,
  ): string {
    if (entries.length === 0) {
      return "No pending approvals were found.";
    }

    return [
      `Pending approvals (${entries.length}):`,
      ...entries.map((entry, index) =>
        [
          `${index + 1}. \`${entry.session.sessionId}\``,
          `requested=\`${entry.pendingApproval.requestedAt}\``,
          `cwd=\`${entry.session.workingDirectory}\``,
          `prompt=\`${this.truncateForCode(entry.pendingApproval.prompt, 80)}\``,
        ].join(" "),
      ),
    ].join("\n");
  }

  private formatFailureList(records: ExecutionRecord[], requestedLimit: number): string {
    if (records.length === 0) {
      return "No failed executions were found.";
    }

    return [
      `Recent failures (${records.length}/${requestedLimit}):`,
      ...records.map((record, index) =>
        [
          `${index + 1}. \`${record.executionId}\``,
          `session=\`${record.sessionId}\``,
          `updated=\`${record.updatedAt}\``,
          `cwd=\`${record.workingDirectory}\``,
          `prompt=\`${this.truncateForCode(record.prompt, 70)}\``,
          `error=\`${this.truncateForCode(record.error ?? "Unknown error.", 90)}\``,
        ].join(" "),
      ),
    ].join("\n");
  }

  private async persistSessionAfterRun(
    session: SessionBinding,
    input: {
      prompt: string;
      resultSummary: string;
      agentState?: Record<string, import("./types.js").JsonValue>;
      clearPendingApproval?: boolean;
      allowPendingApproval?: boolean;
      pendingApprovalExecutionId?: string;
    },
  ): Promise<SessionBinding> {
    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
    };
    const nextBridgeState = this.buildSessionBridgeState(session);

    if (input.agentState) {
      nextSession.agentState = input.agentState;
    }

    if (input.clearPendingApproval) {
      nextSession.bridgeState = nextBridgeState;
    } else if (input.allowPendingApproval && shouldRequestApproval(input.resultSummary)) {
      nextSession.bridgeState = {
        ...nextBridgeState,
        pendingApproval: {
          prompt: input.prompt,
          requestedAt: new Date().toISOString(),
          reason: input.resultSummary,
          ...(input.pendingApprovalExecutionId
            ? {
                executionId: input.pendingApprovalExecutionId,
              }
            : {}),
        },
      };
    } else {
      nextSession.bridgeState = nextBridgeState;
    }

    await this.deps.stateStore.upsertSession(nextSession);
    return nextSession;
  }

  private async clearPendingApprovalIfBypassed(session: SessionBinding): Promise<SessionBinding> {
    if (this.resolveApprovalPolicy(session) !== "never" || !readPendingApproval(session.bridgeState)) {
      return session;
    }

    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: this.buildSessionBridgeState(session),
    };

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.approval.auto-cleared",
      sessionId: session.sessionId,
      reason: "approval-policy-never",
    });

    return nextSession;
  }

  private resolveAliasOrPath(rawPath: string): string {
    const aliasKey = rawPath.trim().toLowerCase();
    const aliasTarget = this.deps.config.repoAliases[aliasKey];
    if (aliasTarget) {
      return assertPathAllowed(aliasTarget, this.deps.config.allowedDirectories);
    }

    return resolveRequestedWorkingDirectory({
      rawPath,
      defaultWorkingDirectory: this.deps.config.defaultWorkingDirectory,
      allowedDirectories: this.deps.config.allowedDirectories,
    });
  }

  private async handleChannelDefaultCommand(
    session: SessionBinding,
    message: IncomingChatMessage,
    command: Extract<BridgeCommand, { kind: "channel-default" }>,
  ): Promise<void> {
    const { provider, channelId } = message.thread;

    if (command.action === "show") {
      const current = await this.deps.stateStore.getChannelDefaultWorkingDirectory(
        provider,
        channelId,
      );
      await this.deps.auditLogger.log({
        type: "bridge.command.channel-default.show",
        sessionId: session.sessionId,
        provider,
        channelId,
        workingDirectory: current ?? "none",
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Channel default working directory: \`${current ?? "none"}\`.`,
      );
      return;
    }

    if (command.action === "clear") {
      await this.deps.stateStore.clearChannelDefaultWorkingDirectory(provider, channelId);
      await this.deps.auditLogger.log({
        type: "bridge.command.channel-default.clear",
        sessionId: session.sessionId,
        provider,
        channelId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        "Channel default working directory cleared. New threads in this channel use their own directory choice.",
      );
      return;
    }

    let workingDirectory: string;
    try {
      workingDirectory = this.resolveAliasOrPath(command.rawPath);
    } catch (error) {
      const text =
        error instanceof Error ? error.message : "Failed to resolve the requested directory.";
      await this.deps.auditLogger.log({
        type: "bridge.command.channel-default.set.failed",
        sessionId: session.sessionId,
        provider,
        channelId,
        rawPath: command.rawPath,
        error: text,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `Channel default not set: ${text}`,
          "Run `@Chat Agent Bridge repos` to see the allowed roots and aliases.",
        ].join("\n"),
      );
      return;
    }

    await this.deps.stateStore.setChannelDefaultWorkingDirectory(
      provider,
      channelId,
      workingDirectory,
    );
    await this.deps.auditLogger.log({
      type: "bridge.command.channel-default.set",
      sessionId: session.sessionId,
      provider,
      channelId,
      rawPath: command.rawPath,
      workingDirectory,
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Channel default working directory set to \`${workingDirectory}\`. New threads in this channel start there.`,
    );
  }

  private resolveApprovalPolicy(session?: SessionBinding): AppConfig["defaultApprovalPolicy"] {
    if (session && !this.resolveSessionAgent(session).capabilities.manualApproval) {
      return "never";
    }

    const override = session ? readApprovalPolicyOverride(session.bridgeState) : undefined;
    if (override) {
      return override;
    }

    return this.deps.config.defaultApprovalPolicy;
  }

  private truncateForCode(text: string, maxLength: number): string {
    const normalized = text.replace(/\s+/g, " ").trim();
    if (normalized.length <= maxLength) {
      return normalized;
    }

    return `${normalized.slice(0, maxLength - 3)}...`;
  }

  private scheduleDetachedRestart(
    channelId?: string,
    threadId?: string,
    options?: { rebuild?: boolean; actor?: string; reason?: string },
  ): void {
    if (this.deps.launchDetachedRestart) {
      this.deps.launchDetachedRestart({
        ...(channelId !== undefined ? { channelId } : {}),
        ...(threadId !== undefined ? { threadId } : {}),
        ...(options?.rebuild !== undefined ? { rebuild: options.rebuild } : {}),
        ...(options?.actor !== undefined ? { actor: options.actor } : {}),
        ...(options?.reason !== undefined ? { reason: options.reason } : {}),
      });
      return;
    }

    const scriptPath = path.resolve(process.cwd(), "scripts", "bridge-restart-detached.ps1");
    const powershellPath = path.join(
      process.env.SystemRoot ?? "C:\\Windows",
      "System32",
      "WindowsPowerShell",
      "v1.0",
      "powershell.exe",
    );
    const orchestratorLog = path.resolve(
      process.cwd(),
      "data",
      "runtime",
      "bridge.orchestrator.log",
    );
    fs.mkdirSync(path.dirname(orchestratorLog), { recursive: true });
    appendRestartLog(
      orchestratorLog,
      `[${new Date().toISOString()}] scheduling detached restart channel=${channelId ?? ""} thread=${threadId ?? ""} rebuild=${options?.rebuild ? "true" : "false"}\n`,
    );
    const helperArgs = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
    if (options?.rebuild) {
      helperArgs.push("-Build");
    }
    if (options?.actor) {
      helperArgs.push("-Actor", options.actor);
    }
    if (options?.reason) {
      helperArgs.push("-Reason", options.reason);
    }
    if (channelId && threadId) {
      helperArgs.push("-ChannelId", channelId, "-ThreadId", threadId);
    }

    const launcherCommand = [
      `$argList = @(${helperArgs.map((arg) => toPowerShellSingleQuoted(arg)).join(", ")});`,
      `Start-Process -WindowStyle Hidden -FilePath ${toPowerShellSingleQuoted(powershellPath)} -ArgumentList $argList;`,
    ].join(" ");

    const child = spawn(
      powershellPath,
      ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", launcherCommand],
      {
        cwd: process.cwd(),
        stdio: "ignore",
        windowsHide: true,
      },
    );

    appendRestartLog(
      orchestratorLog,
      `[${new Date().toISOString()}] detached restart helper launcher spawned pid=${child.pid ?? "unknown"}\n`,
    );

    child.on("error", (error) => {
      const line = `[${new Date().toISOString()}] failed to launch detached restart helper: ${error.message}\n`;
      appendRestartLog(orchestratorLog, line);
    });

    child.unref();
  }

  private buildRestartCompletionText(completion: PendingRestartCompletion): string {
    // Legacy entries (written by old PS1 code) carry a pre-composed text field.
    if (completion.text) {
      return completion.text;
    }

    const lines: string[] = [];

    if (completion.failed) {
      lines.push(`Bridge restart failed: ${completion.errorMessage ?? "unknown error"}`);
    } else {
      lines.push("Bridge restarted successfully. The bridge is back online.");
    }

    if (typeof completion.durationMs === "number") {
      const secs = Math.round(completion.durationMs / 1000);
      lines.push(`• Duration: ${secs}s`);
    }

    const connStatus = this.getChatConnectionStatus();
    if (connStatus) {
      const stateLabel =
        connStatus.connected ? "Connected" : connStatus.state === "connecting" ? "Connecting..." : "Disconnected";
      lines.push(`• Connection: ${connStatus.provider} (${connStatus.mode}) — ${stateLabel}`);
    }

    const uptimeSecs = Math.round((Date.now() - Date.parse(this.startedAt)) / 1000);
    lines.push(`• Uptime: ${uptimeSecs}s`);

    return lines.join("\n");
  }

  private async flushPendingRestartCompletions(): Promise<void> {
    if (this.restartCompletionFlushInFlight) {
      return;
    }

    this.restartCompletionFlushInFlight = true;
    try {
      let raw: string;
      try {
        raw = fs.readFileSync(this.restartCompletionFilePath, "utf8");
      } catch (error) {
        const code =
          error instanceof Error && "code" in error ? String((error as NodeJS.ErrnoException).code) : "";
        if (code === "ENOENT") {
          return;
        }
        throw error;
      }

      const lines = raw
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0);
      if (lines.length === 0) {
        fs.rmSync(this.restartCompletionFilePath, { force: true });
        return;
      }

      const pending: PendingRestartCompletion[] = [];
      let skippedLines = 0;
      for (const line of lines) {
        try {
          const parsed = JSON.parse(line.replace(/^\uFEFF/, "")) as Partial<PendingRestartCompletion>;
          if (
            typeof parsed.channelId === "string" &&
            typeof parsed.threadId === "string" &&
            (typeof parsed.text === "string" || typeof parsed.startedAt === "string")
          ) {
            pending.push({
              channelId: parsed.channelId,
              threadId: parsed.threadId,
              ...(typeof parsed.text === "string" ? { text: parsed.text } : {}),
              ...(typeof parsed.startedAt === "string" ? { startedAt: parsed.startedAt } : {}),
              ...(typeof parsed.durationMs === "number" ? { durationMs: parsed.durationMs } : {}),
              ...(typeof parsed.failed === "boolean" ? { failed: parsed.failed } : {}),
              ...(typeof parsed.errorMessage === "string" ? { errorMessage: parsed.errorMessage } : {}),
              ...(typeof parsed.attempts === "number" ? { attempts: parsed.attempts } : {}),
            });
          } else {
            skippedLines += 1;
          }
        } catch {
          skippedLines += 1;
        }
      }

      if (skippedLines > 0) {
        await this.deps.auditLogger.log({
          type: "bridge.restart.queue.invalid",
          skippedLines,
          file: this.restartCompletionFilePath,
        });
      }

      fs.rmSync(this.restartCompletionFilePath, { force: true });

      const retryQueue: PendingRestartCompletion[] = [];
      let postedCount = 0;
      for (const completion of pending) {
        try {
          const text = this.buildRestartCompletionText(completion);
          await this.deps.chatAdapter.postThreadMessage(
            {
              provider: this.deps.config.chatProvider,
              channelId: completion.channelId,
              threadId: completion.threadId,
            },
            text,
          );
          await this.deps.auditLogger.log({
            type: "bridge.restart.completed",
            channelId: completion.channelId,
            threadId: completion.threadId,
          });
          postedCount += 1;
        } catch (err) {
          const errorMessage = err instanceof Error ? err.message : String(err);

          if (isPermanentChatPostError(errorMessage)) {
            logger.warn("Dropping restart completion; permanent chat error.", {
              channelId: completion.channelId,
              threadId: completion.threadId,
              error: errorMessage,
            });
            continue;
          }

          const attempts = (completion.attempts ?? 0) + 1;
          if (attempts >= MAX_RESTART_COMPLETION_ATTEMPTS) {
            logger.warn("Dropping restart completion; attempt cap reached.", {
              channelId: completion.channelId,
              threadId: completion.threadId,
              error: errorMessage,
            });
            continue;
          }

          logger.warn("Failed to post restart completion; will retry.", {
            channelId: completion.channelId,
            threadId: completion.threadId,
            error: errorMessage,
          });
          retryQueue.push({ ...completion, attempts });
        }
      }

      if (postedCount > 0) {
        await this.deps.auditLogger.log({
          type: "bridge.restart.queue.flushed",
          postedCount,
        });
      }

      if (retryQueue.length > 0) {
        const serialized = retryQueue
          .map((completion) => JSON.stringify(completion))
          .join("\n");
        fs.writeFileSync(
          this.restartCompletionFilePath,
          `${serialized}\n`,
          "utf8",
        );
        await this.deps.auditLogger.log({
          type: "bridge.restart.queue.retry",
          retryCount: retryQueue.length,
        });
      }
    } finally {
      this.restartCompletionFlushInFlight = false;
    }
  }

  private async logExecutionEnvironment(
    session: SessionBinding,
    approvalPolicy: ApprovalPolicy,
  ): Promise<void> {
    const parentEnv = EXECUTION_ENV_AUDIT_KEYS.reduce<Record<string, JsonValue>>((acc, key) => {
      const value = process.env[key];
      if (typeof value !== "string" || value.length === 0) {
        return acc;
      }

      if (key === "PATH" || key === "Path") {
        acc[key] = value.split(path.delimiter).slice(0, 8);
        return acc;
      }

      acc[key] = value;
      return acc;
    }, {});

    await this.deps.auditLogger.log({
      type: "bridge.execution.environment",
      sessionId: session.sessionId,
      workingDirectory: session.workingDirectory,
      approvalPolicy,
      connectionMode: readSessionConnectionMode(session),
      parentEnv,
      childBridgeEnv: {
        CHAT_AGENT_BRIDGE: "1",
        CHAT_AGENT_BRIDGE_SESSION_ID: session.sessionId,
        CHAT_AGENT_BRIDGE_AGENT: session.agent,
        CHAT_AGENT_BRIDGE_CHAT_PROVIDER: session.thread.provider,
        CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID: session.thread.channelId,
        CHAT_AGENT_BRIDGE_CHAT_THREAD_ID: session.thread.threadId,
        CHAT_AGENT_BRIDGE_WORKDIR: session.workingDirectory,
      },
    });
  }
}
