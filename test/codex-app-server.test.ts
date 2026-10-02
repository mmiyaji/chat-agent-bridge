import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough, Writable } from "node:stream";
import type { AppConfig, AgentExecutionRequest } from "../src/core/types.js";
import { CodexAppServerAgent } from "../src/agents/codex/app-server-agent.js";
import { CodexAppServerClient } from "../src/agents/codex/app-server-client.js";

class FakeAppServer extends EventEmitter {
  readonly stdin = new Writable({
    write: (chunk, _encoding, callback) => { this.onInput(chunk.toString()); callback(); },
    final: (callback) => { queueMicrotask(() => this.emit("close", 0, null)); callback(); },
  });
  readonly stdout = new PassThrough();
  readonly stderr = new PassThrough();
  readonly requests: Array<{ method: string; params: Record<string, unknown> }> = [];
  readonly serverRequests: Array<{ method: string; params: Record<string, unknown> }> = [];
  readonly serverResponses: Array<{ id: string | number; result?: unknown; error?: unknown }> = [];
  private readonly pendingServerResponses = new Map<string | number, (response: { result?: unknown; error?: unknown }) => void>();
  private threadCount = 0;
  exitAfterTurnStart = false;
  holdTurn = false;
  holdInitialize = false;

  kill(): boolean { this.emit("close", 0, null); return true; }

  private onInput(chunk: string): void {
    for (const line of chunk.split(/\r?\n/).filter(Boolean)) {
      const message = JSON.parse(line) as { id?: number | string; method?: string; params?: Record<string, unknown>; result?: unknown };
      if (message.id !== undefined && !message.method) {
        const response = message as typeof message & { error?: unknown };
        this.serverResponses.push({ id: message.id, ...(response.result !== undefined ? { result: response.result } : {}), ...(response.error !== undefined ? { error: response.error } : {}) });
        this.pendingServerResponses.get(message.id)?.(response);
        this.pendingServerResponses.delete(message.id);
        continue;
      }
      if (!message.method) continue;
      if (message.id !== undefined) {
        this.requests.push({ method: message.method, params: message.params ?? {} });
        const respond = (result: unknown) => this.stdout.write(`${JSON.stringify({ id: message.id, result })}\n`);
        if (message.method === "initialize") { if (!this.holdInitialize) respond({ userAgent: "codex-test" }); }
        else if (message.method === "account/read") respond({ account: { type: "chatgpt", email: "test@example.com" }, requiresOpenaiAuth: true });
        else if (message.method === "model/list") respond({ data: [{ id: "gpt-default", model: "gpt-default", isDefault: true }] });
        else if (message.method === "thread/start") { this.threadCount++; respond({ thread: { id: `thread-${this.threadCount}` } }); }
        else if (message.method === "thread/resume") respond({ thread: { id: message.params?.threadId } });
        else if (message.method === "turn/start") {
          respond({ turn: { id: "turn-1", status: "inProgress", items: [] } });
          setTimeout(() => {
            if (this.exitAfterTurnStart) { this.emit("close", 7, null); return; }
            this.stdout.write(`${JSON.stringify({ method: "turn/started", params: { threadId: message.params?.threadId, turn: { id: "turn-1" } } })}\n`);
            if (this.holdTurn) return;
            if (this.serverRequests.length === 0) this.finishTurn(String(message.params?.threadId));
            else this.sendServerRequest("srv-1", this.serverRequests.shift()!, () => this.finishTurn(String(message.params?.threadId)));
          }, 0);
        } else if (message.method === "turn/interrupt") respond({});
        else respond({});
      } else if (message.method === "initialized") {
        // A probe or execute connection is fully initialized.
      }
    }
  }

  enqueueServerRequest(method: string, params: Record<string, unknown>): void {
    this.serverRequests.push({ method, params });
  }

  private sendServerRequest(id: string, request: { method: string; params: Record<string, unknown> }, onAccepted: () => void): void {
    this.pendingServerResponses.set(id, (response) => { assert.ok(response); onAccepted(); });
    this.stdout.write(`${JSON.stringify({ id, method: request.method, params: request.params })}\n`);
  }

  private finishTurn(threadId: string): void {
    this.stdout.write(`${JSON.stringify({ method: "item/completed", params: { threadId, item: { id: "item-1", type: "agentMessage", text: "Done." } } })}\n`);
    this.stdout.write(`${JSON.stringify({ method: "turn/completed", params: { threadId, turn: { id: "turn-1", status: "completed", items: [{ id: "item-1", type: "agentMessage", text: "Done." }] } } })}\n`);
  }
}

