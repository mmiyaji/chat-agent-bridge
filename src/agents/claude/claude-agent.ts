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

interface ClaudeJsonResult {
  type?: string;
  subtype?: string;
  is_error?: boolean;
  result?: string;
  session_id?: string;
}

interface ClaudeMessageContentBlock {
  type?: string;
  text?: string;
  name?: string;
}

interface ClaudeStreamMessage extends ClaudeJsonResult {
  message?: {
    content?: ClaudeMessageContentBlock[] | string;
  };
}

const CANCELLATION_GRACE_PERIOD_MS = 1_000;

function trimProgressText(text: string, maxLength: number): string {
  const normalized = text.replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }

  return `${normalized.slice(0, maxLength - 3)}...`;
}

function isNonEmptyString(value: JsonValue | undefined): value is string {
  return typeof value === "string" && value.length > 0;
}

function parseClaudeJsonResult(stdout: string): ClaudeJsonResult {
  const trimmed = stdout.trim();
  if (trimmed.length === 0) {
    return {};
  }

  try {
    return JSON.parse(trimmed) as ClaudeJsonResult;
  } catch {
    const lines = trimmed.split(/\r?\n/).reverse();
    for (const line of lines) {
      const candidate = line.trim();
      if (!candidate.startsWith("{")) {
        continue;
      }

      try {
        return JSON.parse(candidate) as ClaudeJsonResult;
      } catch {
        continue;
      }
    }
  }

  return {};
}

export function parseClaudeJsonLine(line: string): ClaudeStreamMessage | undefined {
  try {
    return JSON.parse(line) as ClaudeStreamMessage;
  } catch {
    return undefined;
  }
}

export function extractAssistantProgress(payload: ClaudeStreamMessage): string | undefined {
  if (payload.type !== "assistant") {
    return undefined;
  }

  const content = payload.message?.content;
  if (typeof content === "string") {
    return content.trim() || undefined;
  }

  if (!Array.isArray(content)) {
    return undefined;
  }

  const parts = content
    .flatMap((block) => {
      if (block.type === "text" && typeof block.text === "string") {
        const text = block.text.trim();
        return text.length > 0 ? [text] : [];
      }

      if (block.type === "tool_use" && typeof block.name === "string" && block.name.length > 0) {
        return [`Using ${block.name}`];
      }

      return [];
    })
    .filter((part) => part.length > 0);

  if (parts.length === 0) {
    return undefined;
  }

  return parts.join(" | ");
}

export class ClaudeCodeAgent implements Agent {
  readonly kind = "claude-code" as const;
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
    const storedSessionId = this.readSessionId(request.session.agentState);
    const args = storedSessionId
      ? this.buildResumeArgs(storedSessionId, request)
      : this.buildExecArgs(request);

    const result = await this.runClaude(request.session, args, request.prompt, request.onActivity);
    const response: AgentExecutionResult = {
      sessionId: request.session.sessionId,
      summary:
        result.resultText.length > 0
          ? result.resultText
          : "Claude Code completed without returning a final result.",
      rawOutput: result.rawOutput,
    };

