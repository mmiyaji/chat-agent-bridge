import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { BridgeService, isPermanentChatPostError } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import {
  InMemoryStateStore,
  RecordingAgent,
  RecordingAuditLogger,
  RecordingChatAdapter,
  InlineJobQueue,
} from "./helpers/fakes.js";
import { AppConfig, IncomingChatMessage } from "../src/core/types.js";

function createConfig(): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    // Empty by default so fresh threads run in the current cwd without a
    // working-directory picker. Tests that exercise the picker configure
    // aliases explicitly.
    repoAliases: {},
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    slackAllowedUserIds: [],
    mattermostMode: "http",
    mattermostAllowedUserIds: [],
    port: 8787,
    host: "127.0.0.1",
    defaultWorkingDirectory: "C:/repo",
    settingsFile: "./.test-tmp/bridge-service-settings.json",
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

async function startBridge() {
  const config = createConfig();
  const store = new InMemoryStateStore();
  const chatAdapter = new RecordingChatAdapter();
  const codexAgent = new RecordingAgent("codex");
  const claudeAgent = new RecordingAgent("claude-code");
  const cursorAgent = new RecordingAgent("cursor");
  const geminiAgent = new RecordingAgent("gemini");
  const auditLogger = new RecordingAuditLogger();
  const detachedRestarts: Array<{
    channelId?: string;
    threadId?: string;
    rebuild?: boolean;
    actor?: string;
    reason?: string;
  }> = [];
  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  if (!previousCompletionFile) {
    const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-bridge-service-"));
    process.env.CAB_RESTART_COMPLETION_FILE = path.join(tempRoot, "restart-completion.jsonl");
  }
  const bridge = new BridgeService({
    config,
    stateStore: store,
    sessionManager: new SessionManager(store),
    jobQueue: new InlineJobQueue(),
    chatAdapter,
    agents: {
      codex: codexAgent,
      "claude-code": claudeAgent,
      cursor: cursorAgent,
      gemini: geminiAgent,
    },
    auditLogger,
    launchDetachedRestart: (input) => {
      detachedRestarts.push(input);
    },
  });

  try {
    await bridge.start();
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
  return {
    bridge,
    config,
    store,
    chatAdapter,
    agent: codexAgent,
    codexAgent,
    claudeAgent,
    cursorAgent,
    geminiAgent,
    auditLogger,
    detachedRestarts,
  };
}

function createMessage(text: string): IncomingChatMessage {
  return {
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    userId: "U1",
    text,
    requestedWorkingDirectory: "C:/repo/project",
  };
}

async function waitFor(
  callback: () => Promise<void>,
  timeoutMs = 10_000,
  intervalMs = 100,
): Promise<void> {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    try {
      await callback();
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
  }

  await callback();
}

test("BridgeService posts progress then final summary", async () => {
  const { chatAdapter, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "all done",
  };

  await chatAdapter.handler?.(createMessage("do work"));

  assert.equal(chatAdapter.posted.length, 2);
  assert.match(chatAdapter.posted[0].text, /Received your request for session/);
  assert.match(chatAdapter.posted[0].text, /State: Running/);
  assert.equal(chatAdapter.posted[1].text, "all done");
  assert.equal(chatAdapter.updated.length, 1);
  assert.match(chatAdapter.updated[0].text, /State: Completed/);
  assert.equal(agent.requests.length, 1);
});

test("BridgeService persists returned agent state", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "done",
    agentState: {
      codexThreadId: "thread-123",
    },
  };

  await chatAdapter.handler?.(createMessage("remember"));
  const sessions = await store.listSessions();

  assert.equal(sessions.length, 1);
  assert.equal(sessions[0].agentState?.codexThreadId, "thread-123");
});

test("BridgeService stores execution history for completed runs", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "all done",
  };

  await chatAdapter.handler?.(createMessage("do work"));
  const records = await store.listExecutionRecords({ limit: 10 });

  assert.equal(records.length, 1);
  assert.equal(records[0].status, "completed");
  assert.equal(records[0].prompt, "do work");
  assert.equal(records[0].summary, "all done");
});

test("BridgeService marks stale running executions as cancelled on start", async () => {
  const config = createConfig();
  const store = new InMemoryStateStore();
  const chatAdapter = new RecordingChatAdapter();
  const codexAgent = new RecordingAgent("codex");
  const claudeAgent = new RecordingAgent("claude-code");
  const auditLogger = new RecordingAuditLogger();

  await store.upsertExecutionRecord({
    executionId: "exec-running",
    sessionId: "session-1",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    agent: "codex",
    prompt: "stuck",
    workingDirectory: "C:/repo/project",
    approvalPolicy: "never",
    networkAccess: "enabled",
    status: "running",
    createdAt: "2026-03-31T00:00:00.000Z",
    updatedAt: "2026-03-31T00:00:00.000Z",
  });

  const bridge = new BridgeService({
    config,
    stateStore: store,
    sessionManager: new SessionManager(store),
    jobQueue: new InlineJobQueue(),
    chatAdapter,
    agents: {
      codex: codexAgent,
      "claude-code": claudeAgent,
    },
    auditLogger,
  });

  await bridge.start();

  const records = await store.listExecutionRecords({ limit: 10 });
  assert.equal(records[0].status, "cancelled");
  assert.match(records[0].summary ?? "", /bridge restarted while work was in progress/);
  assert.equal(
    auditLogger.events.some((event) => event.type === "bridge.execution.recovered-as-cancelled"),
    true,
  );
});

test("BridgeService posts an execution failure", async () => {
  const { chatAdapter, agent } = await startBridge();
  agent.error = new Error("Codex exploded");

  await chatAdapter.handler?.(createMessage("break"));

  assert.equal(chatAdapter.posted.length, 2);
  assert.match(chatAdapter.posted[1].text, /Execution failed/);
  assert.match(chatAdapter.posted[1].text, /Codex exploded/);
  assert.equal(chatAdapter.updated.length, 1);
  assert.match(chatAdapter.updated[0].text, /State: Failed/);
});

test("BridgeService stores failed executions in history", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.error = new Error("Codex exploded");

  await chatAdapter.handler?.(createMessage("break"));
  const records = await store.listExecutionRecords({ limit: 10 });

  assert.equal(records.length, 1);
  assert.equal(records[0].status, "failed");
  assert.equal(records[0].error, "Codex exploded");
});

test("BridgeService silently ignores Slack users outside the allowlist", async () => {
  const { config, chatAdapter, codexAgent, auditLogger } = await startBridge();
  config.slackAllowedUserIds = ["U_ALLOWED"];

  await chatAdapter.handler?.({
    ...createMessage("do work"),
    userId: "U_BLOCKED",
  });

  assert.equal(codexAgent.requests.length, 0);
  assert.equal(chatAdapter.posted.length, 0);
  assert.equal(
    auditLogger.events.some(
      (event) =>
        event.type === "bridge.message.rejected" &&
        event.provider === "slack" &&
        event.userId === "U_BLOCKED" &&
        event.reason === "user-not-allowed",
    ),
    true,
  );
});

