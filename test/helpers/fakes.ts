import {
  AgentExecutionRequest,
  AgentExecutionResult,
  ChatThreadRef,
  ExecutionRecord,
  SessionBinding,
} from "../../src/core/types.js";
import { Agent, AgentExecutionCancelledError } from "../../src/agents/base.js";
import {
  ChatAdapter,
  ChatConnectionStatus,
  ChatMessageHandler,
  ChatMessageOptions,
} from "../../src/adapters/chat.js";
import { AuditEvent, AuditLogger } from "../../src/core/audit.js";
import { JobQueue } from "../../src/core/job-queue.js";
import { StateStore } from "../../src/state/state-store.js";

export class InMemoryStateStore implements StateStore {
  private readonly sessions = new Map<string, SessionBinding>();
  private readonly executionRecords = new Map<string, ExecutionRecord>();
  private readonly channelDefaults = new Map<string, string>();

  async initialize(): Promise<void> {}

  async findSessionByThread(thread: ChatThreadRef): Promise<SessionBinding | undefined> {
    return [...this.sessions.values()].find((session) => {
      return (
        session.thread.provider === thread.provider &&
        session.thread.channelId === thread.channelId &&
        session.thread.threadId === thread.threadId
      );
    });
  }

  async upsertSession(session: SessionBinding): Promise<void> {
    this.sessions.set(session.sessionId, session);
  }

  async listSessions(): Promise<SessionBinding[]> {
    return [...this.sessions.values()];
  }

  async upsertExecutionRecord(record: ExecutionRecord): Promise<void> {
    this.executionRecords.set(record.executionId, record);
  }

  async listExecutionRecords(input?: {
    sessionId?: string;
    limit?: number;
  }): Promise<ExecutionRecord[]> {
    const filtered = [...this.executionRecords.values()]
      .filter((record) => !input?.sessionId || record.sessionId === input.sessionId)
      .sort((left, right) => right.createdAt.localeCompare(left.createdAt));

    if (!input?.limit || input.limit < 1) {
      return filtered;
    }

    return filtered.slice(0, input.limit);
  }

  async getChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
  ): Promise<string | undefined> {
    return this.channelDefaults.get(`${provider}:${channelId}`);
  }

  async setChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
    workingDirectory: string,
  ): Promise<void> {
    this.channelDefaults.set(`${provider}:${channelId}`, workingDirectory);
  }

  async clearChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
  ): Promise<void> {
    this.channelDefaults.delete(`${provider}:${channelId}`);
  }

  async listChannelDefaultWorkingDirectories(): Promise<Record<string, string>> {
    return Object.fromEntries(this.channelDefaults.entries());
  }
}

export class RecordingChatAdapter implements ChatAdapter {
  handler?: ChatMessageHandler;
  connectionStatus: ChatConnectionStatus = {
    provider: "slack",
    mode: "socket",
    state: "connected",
    connected: true,
    statusText: "Connected to Slack via Socket Mode.",
    lastChangedAt: "2026-03-30T10:00:00.000Z",
    lastConnectedAt: "2026-03-30T10:00:00.000Z",
    lastRecoveredAt: "2026-03-30T10:05:00.000Z",
    lastOutageDurationMs: 120000,
    reconnectCount: 2,
    disconnectCount: 2,
  };
  readonly startedThreads: Array<{ channelId: string; text: string; options?: ChatMessageOptions }> = [];
  readonly posted: Array<{ thread: ChatThreadRef; text: string; options?: ChatMessageOptions }> = [];
  readonly updated: Array<{
    thread: ChatThreadRef;
    messageId: string;
    text: string;
    options?: ChatMessageOptions;
  }> = [];
  private nextMessageId = 1;

  async start(handler: ChatMessageHandler): Promise<void> {
    this.handler = handler;
  }

  getConnectionStatus(): ChatConnectionStatus {
    return { ...this.connectionStatus };
  }

  async startNewThread(
    channelId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<ChatThreadRef | undefined> {
    this.startedThreads.push({ channelId, text, options });
    const threadId = `new-thread-${this.nextMessageId}`;
    this.nextMessageId += 1;
    return {
      provider: "slack",
      channelId,
      threadId,
    };
  }

  async postThreadMessage(
    thread: ChatThreadRef,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<string> {
    this.posted.push({ thread, text, options });
    const messageId = `message-${this.nextMessageId}`;
    this.nextMessageId += 1;
    return messageId;
  }

  async updateThreadMessage(
    thread: ChatThreadRef,
    messageId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<void> {
    this.updated.push({ thread, messageId, text, options });
  }

  async stop(): Promise<void> {}
}

export class InlineJobQueue implements JobQueue {
  async enqueue<T>(_sessionId: string, job: () => Promise<T>): Promise<T> {
    return job();
  }
}

export class RecordingAuditLogger implements AuditLogger {
  readonly events: AuditEvent[] = [];

  async log(event: AuditEvent): Promise<void> {
    this.events.push(event);
  }
}

export class RecordingAgent implements Agent {
  readonly kind: Agent["kind"];
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;
  readonly requests: AgentExecutionRequest[] = [];
  result: AgentExecutionResult = {
    sessionId: "session",
    summary: "done",
  };
  activityBursts = 0;
  stallExecution = false;
  error?: Error;
  readonly cancelRequests: string[] = [];
  private pendingReject?: (error: Error) => void;

  constructor(kind: Agent["kind"] = "codex") {
    this.kind = kind;
  }

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    this.requests.push(request);
    if (this.error) {
      throw this.error;
    }

    for (let index = 0; index < this.activityBursts; index += 1) {
      await request.onActivity?.();
    }

    if (this.stallExecution) {
      return await new Promise<AgentExecutionResult>((_resolve, reject) => {
        this.pendingReject = reject;
      });
    }

    return {
      ...this.result,
      sessionId: request.session.sessionId,
    };
  }

  async cancelExecution(sessionId: string): Promise<boolean> {
    this.cancelRequests.push(sessionId);
    if (!this.pendingReject) {
      return false;
    }

    const reject = this.pendingReject;
    this.pendingReject = undefined;
    reject(new AgentExecutionCancelledError());
    return true;
  }
}
