import { Agent, AgentExecutionCancelledError } from "../agents/base.js";
import { ChatAdapter } from "../adapters/chat.js";
import { readLatestCodexSession, readRecentCodexSessions } from "../agents/codex/session-index.js";
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
import {
  BridgeCommand,
  formatRepoAliases,
  parseBridgeCommand,
  resolveRequestedWorkingDirectory,
} from "./commands.js";
import { JobQueue } from "./job-queue.js";
import { SessionManager } from "./session-manager.js";
import {
  AgentProgressUpdate,
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
  agent: Agent;
  auditLogger: AuditLogger;
}

interface SessionPickerState {
  sessionIds: string[];
}

type SessionConnectionMode = "attached" | "bridge-owned";

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

  return typeof session.agentState?.codexThreadId === "string" ? "attached" : "bridge-owned";
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

  return {
    sessionIds: sessionIds as string[],
  };
}

const PROGRESS_UPDATE_INTERVAL_MS = 20_000;
const MAX_PROGRESS_LINES = 4;
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
      elements: [
        {
          type: "button",
          action_id: "cab_approve",
          text: {
            type: "plain_text",
            text: "Approve and rerun",
            emoji: true,
          },
          style: "primary",
          value: "approve",
        },
        {
          type: "button",
          action_id: "cab_reject",
          text: {
            type: "plain_text",
            text: "Reject",
            emoji: true,
          },
          style: "danger",
          value: "reject",
        },
      ],
    },
  ];
}