test("BridgeService silently ignores Mattermost bot-mode users outside the allowlist", async () => {
  const { config, chatAdapter, codexAgent, auditLogger } = await startBridge();
  config.mattermostActorMode = "bot";
  config.mattermostAllowedUserIds = ["mm-allowed"];

  await chatAdapter.handler?.({
    thread: {
      provider: "mattermost",
      channelId: "town-square",
      threadId: "post-1",
    },
    userId: "mm-blocked",
    text: "@cab do work",
    requestedWorkingDirectory: "C:/repo/project",
  });

  assert.equal(codexAgent.requests.length, 0);
  assert.equal(chatAdapter.posted.length, 0);
  assert.equal(
    auditLogger.events.some(
      (event) =>
        event.type === "bridge.message.rejected" &&
        event.provider === "mattermost" &&
        event.userId === "mm-blocked" &&
        event.reason === "user-not-allowed",
    ),
    true,
  );
});

test("BridgeService does not apply Mattermost allowlist outside bot mode", async () => {
  const { config, chatAdapter, codexAgent } = await startBridge();
  config.mattermostActorMode = "self";
  config.mattermostAllowedUserIds = ["someone-else"];
  codexAgent.result = {
    sessionId: "ignored",
    summary: "ok",
  };

  await chatAdapter.handler?.({
    thread: {
      provider: "mattermost",
      channelId: "dm-channel",
      threadId: "post-1",
    },
    userId: "self-user",
    text: "@self status",
    requestedWorkingDirectory: "C:/repo/project",
  });

  assert.equal(codexAgent.requests.length, 1);
});

test("BridgeService cancels a running execution best-effort", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.stallExecution = true;

  const running = chatAdapter.handler?.(createMessage("long running work"));
  await new Promise((resolve) => setTimeout(resolve, 0));
  await chatAdapter.handler?.(createMessage("cancel"));
  await running;

  const records = await store.listExecutionRecords({ limit: 10 });

  assert.equal(agent.cancelRequests.length, 1);
  assert.equal(records[0].status, "cancelled");
  assert.match(chatAdapter.posted.at(-2)?.text ?? "", /Cancellation requested/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Execution cancelled/);
  assert.match(chatAdapter.updated.at(-1)?.text ?? "", /State: Cancelled/);
});

test("BridgeService reuses the same session for the same thread", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("one"));
  await chatAdapter.handler?.(createMessage("two"));

  const sessions = await store.listSessions();
  assert.equal(sessions.length, 1);
});

test("BridgeService auto-splits to a new thread after the configured user-message threshold", async () => {
  const { chatAdapter, store, config, agent } = await startBridge();
  config.autoSplitThreadUserMessageCount = 2;
  agent.result = {
    sessionId: "ignored",
    summary: "done",
  };

  await chatAdapter.handler?.(createMessage("first"));
  await chatAdapter.handler?.(createMessage("second"));
  await chatAdapter.handler?.(createMessage("third"));

  const sessions = await store.listSessions();
  assert.equal(chatAdapter.startedThreads.length, 1);
  assert.match(chatAdapter.startedThreads[0]?.text ?? "", /Auto-split continuation/);
  assert.match(chatAdapter.posted.find((entry) => /moved to a new thread automatically/.test(entry.text))?.text ?? "", /moved to a new thread automatically/);
  assert.notEqual(sessions[0]?.thread.threadId, "T1");
  assert.equal(sessions[0]?.bridgeState?.threadUserMessageCount, 1);
  assert.equal(chatAdapter.posted.at(-1)?.thread.threadId, sessions[0]?.thread.threadId);
});

test("BridgeService restart command acknowledges immediately", async () => {
  const { chatAdapter, auditLogger, detachedRestarts } = await startBridge();

  await chatAdapter.handler?.(createMessage("restart"));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Bridge restart requested/);
  assert.equal(
    auditLogger.events.some(
      (event) =>
        event.type === "bridge.command.restart" &&
        event.sessionId !== undefined,
    ),
    true,
  );
  assert.equal(detachedRestarts.length, 1);
  assert.equal(detachedRestarts[0]?.rebuild, false);
  assert.equal(detachedRestarts[0]?.channelId, "C1");
  assert.equal(detachedRestarts[0]?.threadId, "T1");
});

test("BridgeService posts queued restart completions on start", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-restart-completion-"));
  const completionFile = path.join(tempRoot, "restart-completion.jsonl");
  await fs.writeFile(
    completionFile,
    `${JSON.stringify({
      channelId: "C1",
      threadId: "T1",
      text: "Bridge restart completed. The bridge is back online.",
    })}\n`,
    "utf8",
  );

  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  process.env.CAB_RESTART_COMPLETION_FILE = completionFile;

  try {
    const { chatAdapter, auditLogger } = await startBridge();
    assert.equal(chatAdapter.posted.length, 1);
    assert.equal(
      chatAdapter.posted[0]?.text,
      "Bridge restart completed. The bridge is back online.",
    );
    assert.equal(chatAdapter.posted[0]?.thread.channelId, "C1");
    assert.equal(chatAdapter.posted[0]?.thread.threadId, "T1");
    assert.equal(
      auditLogger.events.some((event) => event.type === "bridge.restart.completed"),
      true,
    );
    await assert.rejects(fs.readFile(completionFile, "utf8"));
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
});

test("BridgeService posts queued restart completions written after start", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-restart-completion-late-"));
  const completionFile = path.join(tempRoot, "restart-completion.jsonl");
  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  process.env.CAB_RESTART_COMPLETION_FILE = completionFile;

  try {
    const { chatAdapter, auditLogger } = await startBridge();
    await fs.writeFile(
      completionFile,
      `${JSON.stringify({
        channelId: "C1",
        threadId: "T1",
        text: "Bridge restart completed. The bridge is back online.",
      })}\n`,
      "utf8",
    );

    await waitFor(async () => {
      assert.equal(chatAdapter.posted.length, 1);
      assert.equal(
        chatAdapter.posted[0]?.text,
        "Bridge restart completed. The bridge is back online.",
      );
    });

    assert.equal(
      auditLogger.events.some((event) => event.type === "bridge.restart.completed"),
      true,
    );
    await assert.rejects(fs.readFile(completionFile, "utf8"));
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
});

