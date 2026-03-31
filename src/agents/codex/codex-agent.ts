import process from "node:process";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { Agent, AgentExecutionCancelledError } from "../base.js";
import {
  AgentExecutionRequest,
  AgentExecutionResult,
  AgentProgressUpdate,
  AppConfig,
  JsonValue,
  SessionBinding,
} from "../../core/types.js";

interface CodexJsonEvent {
  type?: string;
  thread_id?: string;
  item?: {
    type?: string;
    text?: string;
  };
}

const CANCELLATION_GRACE_PERIOD_MS = 1_000;

const FILTERED_STDERR_SUBSTRINGS = [
  "failed to warm featured plugin ids cache",
  "startup remote plugin sync failed",
  "remote plugin sync request to https://chatgpt.com/backend-api/plugins/",
  "Shell snapshot not supported yet for PowerShell",
  "Transport channel closed, when AuthRequired(",
] as const;

function trimProgressText(text: string, maxLength: number): string {
  const normalized = text.replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }

  return `${normalized.slice(0, maxLength - 3)}...`;
}

function summarizeStdoutChunk(chunk: string): AgentProgressUpdate[] {
  const lines: AgentProgressUpdate[] = [];

  for (const line of chunk.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed.length === 0) {
      continue;
    }

    if (!trimmed.startsWith("{")) {
      continue;
    }

    try {
      const event = JSON.parse(trimmed) as CodexJsonEvent;
      if (event.type === "item.completed" && event.item?.type === "agent_message" && event.item.text) {
        lines.push({
          stream: "stdout",
          text: `Assistant: ${trimProgressText(event.item.text, 160)}`,
          urgent: true,
        });
        continue;
      }
    } catch {
      continue;
    }
  }

  return lines.slice(-4);
}

function isNonEmptyString(value: JsonValue | undefined): value is string {
  return typeof value === "string" && value.length > 0;
}

function sanitizeStderrChunk(chunk: string): string {
  if (FILTERED_STDERR_SUBSTRINGS.some((needle) => chunk.includes(needle))) {
    return "";
  }

  return chunk;
}