function createConfig(): AppConfig {
  return {
    chatProvider: "slack", agentKind: "codex", allowedDirectories: ["/repo"], repoAliases: {}, defaultApprovalPolicy: "manual", networkAccess: "disabled",
    slackMode: "http", slackAllowedUserIds: [], mattermostMode: "http", mattermostAllowedUserIds: [], port: 8787, host: "127.0.0.1", defaultWorkingDirectory: "/repo",
    settingsFile: "/repo/settings.json", stateFile: "/repo/state.json", auditLogFile: "/repo/audit.jsonl", codexSessionIndexFile: "/repo/codex.jsonl", claudeSessionIndexFile: "/repo/claude.json", claudeProjectsDir: "/repo/projects",
    codexModel: "auto", codexMaxTurns: 8, codexCommandTimeoutMs: 5_000, codexMaxRuntimeMs: 10_000, codexMaxOutputLength: 12000, claudeModel: "sonnet", claudeMaxTurns: 8, claudeCommandTimeoutMs: 1000, claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30, agentRetryCount: 0,
  };
}

function createRequest(agentState?: AgentExecutionRequest["session"]["agentState"], onRequest?: AgentExecutionRequest["onRequest"]): AgentExecutionRequest {
  return {
    session: { sessionId: "session-1", thread: { provider: "slack", channelId: "C1", threadId: "T1" }, agent: "codex", workingDirectory: "/repo", createdAt: "now", updatedAt: "now", ...(agentState ? { agentState } : {}) },
    prompt: "Do the task", approvalPolicy: "manual", networkAccess: "disabled", allowedDirectories: ["/repo"], ...(onRequest ? { onRequest } : {}),
  };
}

function fakeAgent(onCreate: (server: FakeAppServer) => void = () => {}) {
  const servers: FakeAppServer[] = [];
  const agent = new CodexAppServerAgent(createConfig(), {
    command: "fake-codex",
    spawnProcess: () => { const server = new FakeAppServer(); servers.push(server); onCreate(server); return server as never; },
  });
  return { agent, servers };
}

test("probe returns auth and default-model info without starting a thread or turn", async () => {
  const { agent, servers } = fakeAgent();
  const probe = await agent.probe();
  assert.equal(probe.authenticated, true);
  assert.equal(probe.model, "gpt-default");
  assert.ok(probe.statusText?.includes("test@example.com"));
  assert.deepEqual(servers[0]?.requests.map((request) => request.method), ["initialize", "account/read", "model/list"]);
  assert.equal(servers[0]?.requests.some((request) => request.method === "turn/start"), false);
});

test("execute starts then resumes a thread and preserves existing agent state", async () => {
  const { agent, servers } = fakeAgent();
  const first = await agent.execute(createRequest({ keep: "value" }));
  assert.equal(first.summary, "Done.");
  assert.deepEqual(first.agentState, { keep: "value", codexThreadId: "thread-1" });
  assert.equal(servers[0]?.requests.find((request) => request.method === "thread/start")?.params.model, undefined);
  const second = await agent.execute(createRequest(first.agentState));
  assert.equal(second.agentState?.codexThreadId, "thread-1");
  assert.equal(servers[1]?.requests.some((request) => request.method === "thread/resume"), true);
});

test("native file approvals are bridged and workspace/network settings stay constrained", async () => {
  const { agent, servers } = fakeAgent((server) => server.enqueueServerRequest("item/fileChange/requestApproval", { itemId: "change-1", reason: "Update project files", grantRoot: "/repo" }));
  const calls: unknown[] = [];
  const result = await agent.execute(createRequest(undefined, async (request) => {
    calls.push(request);
    return { decision: "accept" };
  }));
  assert.equal(result.summary, "Done.");
  assert.deepEqual(calls, [{ id: "change-1", kind: "approval", title: "Approve Codex file changes?", detail: "Update project files\nRequested directory: /repo" }]);
  const turnParams = servers[0]?.requests.find((request) => request.method === "turn/start")?.params;
  assert.equal(turnParams?.approvalPolicy, "on-request");
  assert.deepEqual(turnParams?.sandboxPolicy, { type: "readOnly", networkAccess: false });
  const threadParams = servers[0]?.requests.find((request) => request.method === "thread/start")?.params;
  assert.equal(threadParams?.approvalPolicy, "on-request");
  assert.equal(threadParams?.sandbox, "read-only");
});

