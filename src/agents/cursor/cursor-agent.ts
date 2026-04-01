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

const CANCELLATION_GRACE_PERIOD_MS = 1_000;

function trimProgressText(text: string, maxLength: number): string {
  const normalized = text.replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }

  return `${normalized.slice(0, maxLength - 3)}...`;
}

function readCursorSessionId(agentState: Record<string, JsonValue> | undefined): string | undefined {
  const value = agentState?.cursorSessionId;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function buildCursorCommand(baseCommand: string | undefined, model: string | undefined): string {
  const command = (baseCommand ?? "cursor-agent --trust").trim();
  if (/\s--model(\s|=)/i.test(` ${command}`)) {
    return command;
  }

  const normalizedModel = model?.trim();
  if (!normalizedModel) {
    return command;
  }

  return `${command} --model ${normalizedModel}`;
}

export class CursorAgent implements Agent {
  readonly kind = "cursor" as const;
  readonly capabilities = {
    manualApproval: false,
    writeApprovalRerun: false,
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
    const result = await this.runCursor(request.session, request.prompt, request.onActivity);
    return {
      sessionId: request.session.sessionId,
      summary: result.summary,
      rawOutput: result.rawOutput,
      ...(request.session.agentState ? { agentState: request.session.agentState } : {}),
    };
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

  private async runCursor(
    session: SessionBinding,
    prompt: string,
    onActivity: AgentExecutionRequest["onActivity"],
  ): Promise<{ summary: string; rawOutput: string }> {
    const command = buildCursorCommand(this.config.cursorCommand, this.config.cursorModel ?? "Auto");
    const child = spawn(command, {
      cwd: session.workingDirectory,
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
    let timedOut: "idle" | "max" | undefined;

    const refreshIdleTimeout = () => {
      clearTimeout(idleTimeout);
      idleTimeout = setTimeout(() => {
        timedOut = "idle";
        child.kill();
      }, this.config.cursorCommandTimeoutMs ?? 600000);
    };

    let idleTimeout = setTimeout(() => {
      timedOut = "idle";
      child.kill();
    }, this.config.cursorCommandTimeoutMs ?? 600000);

    const maxTimeout = setTimeout(() => {
      timedOut = "max";
      child.kill();
    }, this.config.cursorMaxRuntimeMs ?? 3600000);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");

    child.stdout.on("data", (chunk: string) => {
      refreshIdleTimeout();
      stdoutChunks.push(chunk);
      const text = trimProgressText(chunk, 160);
      if (!text) {
        return;
      }
      void Promise.resolve(
        onActivity?.({
          stream: "stdout",
          text,
        }),
      ).catch(() => undefined);
    });

    child.stderr.on("data", (chunk: string) => {
      refreshIdleTimeout();
      stderrChunks.push(chunk);
      const text = trimProgressText(chunk, 160);
      if (!text) {
        return;
      }
      const update: AgentProgressUpdate = {
        stream: "stderr",
        text,
        urgent: true,
      };
      void Promise.resolve(onActivity?.(update)).catch(() => undefined);
    });

    child.stdin.write(prompt);
    child.stdin.end();

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

    const stdout = stdoutChunks.join("").trim();
    const stderr = stderrChunks.join("").trim();
    const rawOutput = [stdout, stderr].filter((part) => part.length > 0).join("\n");

    if (completion.type === "cancelled" || activeRun.cancelRequested) {
      throw new AgentExecutionCancelledError();
    }

    if (timedOut === "idle") {
      throw new Error(
        `Cursor agent was idle for ${this.config.cursorCommandTimeoutMs ?? 600000}ms and was stopped.`,
      );
    }

    if (timedOut === "max") {
      throw new Error(
        `Cursor agent exceeded the maximum runtime of ${this.config.cursorMaxRuntimeMs ?? 3600000}ms.`,
      );
    }

    if (completion.exitCode !== 0) {
      throw new Error(`Cursor agent failed with exit code ${completion.exitCode ?? -1}.\n${rawOutput}`);
    }

    const summary = stdout || stderr;
    if (summary.length === 0) {
      throw new Error(
        "Cursor agent completed without returning output. Configure CAB_CURSOR_COMMAND to a non-interactive wrapper if needed.",
      );
    }

    return {
      summary,
      rawOutput,
    };
  }

  private buildBridgeEnvironment(session: SessionBinding): NodeJS.ProcessEnv {
    const cursorSessionId = readCursorSessionId(session.agentState);
    return {
      ...process.env,
      CHAT_AGENT_BRIDGE: "1",
      CHAT_AGENT_BRIDGE_SESSION_ID: session.sessionId,
      CHAT_AGENT_BRIDGE_AGENT: session.agent,
      CHAT_AGENT_BRIDGE_CHAT_PROVIDER: session.thread.provider,
      CHAT_AGENT_BRIDGE_CHAT_CHANNEL_ID: session.thread.channelId,
      CHAT_AGENT_BRIDGE_CHAT_THREAD_ID: session.thread.threadId,
      CHAT_AGENT_BRIDGE_WORKDIR: session.workingDirectory,
      ...(cursorSessionId ? { CHAT_AGENT_BRIDGE_CURSOR_SESSION_ID: cursorSessionId } : {}),
    };
  }
}
