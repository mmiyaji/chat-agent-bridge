import test from "node:test";
import assert from "node:assert/strict";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import { AppConfig, AgentExecutionRequest, AgentExecutionRequestCallbackResult, IncomingChatMessage } from "../src/core/types.js";
import {
  InMemoryStateStore,
  RecordingAgent,
  RecordingAuditLogger,
  RecordingChatAdapter,
  InlineJobQueue,
} from "./helpers/fakes.js";

function createConfig(): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    repoAliases: {},
    slackAllowedUserIds: ["U1", "U2"],
    mattermostAllowedUserIds: [],
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "socket",
    mattermostMode: "websocket",
    port: 8787,
    host: "127.0.0.1",
    defaultWorkingDirectory: "C:/repo",
    settingsFile: "./.test-tmp/native-settings.json",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    claudeProjectsDir: "C:/repo/data/claude-projects",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 1000,
    codexMaxRuntimeMs: 10000,
    codexMaxOutputLength: 12000,
    cursorCommand: "cursor-agent --trust",
    cursorModel: "Auto",
    cursorCommandTimeoutMs: 1000,
    cursorMaxRuntimeMs: 10000,
    geminiCommand: "gemini --approval-mode yolo",
    geminiModel: "gemini-2.5-pro",
    geminiCommandTimeoutMs: 1000,
    geminiMaxRuntimeMs: 10000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

class RequestingAgent extends RecordingAgent {
  response?: AgentExecutionRequestCallbackResult;

  constructor(
    private readonly request: NonNullable<AgentExecutionRequest["onRequest"]> extends (request: infer T) => Promise<unknown> ? T : never,
  ) {
    super("codex");
  }

  override async execute(request: AgentExecutionRequest) {
    this.requests.push(request);
    this.response = await request.onRequest?.(this.request);
    return { sessionId: request.session.sessionId, summary: "done" };
  }
}

async function setup(
  request: Parameters<NonNullable<AgentExecutionRequest["onRequest"]>>[0],
  nativeRequestTimeoutMs?: number,
) {
  const store = new InMemoryStateStore();
  const chat = new RecordingChatAdapter();
  const agent = new RequestingAgent(request);
  const bridge = new BridgeService({
    config: createConfig(),
    stateStore: store,
    sessionManager: new SessionManager(store),
    jobQueue: new InlineJobQueue(),
    chatAdapter: chat,
    agents: {
      codex: agent,
      "claude-code": new RecordingAgent("claude-code"),
      cursor: new RecordingAgent("cursor"),
      gemini: new RecordingAgent("gemini"),
    },
    auditLogger: new RecordingAuditLogger(),
    ...(nativeRequestTimeoutMs !== undefined ? { nativeRequestTimeoutMs } : {}),
  });
  await bridge.start();
  return { bridge, chat, agent };
}

function message(text: string, options: Partial<IncomingChatMessage> = {}): IncomingChatMessage {
  return {
    thread: { provider: "slack", channelId: "C1", threadId: "T1" },
    userId: "U1",
    text,
    requestedWorkingDirectory: "C:/repo/project",
    ...options,
  };
}

async function waitUntil(predicate: () => boolean, timeoutMs = 1000): Promise<void> {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) {
      throw new Error("Timed out waiting for native request state.");
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

test("native approval is thread-scoped and bound to the requesting allowlisted user", async () => {
  const { bridge, chat, agent } = await setup({
    id: "approval-1",
    kind: "approval",
    title: "Run a command",
    detail: "The agent wants to run npm test.",
  });
  try {
    const running = chat.handler!(message("continue"));
    await waitUntil(() => bridge.getPendingNativeRequests().length === 1);

    await chat.handler!(message("approve approval-1", {
      thread: { provider: "slack", channelId: "C1", threadId: "T2" },
    }));
    await chat.handler!(message("approve approval-1", { userId: "U2" }));
    assert.equal(bridge.getPendingNativeRequests().length, 1);

    await chat.handler!(message("approve approval-1"));
    await running;
    assert.deepEqual(agent.response, { decision: "accept" });
    assert.equal(agent.requests.length, 1, "the same execution resumes instead of being rerun");
    assert.equal(bridge.getPendingNativeRequests().length, 0);
  } finally {
    await bridge.stop();
  }
});

test("cancel declines a pending native approval and unblocks the running turn", async () => {
  const { bridge, chat, agent } = await setup({
    id: "approval-cancel",
    kind: "approval",
    title: "Confirm write",
    detail: "Apply changes to the workspace?",
  });
  try {
    const running = chat.handler!(message("continue"));
    await waitUntil(() => bridge.getPendingNativeRequests().length === 1);
    await chat.handler!(message("cancel"));
    await running;
    assert.deepEqual(agent.response, { decision: "decline" });
    assert.equal(bridge.getPendingNativeRequests().length, 0);
  } finally {
    await bridge.stop();
  }
});

test("native request times out with a safe decline", async () => {
  const { bridge, chat, agent } = await setup({
    id: "approval-timeout",
    kind: "approval",
    title: "Confirm write",
    detail: "Apply changes?",
  }, 20);
  try {
    await chat.handler!(message("continue"));
    assert.deepEqual(agent.response, { decision: "decline" });
    assert.equal(bridge.getPendingNativeRequests().length, 0);
  } finally {
    await bridge.stop();
  }
});

test("native user input answers are returned to the waiting agent", async () => {
  const { bridge, chat, agent } = await setup({
    id: "input-1",
    kind: "input",
    title: "Choose a test suite",
    detail: "Which tests should run?",
    questions: [
      { id: "suite", question: "Select a suite", options: ["unit", "integration"] },
      { id: "reason", question: "Why should it run?" },
    ],
  });
  try {
    const running = chat.handler!(message("continue"));
    await waitUntil(() => bridge.getPendingNativeRequests().length === 1);
    await chat.handler!(message("answer stale-input integration"));
    assert.deepEqual(bridge.getPendingNativeRequests()[0]?.unansweredQuestionIds, ["suite", "reason"]);
    await chat.handler!(message("answer input-1 suite integration"));
    assert.equal(bridge.getPendingNativeRequests().length, 1);
    await chat.handler!(message("answer input-1 reason deadline"));
    await running;
    assert.deepEqual(agent.response, { answers: { suite: ["integration"], reason: ["deadline"] } });
  } finally {
    await bridge.stop();
  }
});
