import process from "node:process";
import fs from "node:fs";
import path from "node:path";
import { Agent, AgentExecutionCancelledError } from "../base.js";
import type {
  AgentExecutionRequest,
  AgentExecutionRequestCallbackRequest,
  AgentExecutionResult,
  AgentProgressUpdate,
  AppConfig,
  JsonValue,
} from "../../core/types.js";
import { resolveCommand } from "../../core/command-resolution.js";
import { findInstalledCodexExecutable } from "./codex-installation.js";
import { CodexAppServerClient, type AppServerClientOptions, type AppServerMessage } from "./app-server-client.js";

export interface CodexAppServerProbe {
  authenticated: boolean;
  model?: string;
  statusText?: string;
}

type TurnCompletion = { turn?: { id?: string; status?: string; error?: { message?: string }; items?: unknown[] } };
type ActiveRun = {
  client: CodexAppServerClient;
  threadId?: string;
  turnId?: string;
  cancelRequested: boolean;
  stopReason?: "cancelled" | "idle" | "max";
  resolveStop: () => void;
  stopPromise: Promise<void>;
  interruptPromise?: Promise<void>;
  timers: Set<NodeJS.Timeout>;
};

const CLIENT_INFO = { name: "chat-agent-bridge", title: "Chat Agent Bridge", version: "0.1.0" };
const APP_SERVER_REQUEST_TIMEOUT_MS = 15_000;

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function shortText(value: unknown, limit = 180): string {
  const text = typeof value === "string" ? value.replace(/\s+/g, " ").trim() : "";
  return text.length > limit ? `${text.slice(0, limit - 3)}...` : text;
}

function valueAsRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function finalMessage(items: unknown[]): string | undefined {
  return items
    .map(valueAsRecord)
    .filter((item) => item.type === "agentMessage" && nonEmptyString(item.text))
    .map((item) => String(item.text).trim())
    .at(-1);
}