test("user-input answers use App Server's answer map", async () => {
  const { agent } = fakeAgent((server) => server.enqueueServerRequest("item/tool/requestUserInput", {
    itemId: "input-1", questions: [{ id: "mode", header: "Mode", question: "Choose a mode", options: [{ label: "Fast" }, { label: "Careful" }] }],
  }));
  let seen: unknown;
  const result = await agent.execute(createRequest(undefined, async (request) => { seen = request; return { answers: { mode: ["Careful"] } }; }));
  assert.equal(result.summary, "Done.");
  assert.deepEqual(seen, { id: "input-1", kind: "input", title: "Codex needs an answer", detail: "Reply to continue this Codex turn.", questions: [{ id: "mode", header: "Mode", question: "Choose a mode", options: ["Fast", "Careful"] }] });
});

test("transport exit before turn completion rejects promptly", async () => {
  const { agent } = fakeAgent((server) => { server.exitAfterTurnStart = true; });
  await assert.rejects(agent.execute(createRequest()), /Codex App Server exited before completing the turn/);
});

test("cancelExecution interrupts the active App Server turn", async () => {
  const { agent, servers } = fakeAgent((server) => { server.holdTurn = true; });
  const running = agent.execute(createRequest());
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(await agent.cancelExecution("session-1"), true);
  await assert.rejects(running, /Execution cancelled/);
  assert.equal(servers[0]?.requests.some((request) => request.method === "turn/interrupt"), true);
});

test("unsupported App Server requests receive an explicit JSON-RPC error", async () => {
  const { agent, servers } = fakeAgent((server) => server.enqueueServerRequest("item/unknown/request", { itemId: "permissions-1" }));
  await agent.execute(createRequest());
  const reply = servers[0]?.serverResponses.find((response) => response.id === "srv-1");
  assert.equal((reply?.error as { code?: number; message?: string } | undefined)?.code, -32000);
  assert.match((reply?.error as { message?: string } | undefined)?.message ?? "", /Unsupported Codex App Server request/);
});

test("native permission requests grant approved paths inside allowed roots for this turn", async () => {
  const { agent, servers } = fakeAgent((server) => server.enqueueServerRequest("item/permissions/requestApproval", {
    itemId: "permission-1",
    cwd: "/repo",
    reason: "Read and update a project file",
    permissions: { network: null, fileSystem: { read: ["/repo/input.txt"], write: ["/repo/output.txt"], entries: [] } },
  }));
  let callbackValue: unknown;
  await agent.execute(createRequest(undefined, async (request) => { callbackValue = request; return { decision: "accept" }; }));
  assert.deepEqual(callbackValue, {
    id: "permission-1", kind: "approval", title: "Approve Codex permissions?",
    detail: "Read: /repo/input.txt\nWrite: /repo/output.txt",
  });
  assert.deepEqual(servers[0]?.serverResponses[0]?.result, {
    permissions: { fileSystem: { read: ["/repo/input.txt"], write: ["/repo/output.txt"] } }, scope: "turn",
  });
});

test("declined native permission requests grant nothing", async () => {
  const { agent, servers } = fakeAgent((server) => server.enqueueServerRequest("item/permissions/requestApproval", {
    itemId: "permission-2", cwd: "/repo", reason: "Write project file",
    permissions: { network: null, fileSystem: { read: null, write: ["/repo/output.txt"] } },
  }));
  await agent.execute(createRequest(undefined, async () => ({ decision: "decline" })));
  assert.deepEqual(servers[0]?.serverResponses[0]?.result, { permissions: {}, scope: "turn" });
});

test("native permission requests outside allowed roots are denied without prompting", async () => {
  const { agent, servers } = fakeAgent((server) => server.enqueueServerRequest("item/permissions/requestApproval", {
    itemId: "permission-3", cwd: "/repo", reason: "Write outside project",
    permissions: { network: null, fileSystem: { read: null, write: ["/other/secrets.txt"] } },
  }));
  let prompted = false;
  await agent.execute(createRequest(undefined, async () => { prompted = true; return { decision: "accept" }; }));
  assert.equal(prompted, false);
  assert.deepEqual(servers[0]?.serverResponses[0]?.result, { permissions: {}, scope: "turn" });
});

test("stdin errors reject pending RPC requests without escaping as unhandled errors", async () => {
  const server = new FakeAppServer();
  server.holdInitialize = true;
  const client = new CodexAppServerClient({
    command: "fake-codex", cwd: "/repo", requestTimeoutMs: 5_000,
    spawnProcess: () => server as never,
  });
  const pendingRequest = client.request("initialize", { clientInfo: { name: "test" } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(server.stdin.listenerCount("error"), 1);
  server.stdin.emit("error", new Error("EPIPE"));
  await assert.rejects(pendingRequest, /EPIPE/);
  await client.dispose();
});