test("BridgeService retains queued restart completions when posting fails", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-restart-completion-retry-"));
  const completionFile = path.join(tempRoot, "restart-completion.jsonl");
  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  process.env.CAB_RESTART_COMPLETION_FILE = completionFile;

  try {
    const { chatAdapter, auditLogger } = await startBridge();
    const originalPost = chatAdapter.postThreadMessage.bind(chatAdapter);
    let shouldFail = true;
    chatAdapter.postThreadMessage = async (...args) => {
      if (shouldFail) {
        throw new Error("temporary slack outage");
      }
      return originalPost(...args);
    };

    await fs.writeFile(
      completionFile,
      `${JSON.stringify({
        channelId: "C1",
        threadId: "T1",
        text: "Bridge restart completed. The bridge is back online.",
      })}\n`,
      "utf8",
    );

    await waitFor(async () => {
      const retained = await fs.readFile(completionFile, "utf8");
      assert.match(retained, /Bridge restart completed/);
    });
    assert.equal(chatAdapter.posted.length, 0);
    assert.equal(
      auditLogger.events.some((event) => event.type === "bridge.restart.completed"),
      false,
    );

    shouldFail = false;
    await waitFor(async () => {
      assert.equal(chatAdapter.posted.length, 1);
      assert.equal(
        chatAdapter.posted[0]?.text,
        "Bridge restart completed. The bridge is back online.",
      );
    });
    await assert.rejects(fs.readFile(completionFile, "utf8"));
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
});

test("isPermanentChatPostError classifies permanent vs transient errors", () => {
  // Permanent chat errors — must be dropped, never retried.
  assert.equal(isPermanentChatPostError("channel_not_found"), true);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 404"), true);
  assert.equal(isPermanentChatPostError("Slack API error: channel_not_found"), true);
  assert.equal(isPermanentChatPostError("Slack API error: is_archived"), true);
  assert.equal(isPermanentChatPostError("Slack API error: not_in_channel"), true);
  assert.equal(isPermanentChatPostError("Slack API error: invalid_arguments"), true);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 400"), true);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 401"), true);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 403"), true);

  // Transient errors — must be retried.
  assert.equal(isPermanentChatPostError("Request timed out"), false);
  assert.equal(isPermanentChatPostError("connect ECONNREFUSED 127.0.0.1:443"), false);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 500"), false);
  assert.equal(isPermanentChatPostError("Mattermost API HTTP error: 503"), false);
  assert.equal(isPermanentChatPostError(""), false);
});

test("BridgeService drops restart completions that fail with a permanent chat error", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-restart-completion-permanent-"));
  const completionFile = path.join(tempRoot, "restart-completion.jsonl");
  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  process.env.CAB_RESTART_COMPLETION_FILE = completionFile;

  try {
    const { chatAdapter } = await startBridge();
    let postCalls = 0;
    chatAdapter.postThreadMessage = async () => {
      postCalls += 1;
      throw new Error("Slack API error: channel_not_found");
    };

    await fs.writeFile(
      completionFile,
      `${JSON.stringify({
        channelId: "C1",
        threadId: "T1",
        text: "Bridge restart completed. The bridge is back online.",
      })}\n`,
      "utf8",
    );

    // The next flush tick must post exactly once and then permanently drop the
    // entry (file removed, not re-queued).
    await waitFor(async () => {
      await assert.rejects(fs.readFile(completionFile, "utf8"));
    });
    assert.equal(postCalls, 1);
    assert.equal(chatAdapter.posted.length, 0);
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
});

test("BridgeService increments attempts and retains transient restart completions", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-restart-completion-attempts-"));
  const completionFile = path.join(tempRoot, "restart-completion.jsonl");
  const previousCompletionFile = process.env.CAB_RESTART_COMPLETION_FILE;
  process.env.CAB_RESTART_COMPLETION_FILE = completionFile;

  try {
    const { chatAdapter } = await startBridge();
    chatAdapter.postThreadMessage = async () => {
      throw new Error("temporary slack outage");
    };

    await fs.writeFile(
      completionFile,
      `${JSON.stringify({
        channelId: "C1",
        threadId: "T1",
        text: "Bridge restart completed. The bridge is back online.",
      })}\n`,
      "utf8",
    );

    // After at least one flush the entry is retained with an incremented
    // attempts count persisted back to the file.
    await waitFor(async () => {
      const retained = await fs.readFile(completionFile, "utf8");
      const line = retained.split(/\r?\n/).find((entry) => entry.trim().length > 0) ?? "{}";
      const parsed = JSON.parse(line) as { attempts?: number; channelId?: string };
      assert.equal(parsed.channelId, "C1");
      assert.ok((parsed.attempts ?? 0) >= 1, `expected attempts >= 1, got ${parsed.attempts}`);
    });
    assert.equal(chatAdapter.posted.length, 0);
  } finally {
    if (previousCompletionFile === undefined) {
      delete process.env.CAB_RESTART_COMPLETION_FILE;
    } else {
      process.env.CAB_RESTART_COMPLETION_FILE = previousCompletionFile;
    }
  }
});

test("BridgeService stores a pending approval when the result indicates write access is needed", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.(createMessage("make changes"));
  const sessions = await store.listSessions();

  assert.equal(sessions.length, 1);
  assert.equal(
    (sessions[0].bridgeState?.pendingApproval as { prompt?: string } | undefined)?.prompt,
    "make changes",
  );
  assert.match(chatAdapter.posted[2].text, /Approval required/);
});

test("BridgeService includes provider-neutral approval actions for Mattermost threads", async () => {
  const { chatAdapter, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.({
    ...createMessage("make changes"),
    thread: {
      provider: "mattermost",
      channelId: "DM1",
      threadId: "POST1",
    },
  });

  const approvalMessage = chatAdapter.posted.find((entry) => /Approval required/.test(entry.text));
  assert.deepEqual(
    approvalMessage?.options?.actions?.map((action) => action.value ?? action.id),
    ["approve", "reject"],
  );
});

test("BridgeService reruns the pending prompt when approval is given", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.(createMessage("edit files"));

  agent.result = {
    sessionId: "ignored",
    summary: "Applied the requested changes.",
  };

  await chatAdapter.handler?.(createMessage("approve"));
  const sessions = await store.listSessions();
  const records = await store.listExecutionRecords({ limit: 10 });
  const rerunRecord = records.find((record) => record.status === "completed");
  const blockedRecord = records.find((record) => record.status === "waiting_for_approval");

  assert.equal(agent.requests.length, 2);
  assert.equal(agent.requests[1].approvalPolicy, "never");
  assert.equal(agent.requests[1].prompt, "edit files");
  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.equal(sessions[0].bridgeState?.threadUserMessageCount, 1);
  assert.equal(records.length, 2);
  assert.equal(rerunRecord?.status, "completed");
  assert.equal(rerunRecord?.rerunOfExecutionId, blockedRecord?.executionId);
  assert.equal(blockedRecord?.status, "waiting_for_approval");
  assert.match(chatAdapter.updated.at(-1)?.text ?? "", /State: Completed/);
});