export class CodexAppServerAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = { manualApproval: true, writeApprovalRerun: false, networkAccessControl: true } as const;
  private readonly activeRuns = new Map<string, ActiveRun>();
  private readonly clients = new Set<CodexAppServerClient>();

  constructor(
    private readonly config: AppConfig,
    private readonly clientOptions: Partial<Pick<AppServerClientOptions, "command" | "args" | "spawnProcess">> = {},
  ) {}

  async probe(): Promise<CodexAppServerProbe> {
    const client = this.createClient(this.config.defaultWorkingDirectory);
    try {
      await this.initialize(client);
      const [accountValue, modelsValue] = await Promise.all([
        client.request("account/read", { refreshToken: false }),
        client.request("model/list", { limit: 100, includeHidden: false }),
      ]);
      const accountResult = valueAsRecord(accountValue);
      const account = valueAsRecord(accountResult.account);
      const requiresAuth = accountResult.requiresOpenaiAuth === true;
      const modelsResult = valueAsRecord(modelsValue);
      const models = Array.isArray(modelsResult.data) ? modelsResult.data.map(valueAsRecord) : [];
      const defaultModel = models.find((model) => model.isDefault === true);
      const defaultModelId = defaultModel?.model ?? defaultModel?.id;
      const explicitModel = this.config.codexModel && this.config.codexModel !== "auto" ? this.config.codexModel : undefined;
      const probe: CodexAppServerProbe = {
        authenticated: !requiresAuth || Object.keys(account).length > 0,
        ...(explicitModel ? { model: explicitModel } : nonEmptyString(defaultModelId) ? { model: defaultModelId } : {}),
      };
      const accountType = nonEmptyString(account.type) ? account.type : undefined;
      const email = nonEmptyString(account.email) ? account.email : undefined;
      probe.statusText = probe.authenticated
        ? `Connected${accountType ? ` (${accountType}${email ? `, ${email}` : ""})` : ""}.`
        : "Codex is not authenticated. Sign in with the Codex CLI, then retry.";
      return probe;
    } finally {
      this.clients.delete(client);
      await client.dispose();
    }
  }

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    const client = this.createClient(request.session.workingDirectory);
    const active: ActiveRun = {
      client,
      cancelRequested: false,
      resolveStop: () => {},
      stopPromise: Promise.resolve(),
      timers: new Set(),
    };
    active.stopPromise = new Promise<void>((resolve) => { active.resolveStop = resolve; });
    this.activeRuns.set(request.session.sessionId, active);
    let idleTimer: NodeJS.Timeout | undefined;
    let maxTimer: NodeJS.Timeout | undefined;
    const clearTimer = (timer: NodeJS.Timeout | undefined) => {
      if (timer) { clearTimeout(timer); active.timers.delete(timer); }
    };
    const interrupt = (reason: NonNullable<ActiveRun["stopReason"]>) => {
      if (!active.stopReason) active.stopReason = reason;
      active.interruptPromise ??= (async () => {
        if (active.threadId && active.turnId) {
          try { await client.request("turn/interrupt", { threadId: active.threadId, turnId: active.turnId }); }
          catch { /* A completed/closed turn needs no further action. */ }
        }
        active.resolveStop();
      })();
      return active.interruptPromise;
    };
    const resetIdle = () => {
      clearTimer(idleTimer);
      idleTimer = setTimeout(() => { void interrupt("idle"); }, this.config.codexCommandTimeoutMs);
      active.timers.add(idleTimer);
    };

    try {
      await this.initialize(client);
      const storedThreadId = request.session.agentState?.codexThreadId;
      const threadId = nonEmptyString(storedThreadId) ? storedThreadId : undefined;
      const threadParams = this.buildThreadParams(request);
      const threadResponse = valueAsRecord(await client.request(
        threadId ? "thread/resume" : "thread/start",
        threadId ? { ...threadParams, threadId } : threadParams,
      ));
      const thread = valueAsRecord(threadResponse.thread);
      const actualThreadId = thread.id;
      if (!nonEmptyString(actualThreadId)) throw new Error("Codex App Server did not return a thread id.");
      active.threadId = actualThreadId;
      if (active.cancelRequested) {
        await interrupt("cancelled");
        throw new AgentExecutionCancelledError();
      }

      let lastMessage: string | undefined;
      let rawOutput = "";
      const appendRawOutput = (text: string) => {
        rawOutput = (rawOutput + text).slice(-Math.max(1, this.config.codexMaxOutputLength));
      };
      let completionResolve!: (value: TurnCompletion) => void;
      const completionPromise = new Promise<TurnCompletion>((resolve) => { completionResolve = resolve; });
      client.onNotification((method, params) => {
        resetIdle();
        const paramsThreadId = params.threadId;
        if (nonEmptyString(paramsThreadId) && paramsThreadId !== actualThreadId) return;
        if (method === "turn/started") {
          const turn = valueAsRecord(params.turn);
          if (nonEmptyString(turn.id)) active.turnId = turn.id;
          return;
        }
        if (method === "turn/completed") {
          const turn = valueAsRecord(params.turn);
          if (nonEmptyString(turn.id)) active.turnId = turn.id;
          completionResolve({ turn: turn as NonNullable<TurnCompletion["turn"]> });
          return;
        }
        const item = valueAsRecord(params.item);
        if (method === "item/completed" && item.type === "agentMessage" && nonEmptyString(item.text)) {
          lastMessage = item.text.slice(-Math.max(1, this.config.codexMaxOutputLength));
        }
        let update: AgentProgressUpdate | undefined;
        if (method === "item/agentMessage/delta" && nonEmptyString(params.delta)) {
          appendRawOutput(params.delta);
          update = { stream: "stdout", text: shortText(params.delta), urgent: true };
        } else if (method === "item/commandExecution/outputDelta" && nonEmptyString(params.delta)) {
          appendRawOutput(params.delta);
          update = { stream: "stdout", text: shortText(params.delta), urgent: false };
        } else if (method === "item/started" && nonEmptyString(item.type)) {
          update = { stream: "stdout", text: `Codex: ${item.type}`, urgent: false };
        }
        void Promise.resolve(request.onActivity?.(update)).catch(() => undefined);
      });
      client.onRequest((message) => this.handleServerRequest(request, message));

      const turnParams = this.buildTurnParams(request, actualThreadId);
      const turnResponse = valueAsRecord(await client.request("turn/start", turnParams));
      const turn = valueAsRecord(turnResponse.turn);
      if (!nonEmptyString(turn.id)) throw new Error("Codex App Server did not return a turn id.");
      active.turnId = turn.id;
      if (active.cancelRequested) await interrupt("cancelled");
      resetIdle();
      maxTimer = setTimeout(() => { void interrupt("max"); }, this.config.codexMaxRuntimeMs);
      active.timers.add(maxTimer);

      const outcome = await Promise.race([
        completionPromise.then((value) => ({ type: "completed" as const, value })),
        active.stopPromise.then(() => ({ type: "stopped" as const })),
        client.whenClosed().then((closed) => ({ type: "closed" as const, closed })),
      ]);
      if (outcome.type === "closed") {
        throw new Error(`Codex App Server exited before completing the turn (${outcome.closed?.code ?? outcome.closed?.signal ?? "timeout"}).`);
      }
      if (outcome.type === "stopped") {
        if (active.stopReason === "cancelled" || active.cancelRequested) throw new AgentExecutionCancelledError();
        if (active.stopReason === "idle") throw new Error(`Codex App Server was idle for ${this.config.codexCommandTimeoutMs}ms and was interrupted.`);
        throw new Error(`Codex App Server exceeded the maximum runtime of ${this.config.codexMaxRuntimeMs}ms and was interrupted.`);
      }
      const completedTurn = outcome.value.turn ?? {};
      if (completedTurn.status === "failed") {
        const error = valueAsRecord(completedTurn.error);
        throw new Error(`Codex App Server turn failed: ${shortText(error.message ?? "unknown error")}`);
      }
      if (completedTurn.status === "interrupted") {
        if (active.cancelRequested) throw new AgentExecutionCancelledError();
        throw new Error("Codex App Server turn was interrupted.");
      }
      const summary = finalMessage(completedTurn.items ?? []) ?? lastMessage ?? (rawOutput.trim() || "Codex completed without returning a final agent message.");
      const result: AgentExecutionResult = { sessionId: request.session.sessionId, summary, rawOutput };
      result.agentState = { ...(request.session.agentState ?? {}), codexThreadId: actualThreadId } as Record<string, JsonValue>;
      return result;
    } finally {
      clearTimer(idleTimer);
      clearTimer(maxTimer);
      for (const timer of active.timers) clearTimeout(timer);
      this.activeRuns.delete(request.session.sessionId);
      this.clients.delete(client);
      await client.dispose();
    }
  }

  async cancelExecution(sessionId: string): Promise<boolean> {
    const active = this.activeRuns.get(sessionId);
    if (!active) return false;
    active.cancelRequested = true;
    if (active.threadId && active.turnId) void this.interruptActive(active);
    else active.stopReason = "cancelled";
    return true;
  }

  async dispose(): Promise<void> {
    await Promise.all([...this.clients].map(async (client) => {
      this.clients.delete(client);
      await client.dispose();
    }));
  }

  private async interruptActive(active: ActiveRun): Promise<void> {
    if (active.interruptPromise) return active.interruptPromise;
    active.stopReason = "cancelled";
    active.interruptPromise = (async () => {
      if (active.threadId && active.turnId) {
        try { await active.client.request("turn/interrupt", { threadId: active.threadId, turnId: active.turnId }); }
        catch { /* The process may have exited between lookup and interrupt. */ }
      }
      active.resolveStop();
    })();
    await active.interruptPromise;
  }

  private createClient(cwd: string): CodexAppServerClient {
    let command = this.clientOptions.command;
    if (!command) {
      if (process.platform === "win32") {
        const resolved = resolveCommand("codex.exe", cwd);
        command = resolved.status === "resolved"
          ? resolved.resolvedPath ?? "codex.exe"
          : findInstalledCodexExecutable(process.env.LOCALAPPDATA) ?? "codex.exe";
      } else {
        command = "codex";
      }
    }
    const client = new CodexAppServerClient({
      command,
      args: this.clientOptions.args ?? ["app-server", "--listen", "stdio://"],
      cwd,
      env: {
        ...process.env,
        CHAT_AGENT_BRIDGE: "1",
      },
      requestTimeoutMs: APP_SERVER_REQUEST_TIMEOUT_MS,
      ...(this.clientOptions.spawnProcess ? { spawnProcess: this.clientOptions.spawnProcess } : {}),
    });
    this.clients.add(client);
    return client;
  }

  private async initialize(client: CodexAppServerClient): Promise<void> {
    await client.request("initialize", { clientInfo: CLIENT_INFO, capabilities: { experimentalApi: true } });
    client.notify("initialized", {});
  }

  private buildThreadParams(request: AgentExecutionRequest): Record<string, unknown> {
    const model = this.config.codexModel && this.config.codexModel !== "auto" ? this.config.codexModel : undefined;
    const manual = request.approvalPolicy === "manual";
    return {
      cwd: request.session.workingDirectory,
      approvalPolicy: request.approvalPolicy === "never" ? "never" : "on-request",
      sandbox: manual ? "read-only" : "workspace-write",
      ...(model ? { model } : {}),
    };
  }

  private buildTurnParams(request: AgentExecutionRequest, threadId: string): Record<string, unknown> {
    const model = this.config.codexModel && this.config.codexModel !== "auto" ? this.config.codexModel : undefined;
    const manual = request.approvalPolicy === "manual";
    const writableRoots = [...new Set(request.allowedDirectories.map((directory) => directory.trim()).filter(Boolean))];
    return {
      threadId,
      input: [{ type: "text", text: request.prompt }],
      cwd: request.session.workingDirectory,
      approvalPolicy: request.approvalPolicy === "never" ? "never" : "on-request",
      sandboxPolicy: manual
        ? { type: "readOnly", networkAccess: request.networkAccess === "enabled" }
        : {
            type: "workspaceWrite",
            writableRoots: writableRoots.length > 0 ? writableRoots : [request.session.workingDirectory],
            networkAccess: request.networkAccess === "enabled",
            excludeTmpdirEnvVar: false,
            excludeSlashTmp: false,
          },
      ...(model ? { model } : {}),
    };
  }

  private async handleServerRequest(request: AgentExecutionRequest, message: AppServerMessage): Promise<unknown> {
    const method = message.method ?? "";
    const params = message.params ?? {};
    if (method === "item/permissions/requestApproval") {
      return this.handlePermissionRequest(request, params, message.id);
    }
    if (method === "item/commandExecution/requestApproval" || method === "item/fileChange/requestApproval") {
      if (method === "item/fileChange/requestApproval" && nonEmptyString(params.grantRoot) && !this.isAllowedPath(params.grantRoot, request.allowedDirectories)) {
        return { decision: "decline" };
      }
      const detail = method.includes("commandExecution")
        ? [nonEmptyString(params.command) ? `Command: ${params.command}` : "", nonEmptyString(params.cwd) ? `Directory: ${params.cwd}` : "", nonEmptyString(params.reason) ? `Reason: ${params.reason}` : ""].filter(Boolean).join("\n")
        : [nonEmptyString(params.reason) ? params.reason : "Approve proposed file changes?", nonEmptyString(params.grantRoot) ? `Requested directory: ${params.grantRoot}` : ""].filter(Boolean).join("\n");
      const callbackRequest: AgentExecutionRequestCallbackRequest = {
        id: nonEmptyString(params.itemId) ? params.itemId : String(message.id ?? "approval"),
        kind: "approval",
        title: method.includes("commandExecution") ? "Approve Codex command?" : "Approve Codex file changes?",
        detail: detail || "Codex requested permission to continue.",
      };
      const response = await request.onRequest?.(callbackRequest);
      return { decision: response?.decision === "accept" ? "accept" : "decline" };
    }
    if (method === "item/tool/requestUserInput") {
      const questions = Array.isArray(params.questions) ? params.questions : [];
      const callbackRequest: AgentExecutionRequestCallbackRequest = {
        id: nonEmptyString(params.itemId) ? params.itemId : String(message.id ?? "input"),
        kind: "input",
        title: "Codex needs an answer",
        detail: "Reply to continue this Codex turn.",
        questions: questions.flatMap((raw, index) => {
          const question = valueAsRecord(raw);
          if (!nonEmptyString(question.id) || !nonEmptyString(question.question)) return [];
          const options = Array.isArray(question.options)
            ? question.options.flatMap((option) => nonEmptyString(valueAsRecord(option).label) ? [String(valueAsRecord(option).label)] : [])
            : undefined;
          return [{ id: question.id, header: nonEmptyString(question.header) ? question.header : `Question ${index + 1}`, question: question.question, ...(options ? { options } : {}), ...(typeof question.multiSelect === "boolean" ? { multiSelect: question.multiSelect } : {}) }];
        }),
      };
      const response = await request.onRequest?.(callbackRequest);
      const answers = response?.answers ?? {};
      return { answers: Object.fromEntries(Object.entries(answers).map(([id, values]) => [id, { answers: values }])) };
    }
    throw new Error(`Unsupported Codex App Server request '${method}' was rejected.`);
  }

  private async handlePermissionRequest(
    request: AgentExecutionRequest,
    params: Record<string, unknown>,
    messageId: string | number | undefined,
  ): Promise<Record<string, unknown>> {
    const requested = valueAsRecord(params.permissions);
    const fileSystem = valueAsRecord(requested.fileSystem);
    const requestedRead = Array.isArray(fileSystem.read) ? fileSystem.read.filter(nonEmptyString) : [];
    const requestedWrite = Array.isArray(fileSystem.write) ? fileSystem.write.filter(nonEmptyString) : [];
    const entries = Array.isArray(fileSystem.entries) ? fileSystem.entries.map(valueAsRecord) : [];
    const allPaths = [...requestedRead, ...requestedWrite];
    const entryLabels: string[] = [];
    for (const entry of entries) {
      const filePath = valueAsRecord(entry.path);
      if (filePath.type !== "path" || !nonEmptyString(filePath.path)) return { permissions: {}, scope: "turn" };
      allPaths.push(filePath.path);
      if (entry.access === "read" || entry.access === "write") entryLabels.push(`${entry.access === "read" ? "Read" : "Write"}: ${filePath.path}`);
    }
    if (nonEmptyString(params.grantRoot)) allPaths.push(params.grantRoot);
    if (allPaths.some((candidate) => !this.isAllowedPath(candidate, request.allowedDirectories))) {
      return { permissions: {}, scope: "turn" };
    }

    const network = valueAsRecord(requested.network);
    const networkRequested = network.enabled === true;
    const networkAllowed = request.networkAccess === "enabled";
    if (allPaths.length === 0 && !(networkRequested && networkAllowed)) return { permissions: {}, scope: "turn" };
    const details = [
      ...requestedRead.map((candidate) => `Read: ${candidate}`),
      ...requestedWrite.map((candidate) => `Write: ${candidate}`),
      ...entryLabels,
      ...(networkRequested ? [`Network access${networkAllowed ? "" : " (blocked by bridge policy)"}`] : []),
    ];
    const accepted = (await request.onRequest?.({
      id: nonEmptyString(params.itemId) ? params.itemId : String(messageId ?? "permission"),
      kind: "approval",
      title: "Approve Codex permissions?",
      detail: details.join("\n"),
    }))?.decision === "accept";
    if (!accepted) return { permissions: {}, scope: "turn" };

    const granted: Record<string, unknown> = {};
    if (requestedRead.length > 0 || requestedWrite.length > 0 || entries.length > 0) {
      granted.fileSystem = {
        read: requestedRead.length > 0 ? requestedRead : null,
        write: requestedWrite.length > 0 ? requestedWrite : null,
        ...(entries.length > 0 ? { entries } : {}),
      };
    }
    if (networkRequested && networkAllowed) granted.network = { enabled: true };
    return { permissions: granted, scope: "turn" };
  }

  private isAllowedPath(candidate: string, allowedDirectories: string[]): boolean {
    if (!path.isAbsolute(candidate) || /[*?]/.test(candidate)) return false;
    const absoluteCandidate = this.resolveCanonicalPath(candidate);
    if (!absoluteCandidate) return false;
    return allowedDirectories.some((directory) => {
      if (!path.isAbsolute(directory)) return false;
      const canonicalRoot = this.resolveCanonicalPath(directory);
      if (!canonicalRoot) return false;
      const relative = path.relative(canonicalRoot, absoluteCandidate);
      return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
    });
  }

  private resolveCanonicalPath(candidate: string): string | undefined {
    const resolved = path.resolve(candidate);
    let existingPath = resolved;
    const missingSegments: string[] = [];
    while (!fs.existsSync(existingPath)) {
      const parent = path.dirname(existingPath);
      if (parent === existingPath) return undefined;
      missingSegments.unshift(path.basename(existingPath));
      existingPath = parent;
    }
    try {
      return path.join(fs.realpathSync.native(existingPath), ...missingSegments);
    } catch {
      return undefined;
    }
  }
}