function buildCancelBlocks(): unknown[] {
  return [
    {
      type: "actions",
      elements: [
        {
          type: "button",
          action_id: "cab_cancel",
          text: {
            type: "plain_text",
            text: "Cancel",
            emoji: true,
          },
          style: "danger",
          value: "cancel",
        },
      ],
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

export class BridgeService {
  private readonly startedAt = new Date().toISOString();

  constructor(private readonly deps: BridgeServiceDeps) {}

  async start(): Promise<void> {
    await this.deps.stateStore.initialize();
    await this.deps.chatAdapter.start(async (message) => this.handleMessage(message));
  }

  private async handleMessage(message: IncomingChatMessage): Promise<void> {
    const session = await this.deps.sessionManager.getOrCreate({
      agent: this.deps.agent.kind,
      thread: message.thread,
      workingDirectory: message.requestedWorkingDirectory,
      allowedDirectories: this.deps.config.allowedDirectories,
    });
    const immediateBridgeCommand = parseBridgeCommand(message.text);
    if (immediateBridgeCommand?.kind === "cancel") {
      await this.handleImmediateBridgeCommand(session, message, immediateBridgeCommand);
      return;
    }

    await this.deps.jobQueue.enqueue(session.sessionId, async () => {
      let currentSession = session;
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

      if (/^\d+$/.test(message.text.trim()) || /^(new|start)$/i.test(message.text.trim())) {
        const handled = await this.handleSessionPickerSelection(currentSession, message);
        if (handled) {
          return;
        }
      }

      let progress: Awaited<ReturnType<BridgeService["createProgressReporter"]>> | undefined;
      let executionRecord: ExecutionRecord | undefined;
      try {
        const approvalPolicy = this.resolveApprovalPolicy(currentSession);
        executionRecord = this.createExecutionRecord({
          session: currentSession,
          prompt: message.text,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
        });
        await this.deps.stateStore.upsertExecutionRecord(executionRecord);
        progress = await this.createProgressReporter(
          currentSession,
          message.thread,
          "running",
          "task started",
        );
        await this.deps.auditLogger.log({
          type: "bridge.execution.started",
          sessionId: currentSession.sessionId,
          workingDirectory: currentSession.workingDirectory,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
          prompt: message.text,
        });

        const result = await this.deps.agent.execute({
          session: currentSession,
          prompt: message.text,
          approvalPolicy,
          networkAccess: this.deps.config.networkAccess,
          allowedDirectories: this.deps.config.allowedDirectories,
          onActivity: progress.onActivity,
        });

        const persistInput: Parameters<BridgeService["persistSessionAfterRun"]>[1] = {
          prompt: message.text,
          resultSummary: result.summary,
        };

        if (result.agentState) {
          persistInput.agentState = result.agentState;
        }

        const needsApproval =
          approvalPolicy !== "never" &&
          this.deps.agent.capabilities.writeApprovalRerun &&
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
        currentSession = await this.persistSessionAfterRun(currentSession, persistInput);
        await progress.finish(
          needsApproval ? "waiting for approval" : "completed",
          needsApproval ? "write approval required" : "completed",
        );
        await this.deps.chatAdapter.postThreadMessage(message.thread, result.summary);
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
            prompt: message.text,
            reason: result.summary,
          });
          await this.deps.chatAdapter.postThreadMessage(
            message.thread,
            [
              "Approval required.",
              `Pending request: \`${this.truncateForCode(message.text, 120)}\``,
              "Reply `@Chat Agent Bridge approve` to rerun it with write access, or `@Chat Agent Bridge reject` to clear it.",
            ].join("\n"),
            {
              blocks: buildApprovalBlocks(),
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
            message.thread,
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
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          `Execution failed for session \`${currentSession.sessionId}\`: ${text}`,
        );
      }
    });
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
          "- `@Chat Agent Bridge repos`",
          "- `@Chat Agent Bridge repo <alias>`",
          "- `@Chat Agent Bridge cwd <path>`",
          "- `@Chat Agent Bridge mode <allow|manual>`",
          "- `@Chat Agent Bridge new <allow|manual>`",
          "- `@Chat Agent Bridge attach <session-id>`",
          "- `@Chat Agent Bridge attach-last`",
          "- `@Chat Agent Bridge detach`",
          "- `@Chat Agent Bridge restart bridge`",
          "- `@Chat Agent Bridge approve`",
          "- `@Chat Agent Bridge reject`",
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "cancel") {
      await this.handleImmediateBridgeCommand(session, message, command);
      return;
    }

    if (command.kind === "status") {
      const pendingApproval = readPendingApproval(session.bridgeState);
      const approvalPolicyOverride = readApprovalPolicyOverride(session.bridgeState);
      const connectionMode = readSessionConnectionMode(session);
      const latestExecution = (
        await this.deps.stateStore.listExecutionRecords({
          sessionId: session.sessionId,
          limit: 1,
        })
      )[0];
      const agentThreadId =
        typeof session.agentState?.codexThreadId === "string"
          ? session.agentState.codexThreadId
          : "none";
      await this.deps.auditLogger.log({
        type: "bridge.command.status",
        sessionId: session.sessionId,
        workingDirectory: session.workingDirectory,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `Session \`${session.sessionId}\``,
          `Agent: \`${this.deps.agent.kind}\``,
          `Slack mode: \`${this.deps.config.slackMode}\``,
          `Bridge started at: \`${this.startedAt}\``,
          `Session connection: \`${connectionMode}\``,
          `Working directory: \`${session.workingDirectory}\``,
          `Approval policy: \`${this.resolveApprovalPolicy(session)}\``,
          `Approval override: \`${approvalPolicyOverride ?? "default"}\``,
          `Network access: \`${this.deps.config.networkAccess}\``,
          `Agent approval support: \`${this.deps.agent.capabilities.manualApproval}\``,
          `Agent write rerun support: \`${this.deps.agent.capabilities.writeApprovalRerun}\``,
          `Agent thread id: \`${agentThreadId}\``,
          `Latest execution: \`${latestExecution?.executionId ?? "none"}\``,
          `Latest status: \`${latestExecution ? formatExecutionStatus(latestExecution.status) : "none"}\``,
          `Pending approval: \`${pendingApproval ? "yes" : "no"}\``,
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

    if (command.kind === "new-session") {
      const nextBridgeState: Record<string, JsonValue> = {};
      const approvalPolicy =
        command.approvalPolicy ?? readApprovalPolicyOverride(session.bridgeState);
      if (approvalPolicy) {
        nextBridgeState.approvalPolicyOverride = approvalPolicy;
      }
      nextBridgeState.connectionMode = "bridge-owned";

      const nextSession: SessionBinding = {
        ...session,
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
          `Bridge session \`${session.sessionId}\` is reset for a fresh Codex session.`,
          `Approval mode: \`${approvalPolicy ?? this.deps.config.defaultApprovalPolicy}\`.`,
          "Send the next instruction to begin.",
        ].join("\n"),
      );
      return;
    }

    if (command.kind === "attach-last") {
      try {
        const latest = await readLatestCodexSession(this.deps.config.codexSessionIndexFile);
        if (!latest) {
          await this.deps.chatAdapter.postThreadMessage(
            message.thread,
            "No Codex app session was found in the local session index.",
          );
          return;
        }

        await this.attachCodexSession(session, message, latest.id, "latest");
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
      await this.attachCodexSession(session, message, command.sessionId, "manual");
      return;
    }

    if (command.kind === "detach") {
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...(session.bridgeState ?? {}),
          connectionMode: "bridge-owned",
        },
        agentState: {},
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.command.detach",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Detached bridge session \`${session.sessionId}\` from any saved Codex thread.`,
      );
      return;
    }

    if (command.kind === "restart-bridge") {
      await this.deps.auditLogger.log({
        type: "bridge.command.restart",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        "Bridge restart requested. I will restart the bridge process now and should be available again in a few seconds.",
      );
      this.scheduleDetachedRestart(message.thread.channelId, message.thread.threadId);
      return;
    }

    if (command.kind === "list-roots") {
      await this.deps.auditLogger.log({
        type: "bridge.command.list-roots",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Allowed working-directory roots:\n${formatRepoAliases(this.deps.config.repoAliases)}`,
      );
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
    const cancelled = await this.deps.agent.cancelExecution(session.sessionId);
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
      typeof session.agentState?.codexThreadId === "string" ? session.agentState.codexThreadId : "";
    if (existingThreadId) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        [
          `This thread is already attached to Codex thread \`${existingThreadId}\`.`,
          "Use `@Chat Agent Bridge status` to inspect it, or send an instruction to continue.",
        ].join("\n"),
      );
      return;
    }

    try {
      const sessions = await readRecentCodexSessions(this.deps.config.codexSessionIndexFile, 5);
      if (sessions.length === 0) {
        await this.deps.chatAdapter.postThreadMessage(
          message.thread,
          "No reusable Codex sessions were found. Reply `new` to start a fresh session.",
        );
        return;
      }

      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: {
          ...(session.bridgeState ?? {}),
          connectionMode: "bridge-owned",
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
        "Choose a Codex session to attach, or reply `new` to start a fresh one:",
        ...sessions.map((candidate, index) => {
          const label = candidate.threadName ? ` - ${candidate.threadName}` : "";
          return `${index + 1}. \`${candidate.id}\`${label}`;
        }),
      ];
      await this.deps.chatAdapter.postThreadMessage(message.thread, lines.join("\n"));
    } catch (error) {
      const text = error instanceof Error ? error.message : "Failed to read local Codex sessions.";
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Failed to list reusable Codex sessions: ${text}`,
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

    const normalized = message.text.trim().toLowerCase();
    if (normalized === "new" || normalized === "start") {
      const nextBridgeState = { ...(session.bridgeState ?? {}) };
      delete nextBridgeState.sessionPicker;
      const nextSession: SessionBinding = {
        ...session,
        updatedAt: new Date().toISOString(),
        bridgeState: nextBridgeState,
      };
      await this.deps.stateStore.upsertSession(nextSession);
      await this.deps.auditLogger.log({
        type: "bridge.session-picker.new",
        sessionId: session.sessionId,
      });
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Starting a fresh Codex session for bridge session \`${session.sessionId}\`. Send the next instruction to begin.`,
      );
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

    await this.attachCodexSession(
      session,
      message,
      picker.sessionIds[selectedIndex - 1] ?? "",
      "picker",
    );
    return true;
  }

  private async updateSessionWorkingDirectory(
    session: SessionBinding,
    message: IncomingChatMessage,
    input: {
      workingDirectory: string;
      source: string;
    },
  ): Promise<void> {
    const nextSession: SessionBinding = {
      ...session,
      workingDirectory: input.workingDirectory,
      updatedAt: new Date().toISOString(),
    };

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.command.set-workdir",
      sessionId: session.sessionId,
      previousWorkingDirectory: session.workingDirectory,
      workingDirectory: input.workingDirectory,
      rawPath: input.source,
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Session \`${session.sessionId}\` is now bound to \`${input.workingDirectory}\`.`,
    );
  }

  private async attachCodexSession(
    session: SessionBinding,
    message: IncomingChatMessage,
    codexThreadId: string,
    source: "latest" | "manual" | "picker",
  ): Promise<void> {
    const nextSession: SessionBinding = {
      ...session,
      updatedAt: new Date().toISOString(),
      bridgeState: {
        connectionMode: "attached",
      },
      agentState: {
        ...(session.agentState ?? {}),
        codexThreadId,
      },
    };

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.command.attach",
      sessionId: session.sessionId,
      codexThreadId,
      source,
    });
    await this.deps.chatAdapter.postThreadMessage(
      message.thread,
      `Bridge session \`${session.sessionId}\` is now attached to Codex thread \`${codexThreadId}\`.`,
    );
  }

  private async handleApprovalMessage(
    session: SessionBinding,
    message: IncomingChatMessage,
  ): Promise<void> {
    if (!this.deps.agent.capabilities.writeApprovalRerun) {
      await this.deps.chatAdapter.postThreadMessage(
        message.thread,
        `Agent \`${this.deps.agent.kind}\` does not support approval reruns.`,
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
      rerunOfExecutionId: pendingApproval.executionId,
    });
    await this.deps.stateStore.upsertExecutionRecord(executionRecord);
    progress = await this.createProgressReporter(
      session,
      message.thread,
      "running",
      "approval accepted; rerunning",
    );
    try {
      const result = await this.deps.agent.execute({
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
    const nextBridgeState: Record<string, JsonValue> = {};
    const approvalPolicyOverride = readApprovalPolicyOverride(session.bridgeState);
    nextBridgeState.connectionMode = readSessionConnectionMode(session);
    if (approvalPolicyOverride) {
      nextBridgeState.approvalPolicyOverride = approvalPolicyOverride;
    }

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
      bridgeState: {
        connectionMode: readSessionConnectionMode(session),
      },
    };

    await this.deps.stateStore.upsertSession(nextSession);
    await this.deps.auditLogger.log({
      type: "bridge.approval.auto-cleared",
      sessionId: session.sessionId,
      reason: "approval-policy-never",
    });

    return nextSession;
  }

  private resolveApprovalPolicy(session?: SessionBinding): AppConfig["defaultApprovalPolicy"] {
    if (!this.deps.agent.capabilities.manualApproval) {
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

  private scheduleDetachedRestart(channelId?: string, threadId?: string): void {
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
    const helperArgs = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
    if (channelId && threadId) {
      helperArgs.push("-ChannelId", channelId, "-ThreadId", threadId);
    }

    const toPowerShellStringLiteral = (value: string) => `'${value.replace(/'/g, "''")}'`;
    const startProcessCommand = [
      `Start-Process -FilePath ${toPowerShellStringLiteral(powershellPath)}`,
      `-ArgumentList @(${helperArgs.map(toPowerShellStringLiteral).join(", ")})`,
      "-WindowStyle Hidden",
    ].join(" ");

    const child = spawn(powershellPath, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", startProcessCommand], {
      cwd: process.cwd(),
      stdio: "ignore",
      windowsHide: true,
    });

    child.on("error", (error) => {
      const line = `[${new Date().toISOString()}] failed to launch detached restart helper: ${error.message}\n`;
      fs.mkdirSync(path.dirname(orchestratorLog), { recursive: true });
      fs.appendFileSync(orchestratorLog, line, "utf8");
    });

    child.unref();
  }
}