test("BridgeService posts a fresh acknowledgement for each run", async () => {
  const { chatAdapter, agent } = await startBridge();

  await chatAdapter.handler?.(createMessage("first"));
  await chatAdapter.handler?.(createMessage("second"));

  assert.equal(
    chatAdapter.posted.filter((entry) => /Received your request for session/.test(entry.text)).length,
    2,
  );
});

test("BridgeService clears a pending approval when rejected", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.(createMessage("edit files"));
  await chatAdapter.handler?.(createMessage("reject"));
  const sessions = await store.listSessions();
  const records = await store.listExecutionRecords({ limit: 10 });

  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.equal(sessions[0].bridgeState?.threadUserMessageCount, 1);
  assert.equal(records[0].status, "rejected");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /was cleared/);
});

test("BridgeService reports session status without invoking the agent", async () => {
  const { chatAdapter, agent, auditLogger } = await startBridge();

  await chatAdapter.handler?.(createMessage("status"));

  assert.equal(agent.requests.length, 0);
  assert.match(chatAdapter.posted[0].text, /Working directory/);
  assert.match(chatAdapter.posted[0].text, /Session connection/);
  assert.match(chatAdapter.posted[0].text, /Agent session id/);
  assert.match(chatAdapter.posted[0].text, /Latest execution/);
  assert.equal(auditLogger.events.at(-1)?.type, "bridge.command.status");
});

test("BridgeService returns recent execution history on demand", async () => {
  const { chatAdapter, agent } = await startBridge();

  agent.result = {
    sessionId: "ignored",
    summary: "first summary",
  };
  await chatAdapter.handler?.(createMessage("first"));

  agent.result = {
    sessionId: "ignored",
    summary: "second summary",
  };
  await chatAdapter.handler?.(createMessage("second"));

  await chatAdapter.handler?.(createMessage("history 2"));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Recent executions for session/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /second summary/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /first summary/);
});

test("BridgeService lists sessions for operations visibility", async () => {
  const { chatAdapter, store } = await startBridge();
  const now = new Date().toISOString();

  await store.upsertSession({
    sessionId: "session-ops",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C9",
      threadId: "T9",
    },
    workingDirectory: "C:/repo/ops",
    createdAt: now,
    updatedAt: now,
    agentState: {},
    bridgeState: {},
  });

  await chatAdapter.handler?.(createMessage("sessions"));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Bridge sessions/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /session-ops/);
});

test("BridgeService lists pending approvals across sessions", async () => {
  const { chatAdapter, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.(createMessage("edit files"));
  await chatAdapter.handler?.(createMessage("approvals"));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Pending approvals/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /edit files/);
});

test("BridgeService lists recent failures across sessions", async () => {
  const { chatAdapter, agent } = await startBridge();
  agent.error = new Error("Codex exploded");

  await chatAdapter.handler?.(createMessage("break things"));
  agent.error = undefined;
  await chatAdapter.handler?.(createMessage("failures 3"));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Recent failures/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Codex exploded/);
});

test("BridgeService updates the working directory within allowed roots", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];

  await chatAdapter.handler?.(createMessage("cwd other-project"));
  const sessions = await store.listSessions();

  assert.equal(agent.requests.length, 0);
  assert.equal(sessions[0].workingDirectory, "C:\\repo\\project\\other-project");
  assert.match(chatAdapter.posted[0].text, /now bound/);
});

test("BridgeService keeps the new workdir for later messages after cwd", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];

  await chatAdapter.handler?.(createMessage("cwd other-project"));
  await chatAdapter.handler?.(createMessage("status"));

  const sessions = await store.listSessions();

  assert.equal(agent.requests.length, 0);
  assert.equal(sessions[0].workingDirectory, "C:\\repo\\project\\other-project");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Working directory: `C:\\repo\\project\\other-project`/);
});

test("BridgeService runs later messages from the updated workdir after cwd", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];
  agent.result = {
    sessionId: "ignored",
    summary: "done",
  };

  await chatAdapter.handler?.(createMessage("cwd other-project"));
  await chatAdapter.handler?.(createMessage("do work"));

  const sessions = await store.listSessions();

  assert.equal(sessions[0].workingDirectory, "C:\\repo\\project\\other-project");
  assert.equal(agent.requests.length, 1);
  assert.equal(agent.requests[0]?.session.workingDirectory, "C:\\repo\\project\\other-project");
  assert.equal(chatAdapter.posted.at(-1)?.text, "done");
});

test("BridgeService switches to a repo alias", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo", "C:/other"];
  config.repoAliases = {
    repo: "C:/repo",
    other: "C:/other",
  };

  await chatAdapter.handler?.(createMessage("repo other"));
  const sessions = await store.listSessions();

  assert.equal(agent.requests.length, 0);
  assert.equal(sessions[0].workingDirectory, "C:/other");
});

test("BridgeService can set per-thread approval mode", async () => {
  const { chatAdapter, store, agent } = await startBridge();

  await chatAdapter.handler?.(createMessage("mode allow"));
  await chatAdapter.handler?.(createMessage("do work"));
  const sessions = await store.listSessions();

  assert.equal(agent.requests.at(-1)?.approvalPolicy, "never");
  assert.equal(sessions[0].bridgeState?.approvalPolicyOverride, "never");
  assert.match(chatAdapter.posted[0]?.text ?? "", /approval mode is now `never`/);
});

test("BridgeService can switch the active agent for a thread", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("agent claude"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "claude-code");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.match(chatAdapter.posted[0]?.text ?? "", /now uses `claude-code`/);
});

test("BridgeService can switch the active agent to Cursor for a thread", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("agent cursor"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "cursor");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.match(chatAdapter.posted[0]?.text ?? "", /now uses `cursor`/);
});

test("BridgeService can switch the active agent to Gemini for a thread", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("agent gemini"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "gemini");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.match(chatAdapter.posted[0]?.text ?? "", /now uses `gemini`/);
});

test("BridgeService can move a session to a new Slack thread", async () => {
  const { chatAdapter, store, agent } = await startBridge();
  agent.result = {
    sessionId: "ignored",
    summary: "first reply",
  };

  await chatAdapter.handler?.(createMessage("do work"));
  await chatAdapter.handler?.(createMessage("new-thread"));

  const sessions = await store.listSessions();
  const movedThreadId = sessions[0]?.thread.threadId;

  assert.equal(chatAdapter.startedThreads.length, 1);
  assert.equal(sessions[0]?.thread.channelId, "C1");
  assert.match(movedThreadId ?? "", /^new-thread-/);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /moved to a new thread/);
});

test("BridgeService can fork a session into a new Slack thread", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("fork claude manual"));
  const sessions = await store.listSessions();
  const originalSession = sessions.find((session) => session.thread.threadId === "T1");
  const forkedSession = sessions.find((session) => session.thread.threadId !== "T1");

  assert.equal(chatAdapter.startedThreads.length, 1);
  assert.equal(sessions.length, 2);
  assert.equal(originalSession?.agent, "codex");
  assert.equal(forkedSession?.agent, "claude-code");
  assert.equal(forkedSession?.bridgeState?.approvalPolicyOverride, "manual");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Forked session/);
});