export class CodexAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;
  private readonly activeRuns = new Map<
    string,
    {
      child: ReturnType<typeof spawn>;
      cancelRequested: boolean;
      resolveCancellation: () => void;
      cancellationTimer?: NodeJS.Timeout;
    }
  >();

  constructor(private readonly config: AppConfig) {}

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    const storedThreadId = this.readThreadId(request.session.agentState);
    const args = storedThreadId
      ? this.buildResumeArgs(storedThreadId, request)
      : this.buildExecArgs(request);

    const result = await this.runCodex(
      request.session,
      args,
      request.prompt,
      request.onActivity,
    );
    const summary =
      result.messages.at(-1) ??
      "Codex completed without returning a final agent message.";
    const response: AgentExecutionResult = {
      sessionId: request.session.sessionId,
      summary,
      rawOutput: result.rawOutput,
    };

    if (result.threadId) {
      response.agentState = {
        codexThreadId: result.threadId,
      };
    } else if (request.session.agentState) {
      response.agentState = request.session.agentState;
    }

    return response;
  }

  async cancelExecution(sessionId: string): Promise<boolean> {
    const activeRun = this.activeRuns.get(sessionId);
    if (!activeRun) {
      return false;
    }

    activeRun.cancelRequested = true;
    if (!activeRun.cancellationTimer) {
      activeRun.cancellationTimer = setTimeout(() => {
        activeRun.resolveCancellation();
      }, CANCELLATION_GRACE_PERIOD_MS);
    }
    const pid = activeRun.child.pid;
    if (typeof pid === "number" && Number.isFinite(pid)) {
      if (process.platform === "win32") {
        void spawn("taskkill", ["/PID", String(pid), "/T", "/F"], {
          stdio: "ignore",
          windowsHide: true,
        }).on("error", () => {
          activeRun.child.kill();
        });
      } else {
        activeRun.child.kill("SIGTERM");
      }
      return true;
    }

    activeRun.child.kill();
    return true;
  }

  private buildExecArgs(request: AgentExecutionRequest): string[] {
    const args = [
      "exec",
      "--json",
      "--cd",
      request.session.workingDirectory,
      "--model",
      this.config.codexModel,
      ...this.buildExecutionModeArgs(request),
      "-",
    ];

    return args;
  }

  private buildResumeArgs(threadId: string, request: AgentExecutionRequest): string[] {
    const modeArgs =
      request.approvalPolicy === "manual" ? [] : ["--full-auto"];

    return [
      "exec",
      "resume",
      "--json",
      "--model",
      this.config.codexModel,
      ...modeArgs,
      threadId,
      "-",
    ];
  }

  private buildExecutionModeArgs(request: AgentExecutionRequest): string[] {
    if (request.approvalPolicy === "manual") {
      return ["--sandbox", "read-only"];
    }

    return ["--full-auto"];
  }

  private async runCodex(
    session: SessionBinding,
    args: string[],
    prompt: string,
    onActivity: AgentExecutionRequest["onActivity"],
  ): Promise<{
    threadId?: string;
    messages: string[];
    rawOutput: string;
  }> {
    const child = spawn("codex", args, {
      cwd: process.cwd(),
      stdio: ["pipe", "pipe", "pipe"],
      shell: true,
      windowsHide: true,
      env: this.buildBridgeEnvironment(session),
    });
    const activeRun: {
      child: ReturnType<typeof spawn>;
      cancelRequested: boolean;
      resolveCancellation: () => void;
      cancellationTimer?: NodeJS.Timeout;
    } = {
      child,
      cancelRequested: false,
      resolveCancellation: () => {},
    };
    const cancellationPromise = new Promise<"cancelled">((resolve) => {
      activeRun.resolveCancellation = () => resolve("cancelled");
    });
    this.activeRuns.set(session.sessionId, activeRun);

    const stdoutChunks: string[] = [];
    const stderrChunks: string[] = [];
    const messages: string[] = [];
    let threadId: string | undefined;
    let timedOut: "idle" | "max" | undefined;

    const refreshIdleTimeout = () => {
      clearTimeout(idleTimeout);
      idleTimeout = setTimeout(() => {
        timedOut = "idle";
        child.kill();
      }, this.config.codexCommandTimeoutMs);
    };

    let idleTimeout = setTimeout(() => {
      timedOut = "idle";
      child.kill();
    }, this.config.codexCommandTimeoutMs);

    const maxTimeout = setTimeout(() => {
      timedOut = "max";
      child.kill();
    }, this.config.codexMaxRuntimeMs);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdin.setDefaultEncoding("utf8");
    child.stdin.end(prompt);

    child.stdout.on("data", (chunk: string) => {
      refreshIdleTimeout();
      stdoutChunks.push(chunk);
      const summaries = summarizeStdoutChunk(chunk);
      if (summaries.length === 0) {
        void Promise.resolve(onActivity?.()).catch(() => undefined);
      } else {
        for (const summary of summaries) {
          void Promise.resolve(onActivity?.(summary)).catch(() => undefined);
        }
      }

      for (const line of chunk.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed.startsWith("{")) {
          continue;
        }

        try {
          const event = JSON.parse(trimmed) as CodexJsonEvent;
          if (event.type === "thread.started" && event.thread_id) {
            threadId = event.thread_id;
          }

          if (event.type === "item.completed" && event.item?.type === "agent_message") {
            if (typeof event.item.text === "string" && event.item.text.length > 0) {
              messages.push(event.item.text);
            }
          }
        } catch {
          continue;
        }
      }
    });

    child.stderr.on("data", (chunk: string) => {
      refreshIdleTimeout();
      const sanitized = sanitizeStderrChunk(chunk);
      if (sanitized.length === 0) {
        void Promise.resolve(onActivity?.()).catch(() => undefined);
        return;
      }

      stderrChunks.push(sanitized);
      const update: AgentProgressUpdate = {
        stream: "stderr",
        text: trimProgressText(sanitized, 160),
        urgent: true,
      };
      void Promise.resolve(onActivity?.(update)).catch(() => undefined);
    });

    const completion = await Promise.race([
      once(child, "close").then(([exitCode]) => ({
        type: "close" as const,
        exitCode: exitCode as number | null,
      })),
      cancellationPromise.then(() => ({
        type: "cancelled" as const,
      })),
    ]);
    clearTimeout(idleTimeout);
    clearTimeout(maxTimeout);
    clearTimeout(activeRun.cancellationTimer);
    this.activeRuns.delete(session.sessionId);

    const stdout = stdoutChunks.join("");
    const stderr = stderrChunks.join("");
    const rawOutput = [stdout.trim(), stderr.trim()].filter((part) => part.length > 0).join("\n");

    if (completion.type === "cancelled" || activeRun.cancelRequested) {
      throw new AgentExecutionCancelledError();
    }

    const exitCode = completion.exitCode;

    if (timedOut === "idle") {
      throw new Error(
        `Codex CLI was idle for ${this.config.codexCommandTimeoutMs}ms and was stopped.`,
      );
    }

    if (timedOut === "max") {
      throw new Error(
        `Codex CLI exceeded the maximum runtime of ${this.config.codexMaxRuntimeMs}ms.`,
      );
    }

    if (exitCode !== 0) {
      throw new Error(`Codex CLI failed with exit code ${exitCode ?? -1}.\n${rawOutput}`);
    }

    const result: {
      threadId?: string;
      messages: string[];
      rawOutput: string;
    } = {
      messages,
      rawOutput,
    };

    if (threadId) {
      result.threadId = threadId;
    }

    return result;
  }

  private readThreadId(agentState: Record<string, JsonValue> | undefined): string | undefined {
    const value = agentState?.codexThreadId;
    return isNonEmptyString(value) ? value : undefined;
  }

  private buildBridgeEnvironment(session: SessionBinding): NodeJS.ProcessEnv {
    return {
      ...process.env,
      CHAT_AGENT_BRIDGE: "1",
      CHAT_AGENT_BRIDGE_SESSION_ID: session.sessionId,
      CHAT_AGENT_BRIDGE_AGENT: session.agent,
      CHAT_AGENT_BRIDGE_CHAT_PROVIDER: session.thread.provider,
      CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID: session.thread.channelId,
      CHAT_AGENT_BRIDGE_CHAT_THREAD_ID: session.thread.threadId,
      CHAT_AGENT_BRIDGE_WORKDIR: session.workingDirectory,
    };
  }
}
