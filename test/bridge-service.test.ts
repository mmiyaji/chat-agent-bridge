import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { BridgeService } from "../src/core/bridge-service.js";
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
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    repoAliases: {
      repo: "C:/repo",
    },
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    port: 8787,
    host: "127.0.0.1",
    defaultWorkingDirectory: "C:/repo",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 1000,
    codexMaxRuntimeMs: 10000,
    codexMaxOutputLength: 12000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
  };
}

async function startBridge() {
  const config = createConfig();
  const store = new InMemoryStateStore();
  const chatAdapter = new RecordingChatAdapter();
  const codexAgent = new RecordingAgent("codex");
  const claudeAgent = new RecordingAgent("claude-code");
  const auditLogger = new RecordingAuditLogger();
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
  return { config, store, chatAdapter, agent: codexAgent, codexAgent, claudeAgent, auditLogger };
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
  assert.deepEqual(sessions[0].bridgeState, { connectionMode: "bridge-owned" });
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

  assert.deepEqual(sessions[0].bridgeState, { connectionMode: "bridge-owned" });
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

  assert.equal(auditLogger.events[0]?.type, "bridge.message.received");
  assert.equal(auditLogger.events[1]?.type, "bridge.execution.started");
  assert.equal(auditLogger.events[2]?.type, "bridge.execution.completed");
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

  assert.deepEqual(sessions[0].bridgeState, { connectionMode: "bridge-owned" });
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

  assert.deepEqual(sessions[0].bridgeState, { connectionMode: "bridge-owned" });
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
