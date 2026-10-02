import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { AdminServer } from "../src/admin/server.js";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import { ConnectionManager } from "../src/core/connection-manager.js";
import { createAppConfig } from "../src/config/env.js";
import { AppConfig, ExecutionRecord, SessionBinding } from "../src/core/types.js";
import {
  InMemoryStateStore,
  InlineJobQueue,
  RecordingAgent,
  RecordingAuditLogger,
  RecordingChatAdapter,
} from "../test/helpers/fakes.js";

function createConfig(port: number): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    repoAliases: {
      repo: "C:/repo",
    },
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    mattermostMode: "http",
    port: 8787,
    host: "127.0.0.1",
    adminHost: "127.0.0.1",
    adminPort: port,
    defaultWorkingDirectory: "C:/repo",
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
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
  };
}

function createSession(): SessionBinding {
  return {
    sessionId: "session-e2e-1",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C-E2E",
      threadId: "T-E2E",
    },
    workingDirectory: "C:/repo",
    createdAt: "2026-03-30T10:00:00.000Z",
    updatedAt: "2026-03-30T10:10:00.000Z",
    bridgeState: {
      connectionMode: "bridge-owned",
      approvalPolicyOverride: "manual",
      pendingApproval: {
        prompt: "Refine admin screen",
        requestedAt: "2026-03-30T10:09:00.000Z",
        reason: "Need approval",
        executionId: "exec-e2e-2",
      },
    },
  };
}

function createRecord(overrides?: Partial<ExecutionRecord>): ExecutionRecord {
  return {
    executionId: "exec-e2e-1",
    sessionId: "session-e2e-1",
    thread: {
      provider: "slack",
      channelId: "C-E2E",
      threadId: "T-E2E",
    },
    agent: "codex",
    prompt: "status",
    workingDirectory: "C:/repo",
    approvalPolicy: "manual",
    networkAccess: "disabled",
    status: "completed",
    summary: "done",
    createdAt: "2026-03-30T10:08:00.000Z",
    updatedAt: "2026-03-30T10:08:30.000Z",
    completedAt: "2026-03-30T10:08:30.000Z",
    ...overrides,
  };
}

async function main(): Promise<void> {
  const port = Number.parseInt(process.env.ADMIN_E2E_PORT ?? "42188", 10);

  // Isolate every runtime file this harness could touch so the e2e server never
  // writes into the repo's real data/ directory. BridgeService.start() flushes
  // the restart-completion queue on boot, which would otherwise re-write the
  // real data/runtime/restart-completion.jsonl with fixture channel ids.
  if (!process.env.CAB_RESTART_COMPLETION_FILE) {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-e2e-"));
    process.env.CAB_RESTART_COMPLETION_FILE = path.join(tempRoot, "restart-completion.jsonl");
  }

  const config = { ...createAppConfig({ CAB_SETTINGS_FILE: path.join(path.dirname(process.env.CAB_RESTART_COMPLETION_FILE!), 'admin-settings.json') }), ...createConfig(port) };
  config.settingsFile = path.join(path.dirname(process.env.CAB_RESTART_COMPLETION_FILE!), 'admin-settings.json');
  const store = new InMemoryStateStore();
  await store.initialize();
  await store.upsertSession(createSession());
  await store.upsertExecutionRecord(createRecord());
  await store.upsertExecutionRecord(
    createRecord({
      executionId: "exec-e2e-2",
      prompt: "Refine admin screen",
      status: "waiting_for_approval",
      summary: "Need approval",
      createdAt: "2026-03-30T10:09:00.000Z",
      updatedAt: "2026-03-30T10:09:00.000Z",
    }),
  );
  await store.upsertExecutionRecord(
    createRecord({
      executionId: "exec-e2e-3",
      sessionId: "session-e2e-2",
      status: "failed",
      error: "Previous run failed",
      createdAt: "2026-03-30T10:11:00.000Z",
      updatedAt: "2026-03-30T10:11:10.000Z",
    }),
  );

  const chatAdapter = new RecordingChatAdapter();
  const codexAgent = new RecordingAgent("codex");
  codexAgent.result = {
    sessionId: "ignored",
    summary: "Approval rerun completed.",
  };
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
    // Demo server must never restart the real bridge process.
    launchDetachedRestart: (input) => {
      console.log("[admin-e2e-server] detached restart requested (stubbed):", input);
    },
  });
  await bridge.start();
  const connections = new ConnectionManager(config, { codex: codexAgent, 'claude-code': claudeAgent }, () => new RecordingChatAdapter(), async () => ({ authenticated: true, statusText: 'プレビュー用エージェント' }));
  await connections.start(async () => undefined);

  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger,
    bridge,
    connections,
  });
  await admin.start();

  const shutdown = async () => {
    await admin.stop().catch(() => undefined);
  };

  process.on("SIGINT", () => {
    void shutdown().finally(() => process.exit(0));
  });
  process.on("SIGTERM", () => {
    void shutdown().finally(() => process.exit(0));
  });
}

void main().catch((error) => {
  console.error("Failed to start admin e2e server.", error);
  process.exit(1);
});
