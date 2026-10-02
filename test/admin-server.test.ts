import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { AdminServer } from "../src/admin/server.js";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import { AppConfig, ExecutionRecord, SessionBinding } from "../src/core/types.js";
import {
  RecordingAuditLogger,
  InMemoryStateStore,
  RecordingAgent,
  RecordingChatAdapter,
  InlineJobQueue,
} from "./helpers/fakes.js";

class ThrowingExecutionStateStore extends InMemoryStateStore {
  override async listExecutionRecords(): Promise<ExecutionRecord[]> {
    throw new Error("execution-store-offline");
  }
}

function createConfig(): AppConfig {
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
    slackAllowedUserIds: [],
    mattermostMode: "http",
    mattermostAllowedUserIds: [],
    port: 8787,
    host: "127.0.0.1",
    adminHost: "127.0.0.1",
    adminPort: 0,
    defaultWorkingDirectory: "C:/repo",
    settingsFile: "./.test-tmp/admin-settings.json",
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
    cursorCommandTimeoutMs: 600000,
    cursorMaxRuntimeMs: 3600000,
    geminiCommand: "gemini --approval-mode yolo",
    geminiModel: "gemini-2.5-pro",
    geminiCommandTimeoutMs: 600000,
    geminiMaxRuntimeMs: 3600000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

function createSession(overrides?: Partial<SessionBinding>): SessionBinding {
  return {
    sessionId: "session-1",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo",
    createdAt: "2026-03-30T10:00:00.000Z",
    updatedAt: "2026-03-30T10:10:00.000Z",
    bridgeState: {
      connectionMode: "bridge-owned",
      approvalPolicyOverride: "manual",
      pendingApproval: {
        prompt: "edit files",
        requestedAt: "2026-03-30T10:09:00.000Z",
        reason: "Need approval",
        executionId: "exec-2",
      },
    },
    ...overrides,
  };
}

function createRecord(overrides?: Partial<ExecutionRecord>): ExecutionRecord {
  return {
    executionId: "exec-1",
    sessionId: "session-1",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
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

test("AdminServer serves session, approval, and failure APIs", async () => {
  const store = new InMemoryStateStore();
  await store.upsertSession(createSession());
  await store.upsertExecutionRecord(createRecord());
  await store.upsertExecutionRecord(
    createRecord({
      executionId: "exec-2",
      prompt: "edit files",
      status: "waiting_for_approval",
      summary: "Need approval",
      createdAt: "2026-03-30T10:09:00.000Z",
      updatedAt: "2026-03-30T10:09:00.000Z",
    }),
  );
  await store.upsertExecutionRecord(
    createRecord({
      executionId: "exec-3",
      sessionId: "session-2",
      status: "failed",
      error: "boom",
      createdAt: "2026-03-30T10:11:00.000Z",
      updatedAt: "2026-03-30T10:11:10.000Z",
    }),
  );

  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const sessionsResponse = await getJson(port, "/admin/api/sessions");
  assert.equal(sessionsResponse.statusCode, 200);
  assert.equal(sessionsResponse.body.sessions.length, 1);
  assert.equal(sessionsResponse.body.sessions[0].sessionId, "session-1");
  assert.equal(sessionsResponse.body.sessions[0].pendingApproval.prompt, "edit files");
  assert.equal(sessionsResponse.body.connectionStatus.provider, "slack");
  assert.equal(sessionsResponse.body.connectionStatus.connected, true);
  assert.equal(sessionsResponse.body.connectionStatus.reconnectCount, 2);

  const approvalsResponse = await getJson(port, "/admin/api/approvals");
  assert.equal(approvalsResponse.statusCode, 200);
  assert.equal(approvalsResponse.body.approvals.length, 1);
  assert.equal(approvalsResponse.body.approvals[0].sessionId, "session-1");

  const failuresResponse = await getJson(port, "/admin/api/failures?limit=1");
  assert.equal(failuresResponse.statusCode, 200);
  assert.equal(failuresResponse.body.failures.length, 1);
  assert.equal(failuresResponse.body.failures[0].executionId, "exec-3");

  await admin.stop();
});

test("AdminServer enriches the session payload with environment fields", async () => {
  const store = new InMemoryStateStore();
  await store.upsertSession(
    createSession({
      agent: "claude-code",
      agentState: { claudeSessionId: "claude-abc" },
      bridgeState: {
        connectionMode: "attached",
        workdirConfirmed: true,
      },
    }),
  );

  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();
  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getJson(port, "/admin/api/sessions");
  assert.equal(response.statusCode, 200);
  const session = response.body.sessions[0];
  assert.equal(session.workingDirectory, "C:/repo");
  assert.equal(session.agent, "claude-code");
  assert.equal(session.connectionMode, "attached");
  assert.equal(session.agentSessionId, "claude-abc");
  // claude-code supports manual approval; default policy is manual.
  assert.equal(session.approvalPolicy, "manual");
  assert.equal(session.workdirConfirmed, true);
  assert.equal(session.pendingWorkdirPicker, false);
  assert.equal(session.pendingSessionPicker, false);

  await admin.stop();
});

test("AdminServer serves an environment endpoint without secret values", async () => {
  const store = new InMemoryStateStore();
  await store.setChannelDefaultWorkingDirectory("slack", "C1", "C:/repo");

  const config = createConfig();
  config.slackBotToken = "xoxb-super-secret";
  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();
  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getJson(port, "/admin/api/environment");
  assert.equal(response.statusCode, 200);
  const environment = response.body.environment;
  assert.equal(environment.defaultWorkingDirectory, "C:/repo");
  assert.deepEqual(environment.allowedDirectories, ["C:/repo"]);
  assert.deepEqual(environment.repoAliases, { repo: "C:/repo" });
  assert.deepEqual(environment.channelDefaultWorkingDirectories, { "slack:C1": "C:/repo" });
  assert.equal(environment.claudeProjectsDir, "C:/repo/data/claude-projects");
  assert.equal(environment.agentKind, "codex");
  assert.equal(environment.chatProvider, "slack");
  assert.ok(Array.isArray(environment.agentEnvironmentVariables));
  assert.ok(environment.agentEnvironmentVariables.includes("CHAT_AGENT_BRIDGE_WORKDIR"));

  // No secret-looking value leaks anywhere in the payload.
  const serialized = JSON.stringify(response.body);
  assert.doesNotMatch(serialized, /xoxb-super-secret/);
  assert.doesNotMatch(serialized, /TOKEN|SECRET|PASSWORD/i);

  await admin.stop();
});

test("AdminServer server-renders dashboard data and action controls", async () => {
  const store = new InMemoryStateStore();
  await store.upsertSession(createSession());
  await store.upsertExecutionRecord(
    createRecord({
      executionId: "exec-3",
      sessionId: "session-2",
      status: "failed",
      error: "boom",
      createdAt: "2026-03-30T10:11:00.000Z",
      updatedAt: "2026-03-30T10:11:10.000Z",
    }),
  );
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/legacy");
  assert.equal(response.statusCode, 200);
  assert.match(response.body, /Bridge Admin/);
  assert.match(response.body, /data-testid="sessions-table"/);
  assert.match(response.body, /session-1/);
  assert.match(response.body, /edit files/);
  assert.match(response.body, /data-action="approve"/);
  assert.match(response.body, /data-testid="session-search"/);
  assert.match(response.body, /Idle/);
  assert.match(response.body, /data-testid="nav-settings"/);
  assert.match(response.body, /data-testid="connection-panel"/);
  assert.match(response.body, /Connected to Slack via Socket Mode/);
  assert.match(response.body, /Reconnects:/);
  assert.match(response.body, /Retry latest/);
  assert.match(response.body, /Attach latest/);
  assert.match(response.body, /detail-command/);

  await admin.stop();
});

test("AdminServer serves a dedicated settings page", async () => {
  const store = new InMemoryStateStore();
  await store.upsertSession(createSession());
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/settings");
  assert.equal(response.statusCode, 200);
  assert.equal(response.headers["cache-control"], "no-store, no-cache, must-revalidate");
  assert.match(response.body, /Runtime Settings/);
  assert.match(response.body, /data-testid="nav-dashboard"/);
  assert.match(response.body, /class="is-active" data-testid="nav-settings"/);
  assert.match(response.body, /function renderCommandResolutionCard\(label, diagnostic\)/);

  await admin.stop();
});

test("AdminServer degrades gracefully when execution history cannot be read", async () => {
  const store = new ThrowingExecutionStateStore();
  await store.upsertSession(createSession());
  const auditLogger = new RecordingAuditLogger();
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger,
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const dashboard = await getText(port, "/admin/legacy");
  assert.equal(dashboard.statusCode, 200);
  assert.match(dashboard.body, /data-testid="dashboard-warning"/);
  assert.match(dashboard.body, /Execution history could not be fully loaded/);

  const session = await getJson(port, "/admin/api/sessions/session-1");
  assert.equal(session.statusCode, 200);
  assert.deepEqual(session.body.executionHistory, []);
  assert.equal(
    auditLogger.events.some((event) => event.type === "admin.dashboard.degraded"),
    true,
  );
  assert.equal(
    auditLogger.events.some((event) => event.type === "admin.api.session.degraded"),
    true,
  );

  await admin.stop();
});

test("AdminServer serves an onboarding page", async () => {
  const store = new InMemoryStateStore();
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/onboarding");
  assert.equal(response.statusCode, 200);
  assert.match(response.body, /Bridge Onboarding/);
  assert.match(response.body, /data-testid="onboarding-step-1"/);
  assert.match(response.body, /data-testid="onboarding-step-2"/);
  assert.match(response.body, /data-testid="onboarding-step-3"/);
  assert.match(response.body, /data-testid="onboarding-step-4"/);
  assert.match(response.body, /data-testid="provider-card-mattermost"/);
  assert.match(response.body, /data-testid="provider-card-slack"/);
  assert.match(response.body, /data-testid="agent-card-claude-code"/);
  assert.match(response.body, /data-testid="test-connection"/);
  assert.match(response.body, /Actor mode/);
  assert.match(response.body, /Mattermost bot token|mattermost-bot-token/);
  assert.match(response.body, /Language/);
  assert.match(response.body, /Theme/);

  const terms = await getText(port, "/admin/terms");
  assert.equal(terms.statusCode, 200);
  assert.match(terms.body, /Terms of Use/);

  const privacy = await getText(port, "/admin/privacy");
  assert.equal(privacy.statusCode, 200);
  assert.match(privacy.body, /Privacy Policy/);

  await admin.stop();
});

test("AdminServer serves a settings page with grouped sections", async () => {
  const store = new InMemoryStateStore();
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/settings");
  assert.equal(response.statusCode, 200);
  assert.match(response.body, /data-testid="settings-group-basics"/);
  assert.match(response.body, /data-testid="settings-group-connection"/);
  assert.match(response.body, /data-testid="settings-group-agent-tuning"/);
  assert.match(response.body, /data-testid="settings-group-server-ports"/);
  assert.match(response.body, /data-testid="settings-group-secret-cleanup"/);

  await admin.stop();
});

test("AdminServer dashboard shows a setup-incomplete banner when required settings are missing", async () => {
  const store = new InMemoryStateStore();
  const config = createConfig();
  config.chatProvider = "mattermost";
  delete config.mattermostServerUrl;
  delete config.mattermostBotToken;
  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/legacy");
  assert.equal(response.statusCode, 200);
  assert.match(response.body, /data-testid="setup-incomplete-banner"/);
  assert.match(response.body, /Setup incomplete/);
  assert.match(response.body, /Mattermost server URL/);
  assert.match(response.body, /Mattermost bot token/);
  assert.match(response.body, /data-testid="connection-chip"/);

  await admin.stop();
});

test("AdminServer dashboard hides the setup banner once required settings are present", async () => {
  const store = new InMemoryStateStore();
  const config = createConfig();
  config.chatProvider = "mattermost";
  config.mattermostServerUrl = "https://chat.example.test";
  config.mattermostBotToken = "token-value";
  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await getText(port, "/admin/legacy");
  assert.equal(response.statusCode, 200);
  assert.doesNotMatch(response.body, /data-testid="setup-incomplete-banner"/);

  await admin.stop();
});

test("AdminServer test-connection succeeds for mattermost using supplied values", async () => {
  const store = new InMemoryStateStore();
  let capturedUrl: string | undefined;
  let capturedHeaders: Record<string, string> | undefined;
  const admin = new AdminServer(
    {
      config: createConfig(),
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    },
    {
      testConnectionFetch: async (input) => {
        capturedUrl = input.url;
        capturedHeaders = input.headers;
        return { username: "bot-user" } as any;
      },
    },
  );
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(
    port,
    "/admin/api/test-connection",
    JSON.stringify({
      provider: "mattermost",
      serverUrl: "https://chat.example.test",
      botToken: "secret-token",
    }),
  );
  assert.equal(response.statusCode, 200);
  assert.equal(response.body.ok, true);
  assert.equal(response.body.details.username, "bot-user");
  assert.match(capturedUrl ?? "", /\/api\/v4\/users\/me$/);
  assert.equal(capturedHeaders?.authorization, "Bearer secret-token");
  assert.doesNotMatch(JSON.stringify(response.body), /secret-token/);

  await admin.stop();
});

test("AdminServer test-connection falls back to stored mattermost config when body omits values", async () => {
  const store = new InMemoryStateStore();
  const config = createConfig();
  config.mattermostServerUrl = "https://stored.example.test";
  config.mattermostBotToken = "stored-token";
  let capturedHeaders: Record<string, string> | undefined;
  const admin = new AdminServer(
    {
      config,
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    },
    {
      testConnectionFetch: async (input) => {
        capturedHeaders = input.headers;
        return { username: "stored-user" } as any;
      },
    },
  );
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(
    port,
    "/admin/api/test-connection",
    JSON.stringify({ provider: "mattermost" }),
  );
  assert.equal(response.statusCode, 200);
  assert.equal(response.body.ok, true);
  assert.equal(response.body.details.username, "stored-user");
  assert.equal(capturedHeaders?.authorization, "Bearer stored-token");

  await admin.stop();
});

test("AdminServer test-connection reports failure without leaking the token", async () => {
  const store = new InMemoryStateStore();
  const admin = new AdminServer(
    {
      config: createConfig(),
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    },
    {
      testConnectionFetch: async () => {
        throw new Error("Mattermost API HTTP error: 401");
      },
    },
  );
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(
    port,
    "/admin/api/test-connection",
    JSON.stringify({
      provider: "mattermost",
      serverUrl: "https://chat.example.test",
      botToken: "bad-token",
    }),
  );
  assert.equal(response.statusCode, 200);
  assert.equal(response.body.ok, false);
  assert.match(response.body.error, /401/);
  assert.doesNotMatch(JSON.stringify(response.body), /bad-token/);

  await admin.stop();
});

test("AdminServer test-connection succeeds for slack using supplied bot token", async () => {
  const store = new InMemoryStateStore();
  let capturedUrl: string | undefined;
  const admin = new AdminServer(
    {
      config: createConfig(),
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    },
    {
      testConnectionFetch: async (input) => {
        capturedUrl = input.url;
        return { ok: true, user: "slack-user", team: "slack-team" } as any;
      },
    },
  );
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(
    port,
    "/admin/api/test-connection",
    JSON.stringify({ provider: "slack", slackBotToken: "xoxb-secret" }),
  );
  assert.equal(response.statusCode, 200);
  assert.equal(response.body.ok, true);
  assert.equal(response.body.details.user, "slack-user");
  assert.equal(response.body.details.team, "slack-team");
  assert.equal(capturedUrl, "https://slack.com/api/auth.test");
  assert.doesNotMatch(JSON.stringify(response.body), /xoxb-secret/);

  await admin.stop();
});

test("AdminServer queues approve actions through the bridge", async () => {
  const store = new InMemoryStateStore();
  const { bridge, chatAdapter, agent, auditLogger } = createBridge(store);
  agent.result = {
    sessionId: "ignored",
    summary: "Applied the requested changes.",
  };

  await store.upsertSession(createSession());
  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger,
    bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(port, "/admin/api/sessions/session-1/approve", "{}");
  assert.equal(response.statusCode, 202);
  await waitForMicrotasks();

  assert.equal(agent.requests[0]?.prompt, "edit files");
  assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Applied the requested changes/);

  await admin.stop();
});

test("AdminServer reads and updates runtime settings", async () => {
  const store = new InMemoryStateStore();
  const config = createConfig();
  fs.mkdirSync("./.test-tmp", { recursive: true });
  fs.rmSync(config.settingsFile, { force: true });
  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge: createBridge(store).bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const before = await getJson(port, "/admin/api/settings");
  assert.equal(before.statusCode, 200);
  assert.equal(before.headers["cache-control"], "no-store, no-cache, must-revalidate");
  assert.equal(before.body.settings.agentKind, "codex");
  assert.equal(before.body.settings.defaultApprovalPolicy, "manual");
  assert.equal(before.body.settings.networkAccess, "disabled");
  assert.equal(before.body.settings.codexModel, "gpt-5.4");
  assert.equal(before.body.settings.claudeModel, "sonnet");
  assert.equal(before.body.settings.cursorCommand, "cursor-agent --trust");
  assert.equal(before.body.settings.cursorModel, "Auto");
  assert.equal(before.body.settings.cursorCommandTimeoutMs, 600000);
  assert.equal(before.body.settings.cursorMaxRuntimeMs, 3600000);
  assert.equal(before.body.settings.geminiCommand, "gemini --approval-mode yolo");
  assert.equal(before.body.settings.geminiModel, "gemini-2.5-pro");
  assert.equal(before.body.settings.geminiCommandTimeoutMs, 600000);
  assert.equal(before.body.settings.geminiMaxRuntimeMs, 3600000);
  assert.equal(before.body.settings.agentCommands.codex.command, "codex");
  assert.equal(before.body.settings.agentCommands["claude-code"].command, "claude");
  assert.equal(before.body.settings.agentCommands.cursor.command, "cursor-agent --trust");
  assert.equal(before.body.settings.agentCommands.gemini.command, "gemini --approval-mode yolo");
  assert.equal(before.body.settings.autoSplitThreadUserMessageCount, 30);
  assert.equal(before.body.settings.slackAllowedUserIds, "");
  assert.equal(before.body.settings.mattermostAllowedUserIds, "");
  assert.equal(before.body.settings.hasSlackBotToken, false);
  assert.match(before.body.settings.settingsFile, /admin-settings\.json$/);
  assert.equal(before.body.settings.settingsSavedAt, undefined);

  const update = await postJson(
    port,
    "/admin/api/settings",
    JSON.stringify({
      agentKind: "claude-code",
      defaultApprovalPolicy: "never",
      networkAccess: "enabled",
      autoSplitThreadUserMessageCount: 45,
      codexModel: "gpt-5.5",
      codexMaxTurns: 12,
      cursorCommand: "cursor-agent --print",
      cursorModel: "Auto",
      cursorCommandTimeoutMs: 222000,
      cursorMaxRuntimeMs: 333000,
      geminiCommand: "gemini --approval-mode yolo --sandbox workspace-write",
      geminiModel: "gemini-2.5-flash",
      geminiCommandTimeoutMs: 444000,
      geminiMaxRuntimeMs: 555000,
      claudeModel: "opus",
      claudeMaxTurns: 10,
      chatProvider: "mattermost",
      slackMode: "socket",
      mattermostMode: "websocket",
      mattermostActorMode: "self",
      slackAllowedUserIds: "U1\nU2",
      mattermostAllowedUserIds: "mm-1\nmm-2",
      host: "0.0.0.0",
      port: 9999,
      adminHost: "127.0.0.1",
      adminPort: 9998,
      defaultWorkingDirectory: "C:/repo",
      mattermostServerUrl: "https://chat.example.test",
      mattermostActionBaseUrl: "https://bridge.example.test",
      slackBotToken: "xoxb-test",
    }),
  );
  assert.equal(update.statusCode, 200);
  assert.equal(update.body.settings.agentKind, "claude-code");
  assert.equal(update.body.settings.defaultApprovalPolicy, "never");
  assert.equal(update.body.settings.networkAccess, "enabled");
  assert.equal(update.body.settings.autoSplitThreadUserMessageCount, 45);
  assert.equal(update.body.settings.codexModel, "gpt-5.5");
  assert.equal(update.body.settings.codexMaxTurns, 12);
  assert.equal(update.body.settings.cursorCommand, "cursor-agent --print");
  assert.equal(update.body.settings.cursorModel, "Auto");
  assert.equal(update.body.settings.cursorCommandTimeoutMs, 222000);
  assert.equal(update.body.settings.cursorMaxRuntimeMs, 333000);
  assert.equal(update.body.settings.agentCommands.cursor.command, "cursor-agent --print");
  assert.equal(
    update.body.settings.geminiCommand,
    "gemini --approval-mode yolo --sandbox workspace-write",
  );
  assert.equal(update.body.settings.geminiModel, "gemini-2.5-flash");
  assert.equal(update.body.settings.geminiCommandTimeoutMs, 444000);
  assert.equal(update.body.settings.geminiMaxRuntimeMs, 555000);
  assert.equal(
    update.body.settings.agentCommands.gemini.command,
    "gemini --approval-mode yolo --sandbox workspace-write",
  );
  assert.equal(update.body.settings.claudeModel, "opus");
  assert.equal(update.body.settings.claudeMaxTurns, 10);
  assert.equal(update.body.settings.slackAllowedUserIds, "U1\nU2");
  assert.equal(update.body.settings.mattermostAllowedUserIds, "mm-1\nmm-2");
  assert.equal(config.agentKind, "claude-code");
  assert.equal(config.defaultApprovalPolicy, "never");
  assert.equal(config.networkAccess, "enabled");
  assert.equal(config.autoSplitThreadUserMessageCount, 45);
  assert.equal(config.codexModel, "gpt-5.5");
  assert.equal(config.codexMaxTurns, 12);
  assert.equal(config.cursorCommand, "cursor-agent --print");
  assert.equal(config.cursorModel, "Auto");
  assert.equal(config.cursorCommandTimeoutMs, 222000);
  assert.equal(config.cursorMaxRuntimeMs, 333000);
  assert.equal(config.geminiCommand, "gemini --approval-mode yolo --sandbox workspace-write");
  assert.equal(config.geminiModel, "gemini-2.5-flash");
  assert.equal(config.geminiCommandTimeoutMs, 444000);
  assert.equal(config.geminiMaxRuntimeMs, 555000);
  assert.equal(config.claudeModel, "opus");
  assert.equal(config.claudeMaxTurns, 10);
  assert.equal(config.chatProvider, "mattermost");
  assert.deepEqual(config.slackAllowedUserIds, ["U1", "U2"]);
  assert.deepEqual(config.mattermostAllowedUserIds, ["mm-1", "mm-2"]);
  assert.equal(config.slackMode, "socket");
  assert.equal(config.mattermostMode, "websocket");
  assert.equal(config.mattermostActorMode, "self");
  assert.equal(config.port, 9999);
  assert.equal(config.adminPort, 9998);
  assert.equal(config.mattermostServerUrl, "https://chat.example.test");
  assert.equal(config.mattermostActionBaseUrl, "https://bridge.example.test");
  assert.equal(update.body.settings.hasSlackBotToken, true);
  assert.match(update.body.settings.settingsSavedAt, /T/);
  const persisted = JSON.parse(fs.readFileSync(config.settingsFile, "utf8")) as Record<string, any>;
  assert.equal(persisted.chatProvider, "mattermost");
  assert.equal(persisted.port, 9999);
  assert.equal(persisted.slackBotToken, "xoxb-test");
  assert.equal(persisted.autoSplitThreadUserMessageCount, 45);
  assert.equal(persisted.cursorCommand, "cursor-agent --print");
  assert.equal(persisted.cursorModel, "Auto");
  assert.equal(persisted.cursorCommandTimeoutMs, 222000);
  assert.equal(persisted.cursorMaxRuntimeMs, 333000);
  assert.equal(persisted.geminiCommand, "gemini --approval-mode yolo --sandbox workspace-write");
  assert.equal(persisted.geminiModel, "gemini-2.5-flash");
  assert.equal(persisted.geminiCommandTimeoutMs, 444000);
  assert.equal(persisted.geminiMaxRuntimeMs, 555000);
  assert.equal(persisted.slackAllowedUserIds, "U1\nU2");
  assert.equal(persisted.mattermostAllowedUserIds, "mm-1\nmm-2");

  const cursorUpdate = await postJson(
    port,
    "/admin/api/settings",
    JSON.stringify({
      agentKind: "cursor",
    }),
  );
  assert.equal(cursorUpdate.statusCode, 200);
  assert.equal(cursorUpdate.body.settings.agentKind, "cursor");
  assert.equal(config.agentKind, "cursor");

  const geminiUpdate = await postJson(
    port,
    "/admin/api/settings",
    JSON.stringify({
      agentKind: "gemini",
    }),
  );
  assert.equal(geminiUpdate.statusCode, 200);
  assert.equal(geminiUpdate.body.settings.agentKind, "gemini");
  assert.equal(config.agentKind, "gemini");

  const cleared = await postJson(
    port,
    "/admin/api/settings",
    JSON.stringify({
      clearSlackBotToken: true,
    }),
  );
  assert.equal(cleared.statusCode, 200);
  assert.equal(cleared.body.settings.hasSlackBotToken, false);
  assert.equal(config.slackBotToken, undefined);
  const persistedAfterClear = JSON.parse(fs.readFileSync(config.settingsFile, "utf8")) as Record<
    string,
    any
  >;
  assert.equal("slackBotToken" in persistedAfterClear, false);

  await admin.stop();
});

test("AdminServer queues bridge restart requests", async () => {
  const store = new InMemoryStateStore();
  const { bridge, auditLogger } = createBridge(store);
  let restartRequested = false;
  (bridge as unknown as { requestAdminRestart: () => Promise<void> }).requestAdminRestart = async () => {
    restartRequested = true;
  };

  const admin = new AdminServer({
    config: createConfig(),
    stateStore: store,
    auditLogger,
    bridge,
  });
  await admin.start();

  const port = admin.getListeningPort();
  assert.ok(port);

  const response = await postJson(port, "/admin/api/bridge/restart", "{}");
  assert.equal(response.statusCode, 202);
  assert.equal(restartRequested, true);

  await admin.stop();
});

async function getJson(
  port: number,
  path: string,
): Promise<{ statusCode: number; headers: http.IncomingHttpHeaders; body: Record<string, any> }> {
  const response = await getText(port, path);
  return {
    statusCode: response.statusCode,
    headers: response.headers,
    body: JSON.parse(response.body) as Record<string, any>,
  };
}

async function postJson(
  port: number,
  path: string,
  body: string,
): Promise<{ statusCode: number; headers: http.IncomingHttpHeaders; body: Record<string, any> }> {
  const response = await new Promise<{ statusCode: number; headers: http.IncomingHttpHeaders; body: string }>((resolve, reject) => {
    const request = http.request(
      {
        agent: false,
        host: "127.0.0.1",
        port,
        path,
        method: "POST",
        headers: {
          "content-type": "application/json",
          "content-length": Buffer.byteLength(body),
          connection: "close",
        },
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk) => {
          chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
        });
        response.on("end", () => {
          resolve({
            statusCode: response.statusCode ?? 0,
            headers: response.headers,
            body: Buffer.concat(chunks).toString("utf8"),
          });
        });
      },
    );

    request.on("error", reject);
    request.write(body);
    request.end();
  });

  return {
    statusCode: response.statusCode,
    headers: response.headers,
    body: JSON.parse(response.body) as Record<string, any>,
  };
}

