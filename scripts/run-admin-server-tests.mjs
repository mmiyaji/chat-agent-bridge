import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

// Point BridgeService's restart-completion queue at a throwaway temp file before
// any BridgeService is constructed. The default resolves to the repo's real
// data/runtime/restart-completion.jsonl; these fixtures use channel C1/thread T1
// and must never leak into production runtime state.
if (!process.env.CAB_RESTART_COMPLETION_FILE) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cab-admin-server-tests-"));
  process.env.CAB_RESTART_COMPLETION_FILE = path.join(tempRoot, "restart-completion.jsonl");
}

import { AdminServer } from "../.test-dist/src/admin/server.js";
import { BridgeService } from "../.test-dist/src/core/bridge-service.js";
import { SessionManager } from "../.test-dist/src/core/session-manager.js";
import {
  RecordingAuditLogger,
  InMemoryStateStore,
  RecordingAgent,
  RecordingChatAdapter,
  InlineJobQueue,
} from "../.test-dist/test/helpers/fakes.js";

class ThrowingExecutionStateStore extends InMemoryStateStore {
  async listExecutionRecords() {
    throw new Error("execution-store-offline");
  }
}

function createConfig() {
  return {
    chatProvider: "slack",
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    repoAliases: { repo: "C:/repo" },
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
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

function createSession(overrides = {}) {
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

function createRecord(overrides = {}) {
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

function createBridge(store) {
  const config = createConfig();
  const chatAdapter = new RecordingChatAdapter();
  const agent = new RecordingAgent("codex");
  const claudeAgent = new RecordingAgent("claude-code");
  const cursorAgent = new RecordingAgent("cursor");
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
      },
      auditLogger,
      launchDetachedRestart: () => {},
    }),
    chatAdapter,
    agent,
    auditLogger,
  };
}