test("BridgeService routes execution to the selected agent", async () => {
  const { chatAdapter, store, codexAgent, claudeAgent } = await startBridge();
  claudeAgent.result = {
    sessionId: "ignored",
    summary: "claude handled it",
  };

  await chatAdapter.handler?.(createMessage("agent claude"));
  await chatAdapter.handler?.(createMessage("do work"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "claude-code");
  assert.equal(codexAgent.requests.length, 0);
  assert.equal(claudeAgent.requests.length, 1);
  assert.equal(claudeAgent.requests[0]?.session.agent, "claude-code");
  assert.equal(chatAdapter.posted.at(-1)?.text, "claude handled it");
});

test("BridgeService routes execution to Cursor when selected", async () => {
  const { chatAdapter, store, codexAgent, claudeAgent, cursorAgent } = await startBridge();
  cursorAgent.result = {
    sessionId: "ignored",
    summary: "cursor handled it",
  };

  await chatAdapter.handler?.(createMessage("agent cursor"));
  await chatAdapter.handler?.(createMessage("do work"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "cursor");
  assert.equal(codexAgent.requests.length, 0);
  assert.equal(claudeAgent.requests.length, 0);
  assert.equal(cursorAgent.requests.length, 1);
  assert.equal(cursorAgent.requests[0]?.session.agent, "cursor");
  assert.equal(chatAdapter.posted.at(-1)?.text, "cursor handled it");
});

test("BridgeService routes execution to Gemini when selected", async () => {
  const { chatAdapter, store, codexAgent, claudeAgent, cursorAgent, geminiAgent } =
    await startBridge();
  geminiAgent.result = {
    sessionId: "ignored",
    summary: "gemini handled it",
  };

  await chatAdapter.handler?.(createMessage("agent gemini"));
  await chatAdapter.handler?.(createMessage("do work"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "gemini");
  assert.equal(codexAgent.requests.length, 0);
  assert.equal(claudeAgent.requests.length, 0);
  assert.equal(cursorAgent.requests.length, 0);
  assert.equal(geminiAgent.requests.length, 1);
  assert.equal(geminiAgent.requests[0]?.session.agent, "gemini");
  assert.equal(chatAdapter.posted.at(-1)?.text, "gemini handled it");
});

test("BridgeService can reset to a fresh session with explicit mode", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("attach 019d39dd-63ea-7570-a5ea-7cb5e5386378"));
  await chatAdapter.handler?.(createMessage("new manual"));
  const sessions = await store.listSessions();

  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.approvalPolicyOverride, "manual");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /fresh Codex session/);
});

test("BridgeService can reset to a fresh Claude session explicitly", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("new claude manual"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "claude-code");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.approvalPolicyOverride, "manual");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /fresh Claude Code session/);
});

test("BridgeService can reset to a fresh Cursor session explicitly", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("new cursor manual"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "cursor");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.approvalPolicyOverride, "manual");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /fresh Cursor session/);
});

test("BridgeService can reset to a fresh Gemini session explicitly", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("new gemini manual"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agent, "gemini");
  assert.deepEqual(sessions[0].agentState, {});
  assert.equal(sessions[0].bridgeState?.approvalPolicyOverride, "manual");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /fresh Gemini CLI session/);
});

test("BridgeService rejects working directory changes outside allowed roots", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];

  await chatAdapter.handler?.(createMessage("cwd ../../outside"));
  const sessions = await store.listSessions();

  assert.equal(agent.requests.length, 0);
  assert.equal(sessions[0].workingDirectory, "C:\\repo\\project");
  assert.match(chatAdapter.posted[0].text, /Working directory update failed/);
});

test("BridgeService writes audit events for normal execution", async () => {
  const { chatAdapter, auditLogger } = await startBridge();

  await chatAdapter.handler?.(createMessage("do work"));

  const eventTypes = auditLogger.events.map((event) => event.type);
  assert.equal(eventTypes[0], "bridge.message.received");
  assert.ok(eventTypes.includes("bridge.execution.started"));
  assert.ok(eventTypes.includes("bridge.execution.environment"));
  assert.ok(eventTypes.includes("bridge.execution.completed"));
});

test("BridgeService help command lists operational commands", async () => {
  const { chatAdapter } = await startBridge();

  await chatAdapter.handler?.(createMessage("help"));

  assert.match(chatAdapter.posted[0]?.text ?? "", /repo <alias>/);
  assert.match(chatAdapter.posted[0]?.text ?? "", /attach <session-id>/);
  assert.match(chatAdapter.posted[0]?.text ?? "", /reject/);
});

test("BridgeService falls back to never approval when the agent does not support manual approval", async () => {
  const { chatAdapter, agent } = await startBridge();
  (agent.capabilities as { manualApproval: boolean }).manualApproval = false;

  await chatAdapter.handler?.(createMessage("do work"));

  assert.equal(agent.requests[0]?.approvalPolicy, "never");
});

test("BridgeService does not keep pending approval when approval policy is never", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.defaultApprovalPolicy = "never";
  agent.result = {
    sessionId: "ignored",
    summary: "Blocked by read-only sandbox. I need write access to continue.",
  };

  await chatAdapter.handler?.(createMessage("make changes"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.equal(sessions[0].bridgeState?.threadUserMessageCount, 1);
  assert.equal(chatAdapter.posted.length, 2);
  assert.equal(chatAdapter.posted[1]?.text, "Blocked by read-only sandbox. I need write access to continue.");
});

test("BridgeService clears stale pending approval when approval policy is never", async () => {
  const { chatAdapter, store, config } = await startBridge();
  config.defaultApprovalPolicy = "never";

  await store.upsertSession({
    sessionId: "existing-session",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    agentState: {},
    bridgeState: {
      pendingApproval: {
        prompt: "old request",
        requestedAt: new Date().toISOString(),
        reason: "need write access",
      },
    },
  });

  await chatAdapter.handler?.(createMessage("status"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].bridgeState?.connectionMode, "bridge-owned");
  assert.equal(sessions[0].bridgeState?.threadUserMessageCount, 0);
  assert.match(chatAdapter.posted[0]?.text ?? "", /Pending approval: `no`/);
});

test("BridgeService can attach to an explicit Codex thread id", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("attach 019d39dd-63ea-7570-a5ea-7cb5e5386378"));
  const sessions = await store.listSessions();

  assert.equal(
    sessions[0].agentState?.codexThreadId,
    "019d39dd-63ea-7570-a5ea-7cb5e5386378",
  );
  assert.equal(sessions[0].bridgeState?.connectionMode, "attached");
  assert.match(chatAdapter.posted[0]?.text ?? "", /now attached/);
});