async function getText(
  port: number,
  path: string,
): Promise<{ statusCode: number; headers: http.IncomingHttpHeaders; body: string }> {
  return await new Promise<{ statusCode: number; headers: http.IncomingHttpHeaders; body: string }>((resolve, reject) => {
    const request = http.request(
      {
        agent: false,
        host: "127.0.0.1",
        port,
        path,
        method: "GET",
        headers: {
          connection: "close",
        },
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk) => {
          chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
        });
        response.on("end", () => {
          resolve({
            statusCode: response.statusCode ?? 0,
            headers: response.headers,
            body: Buffer.concat(chunks).toString("utf8"),
          });
        });
      },
    );

    request.on("error", reject);
    request.end();
  });
}

function createBridge(store: InMemoryStateStore): {
  bridge: BridgeService;
  chatAdapter: RecordingChatAdapter;
  agent: RecordingAgent;
  auditLogger: RecordingAuditLogger;
} {
  const config = createConfig();
  const chatAdapter = new RecordingChatAdapter();
  const agent = new RecordingAgent("codex");
  const claudeAgent = new RecordingAgent("claude-code");
  const cursorAgent = new RecordingAgent("cursor");
  const geminiAgent = new RecordingAgent("gemini");
  const auditLogger = new RecordingAuditLogger();
  return {
    bridge: new BridgeService({
      config,
      stateStore: store,
      sessionManager: new SessionManager(store),
      jobQueue: new InlineJobQueue(),
      chatAdapter,
      agents: {
        codex: agent,
        "claude-code": claudeAgent,
        cursor: cursorAgent,
        gemini: geminiAgent,
      },
      auditLogger,
      launchDetachedRestart: () => {},
    }),
    chatAdapter,
    agent,
    auditLogger,
  };
}