    if (result.sessionId) {
      response.agentState = {
        claudeSessionId: result.sessionId,
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
    return [
      "-p",
      "--verbose",
      "--output-format",
      "stream-json",
      ...this.buildMaxTurnsArgs(),
      "--model",
      this.config.claudeModel,
      ...this.buildPermissionArgs(request),
      request.prompt,
    ];
  }

  private buildResumeArgs(sessionId: string, request: AgentExecutionRequest): string[] {
    return [
      "-p",
      "--resume",
      sessionId,
      "--verbose",
      "--output-format",
      "stream-json",
      ...this.buildMaxTurnsArgs(),
      "--model",
      this.config.claudeModel,
      ...this.buildPermissionArgs(request),
      request.prompt,
    ];
  }

  private buildMaxTurnsArgs(): string[] {
    if (this.config.claudeMaxTurns <= 0) {
      return [];
    }
    return ["--max-turns", String(this.config.claudeMaxTurns)];
  }

  private buildPermissionArgs(request: AgentExecutionRequest): string[] {
    if (request.approvalPolicy === "manual") {
      return ["--permission-mode", "plan"];
    }

    if (request.approvalPolicy === "on-failure") {
      return ["--permission-mode", "acceptEdits"];
    }

    return ["--dangerously-skip-permissions"];
  }

  private async runClaude(
    session: SessionBinding,
    args: string[],
    _prompt: string,
    onActivity: AgentExecutionRequest["onActivity"],
  ): Promise<{
    sessionId?: string;
    resultText: string;
    rawOutput: string;
  }> {
    const child = spawn("claude", args, {
      cwd: session.workingDirectory,
      stdio: ["ignore", "pipe", "pipe"],
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
    let stdoutBuffer = "";
    let streamedResult: ClaudeJsonResult | undefined;
    let streamedSessionId: string | undefined;
    let timedOut: "idle" | "max" | undefined;

    const refreshIdleTimeout = () => {
      clearTimeout(idleTimeout);
      idleTimeout = setTimeout(() => {
        timedOut = "idle";
        child.kill();
      }, this.config.claudeCommandTimeoutMs);
    };

    let idleTimeout = setTimeout(() => {
      timedOut = "idle";
      child.kill();
    }, this.config.claudeCommandTimeoutMs);

    const maxTimeout = setTimeout(() => {
      timedOut = "max";
      child.kill();
    }, this.config.claudeMaxRuntimeMs);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");

    child.stdout.on("data", (chunk: string) => {
      refreshIdleTimeout();
      stdoutChunks.push(chunk);
      stdoutBuffer += chunk;
      const lines = stdoutBuffer.split(/\r?\n/);
      stdoutBuffer = lines.pop() ?? "";

      for (const rawLine of lines) {
        const line = rawLine.trim();
        if (line.length === 0) {
          continue;
        }

        const payload = parseClaudeJsonLine(line);
        if (!payload) {
          void Promise.resolve(
            onActivity?.({
              stream: "stdout",
              text: trimProgressText(line, 160),
            }),
          ).catch(() => undefined);
          continue;
        }

        if (typeof payload.session_id === "string" && payload.session_id.length > 0) {
          streamedSessionId = payload.session_id;
        }

        if (payload.type === "result") {
          streamedResult = payload;
        }

        const progressText = extractAssistantProgress(payload);
        if (!progressText) {
          continue;
        }

        void Promise.resolve(
          onActivity?.({
            stream: "stdout",
            text: trimProgressText(progressText, 160),
          }),
        ).catch(() => undefined);
      }
    });

    child.stderr.on("data", (chunk: string) => {
      refreshIdleTimeout();
      stderrChunks.push(chunk);
      const update: AgentProgressUpdate = {
        stream: "stderr",
        text: trimProgressText(chunk, 160),
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
    const trailingLine = stdoutBuffer.trim();
    if (!streamedResult && trailingLine.length > 0) {
      const payload = parseClaudeJsonLine(trailingLine);
      if (payload?.type === "result") {
        streamedResult = payload;
      }
      if (!streamedSessionId && typeof payload?.session_id === "string" && payload.session_id.length > 0) {
        streamedSessionId = payload.session_id;
      }
    }

    if (completion.type === "cancelled" || activeRun.cancelRequested) {
      throw new AgentExecutionCancelledError();
    }

    const exitCode = completion.exitCode;

    if (timedOut === "idle") {
      throw new Error(
        `Claude Code CLI was idle for ${this.config.claudeCommandTimeoutMs}ms and was stopped.`,
      );
    }

    if (timedOut === "max") {
      throw new Error(
        `Claude Code CLI exceeded the maximum runtime of ${this.config.claudeMaxRuntimeMs}ms.`,
      );
    }

    if (exitCode !== 0) {
      throw new Error(`Claude Code CLI failed with exit code ${exitCode ?? -1}.\n${rawOutput}`);
    }

    const payload = streamedResult ?? parseClaudeJsonResult(stdout);
    if (payload.is_error) {
      throw new Error(payload.result || "Claude Code returned an error result.");
    }

    const sessionId = payload.session_id ?? streamedSessionId;
    let resultText = typeof payload.result === "string" ? payload.result : "";
    if (payload.subtype === "max_turns") {
      resultText = `⚠ Max turns reached (task may be incomplete).\n\n${resultText}`;
    }

    return {
      ...(typeof sessionId === "string"
        ? {
            sessionId,
          }
        : {}),
      resultText,
      rawOutput,
    };
  }

  private readSessionId(agentState: Record<string, JsonValue> | undefined): string | undefined {
    const value = agentState?.claudeSessionId;
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