test("BridgeService can detach from a saved Codex thread id", async () => {
  const { chatAdapter, store } = await startBridge();

  await chatAdapter.handler?.(createMessage("attach 019d39dd-63ea-7570-a5ea-7cb5e5386378"));
  await chatAdapter.handler?.(createMessage("detach"));
  const sessions = await store.listSessions();

  assert.deepEqual(sessions[0].agentState, {});
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Detached bridge session/);
});

test("BridgeService can retry the latest execution from an admin command", async () => {
  const { bridge, store, agent } = await startBridge();
  await store.upsertSession({
    sessionId: "session-1",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: "2026-03-30T10:10:00.000Z",
    updatedAt: "2026-03-30T10:10:00.000Z",
    agentState: {},
    bridgeState: {},
  });
  await store.upsertExecutionRecord({
    executionId: "exec-retry",
    sessionId: "session-1",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    agent: "codex",
    prompt: "retry me",
    workingDirectory: "C:/repo/project",
    approvalPolicy: "manual",
    networkAccess: "disabled",
    status: "failed",
    error: "timeout",
    createdAt: "2026-03-30T10:11:00.000Z",
    updatedAt: "2026-03-30T10:11:10.000Z",
    completedAt: "2026-03-30T10:11:10.000Z",
  });

  const result = await bridge.dispatchAdminCommand("session-1", "retry-last");

  assert.equal(result.accepted, true);
  await waitFor(async () => {
    assert.equal(agent.requests.at(-1)?.prompt, "retry me");
  });
  assert.equal(agent.requests.at(-1)?.prompt, "retry me");
});

test("BridgeService opens a session picker on empty mention", async () => {
  const { chatAdapter, store, config } = await startBridge();
  const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cab-picker-"));
  config.codexSessionIndexFile = path.join(tempDir, "session_index.jsonl");
  await fs.writeFile(
    config.codexSessionIndexFile,
    [
      '{"id":"session-a","thread_name":"Alpha","updated_at":"2026-03-30T01:00:00Z"}',
      '{"id":"session-b","thread_name":"Beta","updated_at":"2026-03-30T00:00:00Z"}',
    ].join("\n"),
    "utf8",
  );

  await chatAdapter.handler?.(createMessage(""));
  const sessions = await store.listSessions();

  assert.match(chatAdapter.posted[0]?.text ?? "", /Choose a Codex session to attach/);
  assert.equal(
    ((sessions[0].bridgeState?.sessionPicker as { sessionIds?: string[] } | undefined)?.sessionIds ?? [])[0],
    "session-a",
  );
});

test("BridgeService attaches selected session from picker", async () => {
  const { chatAdapter, store, config } = await startBridge();
  const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cab-picker-"));
  config.codexSessionIndexFile = path.join(tempDir, "session_index.jsonl");
  await fs.writeFile(
    config.codexSessionIndexFile,
    [
      '{"id":"session-a","thread_name":"Alpha","updated_at":"2026-03-30T01:00:00Z"}',
      '{"id":"session-b","thread_name":"Beta","updated_at":"2026-03-30T00:00:00Z"}',
    ].join("\n"),
    "utf8",
  );

  await chatAdapter.handler?.(createMessage(""));
  await chatAdapter.handler?.(createMessage("2"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agentState?.codexThreadId, "session-b");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /now attached/);
});

test("BridgeService can attach-last for Claude Code", async () => {
  const { chatAdapter, store, config } = await startBridge();
  const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cab-claude-"));
  config.agentKind = "claude-code";
  config.claudeSessionIndexFile = path.join(tempDir, "sessions.json");
  await fs.writeFile(
    config.claudeSessionIndexFile,
    JSON.stringify(
      [
        {
          sessionId: "claude-newer",
          updatedAt: "2026-03-30T01:00:00Z",
          summary: "Latest Claude task",
        },
        {
          sessionId: "claude-older",
          updatedAt: "2026-03-30T00:00:00Z",
          summary: "Older Claude task",
        },
      ],
      null,
      2,
    ),
    "utf8",
  );

  await chatAdapter.handler?.(createMessage("attach-last"));
  const sessions = await store.listSessions();

  assert.equal(sessions[0].agentState?.claudeSessionId, "claude-newer");
  assert.equal(sessions[0].bridgeState?.connectionMode, "attached");
  assert.match(chatAdapter.posted[0]?.text ?? "", /claude-newer/);
});

test("BridgeService opens a Claude session picker on empty mention", async () => {
  const { chatAdapter, store, config } = await startBridge();
  const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cab-claude-picker-"));
  config.agentKind = "claude-code";
  config.claudeSessionIndexFile = path.join(tempDir, "sessions.json");
  await fs.writeFile(
    config.claudeSessionIndexFile,
    JSON.stringify(
      {
        first: {
          sessionId: "claude-a",
          updatedAt: "2026-03-30T01:00:00Z",
          summary: "Alpha task",
        },
        second: {
          sessionId: "claude-b",
          updatedAt: "2026-03-30T00:00:00Z",
          summary: "Beta task",
        },
      },
      null,
      2,
    ),
    "utf8",
  );

  await chatAdapter.handler?.(createMessage(""));
  const sessions = await store.listSessions();

  assert.match(chatAdapter.posted[0]?.text ?? "", /Choose a Claude Code session to attach/);
  assert.equal(
    ((sessions[0].bridgeState?.sessionPicker as { sessionIds?: string[] } | undefined)?.sessionIds ?? [])[0],
    "claude-a",
  );
});

test("BridgeService reports that Cursor reusable sessions are unsupported", async () => {
  const { chatAdapter, store } = await startBridge();
  await store.upsertSession({
    sessionId: "session-cursor",
    agent: "cursor",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    agentState: {},
    bridgeState: {
      connectionMode: "bridge-owned",
    },
  });

  await chatAdapter.handler?.(createMessage(""));

  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Reusable Cursor sessions are not supported yet/);
});

test("BridgeService reports that Gemini reusable sessions are unsupported", async () => {
  const { chatAdapter, store } = await startBridge();
  await store.upsertSession({
    sessionId: "session-gemini",
    agent: "gemini",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    agentState: {},
    bridgeState: {
      connectionMode: "bridge-owned",
    },
  });

  await chatAdapter.handler?.(createMessage(""));

  assert.match(
    chatAdapter.posted.at(-1)?.text ?? "",
    /Reusable Gemini CLI sessions are not supported yet/,
  );
});