async function waitForMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

async function startAdminWithConfig(config: AppConfig): Promise<{
  admin: AdminServer;
  port: number;
  store: InMemoryStateStore;
}> {
  const store = new InMemoryStateStore();
  fs.mkdirSync("./.test-tmp", { recursive: true });
  fs.rmSync(config.settingsFile, { force: true });
  const bridgeStore = new InMemoryStateStore();
  const bridge = new BridgeService({
    config,
    stateStore: bridgeStore,
    sessionManager: new SessionManager(bridgeStore),
    jobQueue: new InlineJobQueue(),
    chatAdapter: new RecordingChatAdapter(),
    agents: {
      codex: new RecordingAgent("codex"),
      "claude-code": new RecordingAgent("claude-code"),
      cursor: new RecordingAgent("cursor"),
      gemini: new RecordingAgent("gemini"),
    },
    auditLogger: new RecordingAuditLogger(),
    launchDetachedRestart: () => {},
  });
  const admin = new AdminServer({
    config,
    stateStore: store,
    auditLogger: new RecordingAuditLogger(),
    bridge,
  });
  await admin.start();
  const port = admin.getListeningPort();
  assert.ok(port);
  return { admin, port: port!, store };
}

test("admin GET settings returns allowed directory roots", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-allowed-get-"));
  const config = createConfig();
  config.allowedDirectories = [tempRoot];
  config.defaultWorkingDirectory = tempRoot;
  config.settingsFile = path.join(tempRoot, "settings.json");
  const { admin, port } = await startAdminWithConfig(config);
  try {
    const before = await getJson(port, "/admin/api/settings");
    assert.equal(before.statusCode, 200);
    assert.equal(before.body.settings.allowedDirectories, tempRoot);
  } finally {
    await admin.stop();
  }
});