async function getText(port, path) {
  return await new Promise((resolve, reject) => {
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
        const chunks = [];
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

async function getJson(port, path) {
  const response = await getText(port, path);
  return {
    statusCode: response.statusCode,
    headers: response.headers,
    body: JSON.parse(response.body),
  };
}

async function postJson(port, path, body) {
  const response = await new Promise((resolve, reject) => {
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
        const chunks = [];
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
    body: JSON.parse(response.body),
  };
}

async function waitForMicrotasks() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

test("admin-server integration scenarios", async () => {
  {
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
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const sessionsResponse = await getJson(port, "/admin/api/sessions");
      assert.equal(sessionsResponse.statusCode, 200);
      assert.equal(sessionsResponse.body.sessions.length, 1);
      const approvalsResponse = await getJson(port, "/admin/api/approvals");
      assert.equal(approvalsResponse.statusCode, 200);
      assert.equal(approvalsResponse.body.approvals.length, 1);
      const failuresResponse = await getJson(port, "/admin/api/failures?limit=1");
      assert.equal(failuresResponse.statusCode, 200);
      assert.equal(failuresResponse.body.failures.length, 1);
      const dashboard = await getText(port, "/admin/legacy");
      assert.equal(dashboard.statusCode, 200);
      assert.match(dashboard.body, /Bridge Admin/);
      const settingsPage = await getText(port, "/admin/settings");
      assert.equal(settingsPage.statusCode, 200);
      assert.equal(settingsPage.headers["cache-control"], "no-store, no-cache, must-revalidate");
    } finally {
      await admin.stop();
    }
  }

  {
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
    try {
      const port = admin.getListeningPort();
      assert.ok(port);

      const sessionsResponse = await getJson(port, "/admin/api/sessions");
      assert.equal(sessionsResponse.statusCode, 200);
      const session = sessionsResponse.body.sessions[0];
      assert.equal(session.agent, "claude-code");
      assert.equal(session.connectionMode, "attached");
      assert.equal(session.agentSessionId, "claude-abc");
      assert.equal(session.approvalPolicy, "manual");
      assert.equal(session.workdirConfirmed, true);
      assert.equal(session.pendingWorkdirPicker, false);
      assert.equal(session.pendingSessionPicker, false);

      const environmentResponse = await getJson(port, "/admin/api/environment");
      assert.equal(environmentResponse.statusCode, 200);
      const environment = environmentResponse.body.environment;
      assert.equal(environment.defaultWorkingDirectory, "C:/repo");
      assert.deepEqual(environment.allowedDirectories, ["C:/repo"]);
      assert.deepEqual(environment.repoAliases, { repo: "C:/repo" });
      assert.deepEqual(environment.channelDefaultWorkingDirectories, { "slack:C1": "C:/repo" });
      assert.equal(environment.claudeProjectsDir, "C:/repo/data/claude-projects");
      assert.equal(environment.agentKind, "codex");
      assert.equal(environment.chatProvider, "slack");
      assert.ok(Array.isArray(environment.agentEnvironmentVariables));
      assert.ok(environment.agentEnvironmentVariables.includes("CHAT_AGENT_BRIDGE_WORKDIR"));

      const serialized = JSON.stringify(environmentResponse.body);
      assert.doesNotMatch(serialized, /xoxb-super-secret/);
      assert.doesNotMatch(serialized, /TOKEN|SECRET|PASSWORD/i);

      const dashboard = await getText(port, "/admin/legacy");
      assert.match(dashboard.body, /data-testid="environment-panel"/);
      assert.match(dashboard.body, /data-testid="environment-channel-defaults"/);
    } finally {
      await admin.stop();
    }
  }

  {
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
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const dashboard = await getText(port, "/admin/legacy");
      assert.equal(dashboard.statusCode, 200);
      assert.match(dashboard.body, /dashboard-warning/);
      const session = await getJson(port, "/admin/api/sessions/session-1");
      assert.equal(session.statusCode, 200);
      assert.deepEqual(session.body.executionHistory, []);
      assert.equal(auditLogger.events.some((event) => event.type === "admin.dashboard.degraded"), true);
    } finally {
      await admin.stop();
    }
  }

  {
    const store = new InMemoryStateStore();
    const admin = new AdminServer({
      config: createConfig(),
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    });
    await admin.start();
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const onboarding = await getText(port, "/admin/onboarding");
      assert.equal(onboarding.statusCode, 200);
      assert.match(onboarding.body, /Bridge Onboarding/);
      assert.match(onboarding.body, /data-testid="onboarding-step-1"/);
      assert.match(onboarding.body, /data-testid="onboarding-step-2"/);
      assert.match(onboarding.body, /data-testid="onboarding-step-3"/);
      assert.match(onboarding.body, /data-testid="onboarding-step-4"/);
      assert.match(onboarding.body, /data-testid="provider-card-mattermost"/);
      assert.match(onboarding.body, /data-testid="provider-card-slack"/);
      assert.match(onboarding.body, /data-testid="agent-card-claude-code"/);
      assert.match(onboarding.body, /data-testid="test-connection"/);
      const terms = await getText(port, "/admin/terms");
      assert.equal(terms.statusCode, 200);
      const privacy = await getText(port, "/admin/privacy");
      assert.equal(privacy.statusCode, 200);

      const settingsPage = await getText(port, "/admin/settings");
      assert.equal(settingsPage.statusCode, 200);
      assert.match(settingsPage.body, /data-testid="settings-group-basics"/);
      assert.match(settingsPage.body, /data-testid="settings-group-connection"/);
      assert.match(settingsPage.body, /data-testid="settings-group-agent-tuning"/);
      assert.match(settingsPage.body, /data-testid="settings-group-server-ports"/);
      assert.match(settingsPage.body, /data-testid="settings-group-secret-cleanup"/);
    } finally {
      await admin.stop();
    }
  }

  {
    // Dashboard setup-incomplete banner: shown when the selected provider is
    // missing required connection settings, absent once they are filled in.
    const store = new InMemoryStateStore();
    const incompleteConfig = createConfig();
    incompleteConfig.chatProvider = "mattermost";
    delete incompleteConfig.mattermostServerUrl;
    delete incompleteConfig.mattermostBotToken;
    const incompleteAdmin = new AdminServer({
      config: incompleteConfig,
      stateStore: store,
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    });
    await incompleteAdmin.start();
    try {
      const port = incompleteAdmin.getListeningPort();
      assert.ok(port);
      const dashboard = await getText(port, "/admin/legacy");
      assert.equal(dashboard.statusCode, 200);
      assert.match(dashboard.body, /data-testid="setup-incomplete-banner"/);
      assert.match(dashboard.body, /Setup incomplete/);
      assert.match(dashboard.body, /Mattermost server URL/);
      assert.match(dashboard.body, /Mattermost bot token/);
      assert.match(dashboard.body, /data-testid="connection-chip"/);
    } finally {
      await incompleteAdmin.stop();
    }

    const completeConfig = createConfig();
    completeConfig.chatProvider = "mattermost";
    completeConfig.mattermostServerUrl = "https://chat.example.test";
    completeConfig.mattermostBotToken = "token-value";
    const completeAdmin = new AdminServer({
      config: completeConfig,
      stateStore: new InMemoryStateStore(),
      auditLogger: new RecordingAuditLogger(),
      bridge: createBridge(store).bridge,
    });
    await completeAdmin.start();
    try {
      const port = completeAdmin.getListeningPort();
      assert.ok(port);
      const dashboard = await getText(port, "/admin/legacy");
      assert.equal(dashboard.statusCode, 200);
      assert.doesNotMatch(dashboard.body, /data-testid="setup-incomplete-banner"/);
    } finally {
      await completeAdmin.stop();
    }
  }

  {
    // /admin/api/test-connection: success with supplied values, fallback to
    // stored config, failure without leaking tokens, and the Slack path.
    const store = new InMemoryStateStore();
    let capturedUrl;
    let capturedHeaders;
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
          return { username: "bot-user" };
        },
      },
    );
    await admin.start();
    try {
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
    } finally {
      await admin.stop();
    }
  }

  {
    const store = new InMemoryStateStore();
    const config = createConfig();
    config.mattermostServerUrl = "https://stored.example.test";
    config.mattermostBotToken = "stored-token";
    let capturedHeaders;
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
          return { username: "stored-user" };
        },
      },
    );
    await admin.start();
    try {
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
    } finally {
      await admin.stop();
    }
  }

  {
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
    try {
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
    } finally {
      await admin.stop();
    }
  }

  {
    const store = new InMemoryStateStore();
    let capturedUrl;
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
          return { ok: true, user: "slack-user", team: "slack-team" };
        },
      },
    );
    await admin.start();
    try {
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
    } finally {
      await admin.stop();
    }
  }

  {
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
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const response = await postJson(port, "/admin/api/sessions/session-1/approve", "{}");
      assert.equal(response.statusCode, 202);
      await waitForMicrotasks();
      assert.equal(agent.requests[0]?.prompt, "edit files");
      assert.match(chatAdapter.posted.at(-1)?.text ?? "", /Applied the requested changes/);
    } finally {
      await admin.stop();
    }
  }

  {
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
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const before = await getJson(port, "/admin/api/settings");
      assert.equal(before.statusCode, 200);
      assert.equal(before.body.settings.cursorCommand, "cursor-agent --trust");
      assert.equal(before.body.settings.cursorModel, "Auto");

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
          claudeModel: "opus",
          claudeMaxTurns: 10,
          chatProvider: "mattermost",
          slackMode: "socket",
          mattermostMode: "websocket",
          mattermostActorMode: "self",
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
      assert.equal(update.body.settings.cursorCommand, "cursor-agent --print");
      assert.equal(update.body.settings.cursorModel, "Auto");

      const cursorUpdate = await postJson(
        port,
        "/admin/api/settings",
        JSON.stringify({ agentKind: "cursor" }),
      );
      assert.equal(cursorUpdate.statusCode, 200);
      assert.equal(cursorUpdate.body.settings.agentKind, "cursor");

      const cleared = await postJson(
        port,
        "/admin/api/settings",
        JSON.stringify({ clearSlackBotToken: true }),
      );
      assert.equal(cleared.statusCode, 200);
      assert.equal(cleared.body.settings.hasSlackBotToken, false);
    } finally {
      await admin.stop();
    }
  }

  {
    const store = new InMemoryStateStore();
    const { bridge, auditLogger } = createBridge(store);
    let restartRequested = false;
    bridge.requestAdminRestart = async () => {
      restartRequested = true;
    };
    const admin = new AdminServer({
      config: createConfig(),
      stateStore: store,
      auditLogger,
      bridge,
    });
    await admin.start();
    try {
      const port = admin.getListeningPort();
      assert.ok(port);
      const response = await postJson(port, "/admin/api/bridge/restart", "{}");
      assert.equal(response.statusCode, 202);
      assert.equal(restartRequested, true);
    } finally {
      await admin.stop();
    }
  }
});