async function seedClaudeProjectSessions(
  workingDirectory: string,
  entries: Array<{ id: string; summary: string; mtimeSeconds: number }>,
): Promise<string> {
  const { encodeClaudeProjectDir } = await import("../src/agents/claude/project-sessions.js");
  const projectsRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-bridge-projects-"));
  const projectFolder = path.join(projectsRoot, encodeClaudeProjectDir(workingDirectory));
  await fs.mkdir(projectFolder, { recursive: true });
  for (const entry of entries) {
    const file = path.join(projectFolder, `${entry.id}.jsonl`);
    await fs.writeFile(file, JSON.stringify({ type: "summary", summary: entry.summary }), "utf8");
    await fs.utimes(file, entry.mtimeSeconds, entry.mtimeSeconds);
  }

  return projectsRoot;
}

test("BridgeService offers a resume picker on the first Claude instruction", async () => {
  const { chatAdapter, store, claudeAgent, config, auditLogger } = await startBridge();
  config.agentKind = "claude-code";
  config.claudeProjectsDir = await seedClaudeProjectSessions("C:/repo/project", [
    { id: "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa", summary: "First session", mtimeSeconds: 2000 },
    { id: "bbbb2222-bbbb-bbbb-bbbb-bbbbbbbbbbbb", summary: "Second session", mtimeSeconds: 1000 },
  ]);

  await chatAdapter.handler?.(createMessage("build the feature"));

  assert.equal(claudeAgent.requests.length, 0);
  const picker = chatAdapter.posted.at(-1)?.text ?? "";
  assert.match(picker, /has previous Claude Code sessions/);
  assert.match(picker, /aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa/);
  assert.match(picker, /First session/);

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.resumeOffered, true);
  const storedPicker = sessions[0]?.bridgeState?.sessionPicker as
    | { sessionIds: string[]; pendingPrompt?: string }
    | undefined;
  assert.deepEqual(storedPicker?.sessionIds, [
    "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "bbbb2222-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  ]);
  assert.equal(storedPicker?.pendingPrompt, "build the feature");
  assert.equal(
    auditLogger.events.some((event) => event.type === "bridge.resume-picker.opened"),
    true,
  );
});

test("BridgeService runs the pending prompt after a numeric resume selection", async () => {
  const { chatAdapter, store, claudeAgent, config } = await startBridge();
  config.agentKind = "claude-code";
  config.claudeProjectsDir = await seedClaudeProjectSessions("C:/repo/project", [
    { id: "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa", summary: "First session", mtimeSeconds: 2000 },
  ]);

  await chatAdapter.handler?.(createMessage("build the feature"));
  await chatAdapter.handler?.(createMessage("1"));

  await waitFor(async () => {
    assert.equal(claudeAgent.requests.length, 1);
  });

  assert.equal(claudeAgent.requests[0]?.prompt, "build the feature");
  assert.equal(
    claudeAgent.requests[0]?.session.agentState?.claudeSessionId,
    "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  );

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.resumeOffered, true);
  assert.equal(sessions[0]?.bridgeState?.sessionPicker, undefined);
});

test("BridgeService runs the pending prompt after a fresh resume selection", async () => {
  const { chatAdapter, store, claudeAgent, config } = await startBridge();
  config.agentKind = "claude-code";
  config.claudeProjectsDir = await seedClaudeProjectSessions("C:/repo/project", [
    { id: "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa", summary: "First session", mtimeSeconds: 2000 },
  ]);

  await chatAdapter.handler?.(createMessage("build the feature"));
  await chatAdapter.handler?.(createMessage("new"));

  await waitFor(async () => {
    assert.equal(claudeAgent.requests.length, 1);
  });

  assert.equal(claudeAgent.requests[0]?.prompt, "build the feature");
  assert.equal(claudeAgent.requests[0]?.session.agentState?.claudeSessionId, undefined);

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.resumeOffered, true);
  assert.equal(sessions[0]?.bridgeState?.sessionPicker, undefined);
});

test("BridgeService executes immediately when the folder has no Claude sessions", async () => {
  const { chatAdapter, store, claudeAgent, config } = await startBridge();
  config.agentKind = "claude-code";
  config.claudeSessionIndexFile = path.join(
    await fs.mkdtemp(path.join(os.tmpdir(), "cab-bridge-empty-")),
    "missing-sessions.json",
  );
  config.claudeProjectsDir = path.join(os.tmpdir(), "cab-bridge-projects-missing-none");

  await chatAdapter.handler?.(createMessage("do work"));

  assert.equal(claudeAgent.requests.length, 1);
  assert.equal(claudeAgent.requests[0]?.prompt, "do work");
  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.resumeOffered, true);
});

test("BridgeService clears the Claude session and re-arms resume when the folder changes", async () => {
  const { chatAdapter, store, config } = await startBridge();
  config.agentKind = "claude-code";
  config.allowedDirectories = ["C:/repo"];
  await store.upsertSession({
    sessionId: "session-claude-cwd",
    agent: "claude-code",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    agentState: {
      claudeSessionId: "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    },
    bridgeState: {
      connectionMode: "attached",
      resumeOffered: true,
    },
  });

  await chatAdapter.handler?.(createMessage("cwd other-project"));

  const sessions = await store.listSessions();
  const updated = sessions.find((entry) => entry.sessionId === "session-claude-cwd");
  assert.equal(updated?.workingDirectory, "C:\\repo\\project\\other-project");
  assert.deepEqual(updated?.agentState, {});
  assert.equal(updated?.bridgeState?.connectionMode, "bridge-owned");
  assert.equal(updated?.bridgeState?.resumeOffered, undefined);
  assert.equal(updated?.bridgeState?.sessionPicker, undefined);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /detached because the folder changed/);
});

test("BridgeService opens a workdir picker on a fresh thread with no directory", async () => {
  const { chatAdapter, store, agent, config, auditLogger } = await startBridge();
  config.allowedDirectories = ["C:/repo", "C:/other"];
  config.repoAliases = {
    repo: "C:/repo",
    other: "C:/other",
  };

  await chatAdapter.handler?.(createMessage("do work"));

  assert.equal(agent.requests.length, 0);
  const picker = chatAdapter.posted.at(-1)?.text ?? "";
  assert.match(picker, /Choose a working directory for this thread/);
  assert.match(picker, /1\. `other`/);
  assert.match(picker, /2\. `repo`/);

  const sessions = await store.listSessions();
  const storedPicker = sessions[0]?.bridgeState?.workdirPicker as
    | { aliases: string[]; pendingPrompt?: string }
    | undefined;
  assert.deepEqual(storedPicker?.aliases, ["other", "repo"]);
  assert.equal(storedPicker?.pendingPrompt, "do work");
  assert.equal(
    auditLogger.events.some((event) => event.type === "bridge.workdir-picker.opened"),
    true,
  );
});