test("admin POST applies new allowed roots and recomputes aliases", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-allowed-post-"));
  const rootA = path.join(tempRoot, "alpha");
  const rootB = path.join(tempRoot, "beta");
  fs.mkdirSync(rootA, { recursive: true });
  fs.mkdirSync(rootB, { recursive: true });
  const config = createConfig();
  config.allowedDirectories = [rootA];
  config.defaultWorkingDirectory = rootA;
  config.settingsFile = path.join(tempRoot, "settings.json");
  const { admin, port } = await startAdminWithConfig(config);
  try {
    const update = await postJson(
      port,
      "/admin/api/settings",
      JSON.stringify({ allowedDirectories: `${rootA}\n${rootB}` }),
    );
    assert.equal(update.statusCode, 200);
    assert.deepEqual(config.allowedDirectories, [path.resolve(rootA), path.resolve(rootB)]);
    assert.equal(path.resolve(config.repoAliases["beta"] ?? ""), path.resolve(rootB));
    assert.equal(update.body.settings.allowedDirectories, `${path.resolve(rootA)}\n${path.resolve(rootB)}`);

    const persisted = JSON.parse(fs.readFileSync(config.settingsFile, "utf8"));
    assert.ok(String(persisted.allowedDirs).includes(rootB));
  } finally {
    await admin.stop();
  }
});

test("admin POST rejects a relative allowed root", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-allowed-rel-"));
  const config = createConfig();
  config.allowedDirectories = [tempRoot];
  config.defaultWorkingDirectory = tempRoot;
  config.settingsFile = path.join(tempRoot, "settings.json");
  const { admin, port } = await startAdminWithConfig(config);
  try {
    const update = await postJson(
      port,
      "/admin/api/settings",
      JSON.stringify({ allowedDirectories: "./relative-root" }),
    );
    assert.equal(update.statusCode, 400);
    assert.match(update.body.error, /absolute path/);
    assert.deepEqual(config.allowedDirectories, [tempRoot]);
  } finally {
    await admin.stop();
  }
});

test("admin POST rejects a nonexistent allowed root", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-allowed-missing-"));
  const config = createConfig();
  config.allowedDirectories = [tempRoot];
  config.defaultWorkingDirectory = tempRoot;
  config.settingsFile = path.join(tempRoot, "settings.json");
  const missing = path.join(tempRoot, "does-not-exist");
  const { admin, port } = await startAdminWithConfig(config);
  try {
    const update = await postJson(
      port,
      "/admin/api/settings",
      JSON.stringify({ allowedDirectories: missing }),
    );
    assert.equal(update.statusCode, 400);
    assert.match(update.body.error, /does not exist or is not a directory/);
  } finally {
    await admin.stop();
  }
});

test("admin POST rejects new roots that orphan the default working directory", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-allowed-orphan-"));
  const rootA = path.join(tempRoot, "current");
  const rootB = path.join(tempRoot, "other");
  fs.mkdirSync(rootA, { recursive: true });
  fs.mkdirSync(rootB, { recursive: true });
  const config = createConfig();
  config.allowedDirectories = [rootA];
  config.defaultWorkingDirectory = rootA;
  config.settingsFile = path.join(tempRoot, "settings.json");
  const { admin, port } = await startAdminWithConfig(config);
  try {
    const update = await postJson(
      port,
      "/admin/api/settings",
      JSON.stringify({ allowedDirectories: rootB }),
    );
    assert.equal(update.statusCode, 400);
    assert.match(update.body.error, /outside the new allowed roots/);
    assert.deepEqual(config.allowedDirectories, [rootA]);
  } finally {
    await admin.stop();
  }
});