test("BridgeService binds the selected alias and cascades into the resume picker", async () => {
  const { chatAdapter, store, claudeAgent, config } = await startBridge();
  config.agentKind = "claude-code";
  const workingDirectory = "C:/repo/project";
  config.allowedDirectories = ["C:/repo"];
  config.repoAliases = {
    // Two aliases so index 2 (sorted) is a meaningful pick.
    alpha: "C:/repo/alpha",
    project: workingDirectory,
  };
  config.claudeProjectsDir = await seedClaudeProjectSessions(workingDirectory, [
    { id: "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa", summary: "First session", mtimeSeconds: 2000 },
  ]);

  await chatAdapter.handler?.(createMessage("build the feature"));

  // Sorted aliases: ["alpha", "project"]; pick #2 -> project.
  const workdirPicker = chatAdapter.posted.at(-1)?.text ?? "";
  assert.match(workdirPicker, /Choose a working directory/);

  await chatAdapter.handler?.(createMessage("2"));

  await waitFor(async () => {
    const resumePost = chatAdapter.posted.at(-1)?.text ?? "";
    assert.match(resumePost, /has previous Claude Code sessions/);
  });

  await chatAdapter.handler?.(createMessage("1"));

  await waitFor(async () => {
    assert.equal(claudeAgent.requests.length, 1);
  });

  assert.equal(claudeAgent.requests[0]?.prompt, "build the feature");
  assert.equal(
    claudeAgent.requests[0]?.session.agentState?.claudeSessionId,
    "aaaa1111-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  );
  assert.equal(claudeAgent.requests[0]?.session.workingDirectory, workingDirectory);

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.workdirConfirmed, true);
  assert.equal(sessions[0]?.bridgeState?.workdirPicker, undefined);
});

test("BridgeService keeps the current directory when the workdir picker reply is keep", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];
  config.repoAliases = {
    repo: "C:/repo",
  };
  agent.result = {
    sessionId: "ignored",
    summary: "done",
  };

  await chatAdapter.handler?.(createMessage("do work"));
  await chatAdapter.handler?.(createMessage("keep"));

  await waitFor(async () => {
    assert.equal(agent.requests.length, 1);
  });

  assert.equal(agent.requests[0]?.prompt, "do work");
  assert.equal(
    path.resolve(agent.requests[0]?.session.workingDirectory ?? ""),
    path.resolve("C:/repo/project"),
  );

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.workdirConfirmed, true);
  assert.equal(sessions[0]?.bridgeState?.workdirPicker, undefined);
  assert.equal(
    chatAdapter.posted.some((post) => /Keeping working directory/.test(post.text)),
    true,
  );
});

test("BridgeService binds an inline alias token and runs the remaining instruction", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo", "C:/other"];
  config.repoAliases = {
    repo: "C:/repo",
    other: "C:/other",
  };
  agent.result = {
    sessionId: "ignored",
    summary: "done",
  };

  await chatAdapter.handler?.(createMessage("other do something"));

  await waitFor(async () => {
    assert.equal(agent.requests.length, 1);
  });

  assert.equal(agent.requests[0]?.prompt, "do something");
  assert.equal(agent.requests[0]?.session.workingDirectory, "C:/other");

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.workdirConfirmed, true);
  assert.equal(sessions[0]?.bridgeState?.workdirPicker, undefined);
  // No workdir picker was ever posted.
  assert.equal(
    chatAdapter.posted.some((post) => /Choose a working directory/.test(post.text)),
    false,
  );
});

test("BridgeService binds an inline absolute path with no instruction and waits", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-inline-abs-"));
  const childDir = path.join(tempRoot, "child");
  await fs.mkdir(childDir, { recursive: true });
  config.allowedDirectories = [tempRoot];
  config.repoAliases = {};

  const message: IncomingChatMessage = {
    thread: { provider: "slack", channelId: "C1", threadId: "T1" },
    userId: "U1",
    text: childDir,
    requestedWorkingDirectory: tempRoot,
  };
  await chatAdapter.handler?.(message);

  assert.equal(agent.requests.length, 0);
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /is now bound to/);

  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.workdirConfirmed, true);
  assert.equal(path.resolve(sessions[0]?.workingDirectory ?? ""), path.resolve(childDir));

  // A later instruction flows normally without another picker.
  agent.result = { sessionId: "ignored", summary: "done" };
  await chatAdapter.handler?.(createMessage("do work"));
  await waitFor(async () => {
    assert.equal(agent.requests.length, 1);
  });
  assert.equal(agent.requests[0]?.prompt, "do work");
});

test("BridgeService rejects an inline path outside allowed roots without executing", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];
  config.repoAliases = {
    repo: "C:/repo",
  };

  await chatAdapter.handler?.(createMessage("C:/forbidden/place fix the bug"));

  assert.equal(agent.requests.length, 0);
  assert.match(
    chatAdapter.posted.at(-1)?.text ?? "",
    /Directory not allowed or not found: `C:\/forbidden\/place`/,
  );
  const sessions = await store.listSessions();
  assert.equal(sessions[0]?.bridgeState?.workdirConfirmed, undefined);
});

test("BridgeService treats a non-matching first word as prose and shows the picker", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];
  config.repoAliases = {
    repo: "C:/repo",
  };

  await chatAdapter.handler?.(createMessage("please fix the bug"));

  assert.equal(agent.requests.length, 0);
  const picker = chatAdapter.posted.at(-1)?.text ?? "";
  assert.match(picker, /Choose a working directory/);
  assert.doesNotMatch(picker, /Directory not allowed/);

  const sessions = await store.listSessions();
  const storedPicker = sessions[0]?.bridgeState?.workdirPicker as
    | { aliases: string[]; pendingPrompt?: string }
    | undefined;
  assert.equal(storedPicker?.pendingPrompt, "please fix the bug");
});

test("BridgeService skips the workdir picker when the session already has execution history", async () => {
  const { chatAdapter, store, agent, config } = await startBridge();
  config.allowedDirectories = ["C:/repo"];
  config.repoAliases = {
    repo: "C:/repo",
  };
  agent.result = {
    sessionId: "ignored",
    summary: "done",
  };

  await store.upsertSession({
    sessionId: "session-history",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    agentState: {},
    bridgeState: {
      connectionMode: "bridge-owned",
    },
  });
  await store.upsertExecutionRecord({
    executionId: "exec-1",
    sessionId: "session-history",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    agent: "codex",
    prompt: "earlier",
    workingDirectory: "C:/repo/project",
    approvalPolicy: "manual",
    networkAccess: "disabled",
    status: "completed",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  });

  await chatAdapter.handler?.(createMessage("do work"));

  await waitFor(async () => {
    assert.equal(agent.requests.length, 1);
  });
  assert.equal(agent.requests[0]?.prompt, "do work");
  assert.equal(
    chatAdapter.posted.some((post) => /Choose a working directory/.test(post.text)),
    false,
  );
  const sessions = await store.listSessions();
  const updated = sessions.find((entry) => entry.sessionId === "session-history");
  assert.equal(updated?.bridgeState?.workdirConfirmed, true);
});
