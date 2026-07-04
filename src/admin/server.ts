import http, { IncomingMessage, ServerResponse } from "node:http";
import fs from "node:fs";
import path from "node:path";
import { AuditLogger } from "../core/audit.js";
import { resolveCommand } from "../core/command-resolution.js";
import { readPendingApproval } from "../core/approval.js";
import { BridgeService } from "../core/bridge-service.js";
import { assertPathAllowed } from "../core/paths.js";
import { computeRepoAliases } from "../config/env.js";
import { updatePersistedSettings } from "../config/settings-io.js";
import { AppConfig, ExecutionRecord, JsonValue, SessionBinding } from "../core/types.js";
import { StateStore } from "../state/state-store.js";
import { ChatConnectionStatus } from "../adapters/chat.js";

interface AdminServerDeps {
  config: AppConfig;
  stateStore: StateStore;
  auditLogger: AuditLogger;
  bridge: BridgeService;
}

interface DashboardData {
  sessions: Array<Record<string, any>>;
  approvals: Array<Record<string, any>>;
  failures: Array<Record<string, any>>;
  connectionStatus?: ChatConnectionStatus;
  repoAliases?: Record<string, string>;
  environment?: EnvironmentPayload;
  warnings?: string[];
}

interface EnvironmentPayload {
  defaultWorkingDirectory: string;
  allowedDirectories: string[];
  repoAliases: Record<string, string>;
  channelDefaultWorkingDirectories: Record<string, string>;
  claudeProjectsDir: string;
  agentKind: AppConfig["agentKind"];
  chatProvider: AppConfig["chatProvider"];
  agentEnvironmentVariables: string[];
}

interface RuntimeSettingsPayload {
  agentKind: AppConfig["agentKind"];
  defaultApprovalPolicy: AppConfig["defaultApprovalPolicy"];
  networkAccess: AppConfig["networkAccess"];
  chatProvider: AppConfig["chatProvider"];
  slackAllowedUserIds: string;
  mattermostAllowedUserIds: string;
  slackMode: AppConfig["slackMode"];
  mattermostMode: AppConfig["mattermostMode"];
  mattermostActorMode: NonNullable<AppConfig["mattermostActorMode"]>;
  host: AppConfig["host"];
  port: AppConfig["port"];
  adminHost: NonNullable<AppConfig["adminHost"]>;
  adminPort: NonNullable<AppConfig["adminPort"]>;
  defaultWorkingDirectory: AppConfig["defaultWorkingDirectory"];
  /** Allowed working-directory roots, one absolute path per line. */
  allowedDirectories: string;
  mattermostServerUrl?: NonNullable<AppConfig["mattermostServerUrl"]>;
  mattermostActionBaseUrl?: NonNullable<AppConfig["mattermostActionBaseUrl"]>;
  mattermostDmOnly: boolean;
  hasSlackBotToken: boolean;
  hasSlackAppToken: boolean;
  hasSlackSigningSecret: boolean;
  hasMattermostBotToken: boolean;
  hasMattermostWebhookToken: boolean;
  slackBotToken?: string;
  slackAppToken?: string;
  slackSigningSecret?: string;
  mattermostBotToken?: string;
  mattermostWebhookToken?: string;
  clearSlackBotToken?: boolean;
  clearSlackAppToken?: boolean;
  clearSlackSigningSecret?: boolean;
  clearMattermostBotToken?: boolean;
  clearMattermostWebhookToken?: boolean;
  codexModel: AppConfig["codexModel"];
  codexMaxTurns: AppConfig["codexMaxTurns"];
  codexCommandTimeoutMs: AppConfig["codexCommandTimeoutMs"];
  codexMaxRuntimeMs: AppConfig["codexMaxRuntimeMs"];
  codexMaxOutputLength: AppConfig["codexMaxOutputLength"];
  cursorCommand: NonNullable<AppConfig["cursorCommand"]>;
  cursorModel: NonNullable<AppConfig["cursorModel"]>;
  cursorCommandTimeoutMs: NonNullable<AppConfig["cursorCommandTimeoutMs"]>;
  cursorMaxRuntimeMs: NonNullable<AppConfig["cursorMaxRuntimeMs"]>;
  geminiCommand: NonNullable<AppConfig["geminiCommand"]>;
  geminiModel: AppConfig["geminiModel"];
  geminiCommandTimeoutMs: NonNullable<AppConfig["geminiCommandTimeoutMs"]>;
  geminiMaxRuntimeMs: NonNullable<AppConfig["geminiMaxRuntimeMs"]>;
  claudeModel: AppConfig["claudeModel"];
  claudeMaxTurns: AppConfig["claudeMaxTurns"];
  claudeCommandTimeoutMs: AppConfig["claudeCommandTimeoutMs"];
  claudeMaxRuntimeMs: AppConfig["claudeMaxRuntimeMs"];
  agentCommands: {
    codex: ReturnType<typeof resolveCommand>;
    "claude-code": ReturnType<typeof resolveCommand>;
    cursor: ReturnType<typeof resolveCommand>;
    gemini: ReturnType<typeof resolveCommand>;
  };
  autoSplitThreadUserMessageCount: AppConfig["autoSplitThreadUserMessageCount"];
  settingsFile: string;
  settingsSavedAt?: string;
}

function writeJson(response: ServerResponse, statusCode: number, body: unknown): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "application/json; charset=utf-8");
  response.setHeader("cache-control", "no-store, no-cache, must-revalidate");
  response.setHeader("pragma", "no-cache");
  response.setHeader("expires", "0");
  response.end(JSON.stringify(body));
}

function writeHtml(response: ServerResponse, statusCode: number, body: string): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "text/html; charset=utf-8");
  response.setHeader("cache-control", "no-store, no-cache, must-revalidate");
  response.setHeader("pragma", "no-cache");
  response.setHeader("expires", "0");
  response.end(body);
}

async function readRequestBody(request: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }
  return Buffer.concat(chunks).toString("utf8");
}

function escapeHtml(text: string | number): string {
  return String(text)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function readApprovalPolicyOverride(session: SessionBinding): string | undefined {
  const value = session.bridgeState?.approvalPolicyOverride;
  return typeof value === "string" ? value : undefined;
}

function readConnectionMode(session: SessionBinding): string {
  const value = session.bridgeState?.connectionMode;
  return typeof value === "string" ? value : "bridge-owned";
}

type SessionEnvironment = ReturnType<BridgeService["describeSessionEnvironment"]>;

function summarizeSession(
  session: SessionBinding,
  latestExecution: ExecutionRecord | undefined,
  environment?: SessionEnvironment,
): Record<string, unknown> {
  const pendingApproval = readPendingApproval(session.bridgeState);
  return {
    sessionId: session.sessionId,
    agent: session.agent,
    provider: session.thread.provider,
    channelId: session.thread.channelId,
    threadId: session.thread.threadId,
    workingDirectory: session.workingDirectory,
    updatedAt: session.updatedAt,
    connectionMode: environment?.connectionMode ?? readConnectionMode(session),
    approvalPolicyOverride: readApprovalPolicyOverride(session) ?? "default",
    ...(environment
      ? {
          agentSessionId: environment.agentSessionId,
          approvalPolicy: environment.approvalPolicy,
          workdirConfirmed: environment.workdirConfirmed,
          pendingWorkdirPicker: environment.pendingWorkdirPicker,
          pendingSessionPicker: environment.pendingSessionPicker,
        }
      : {}),
    pendingApproval:
      pendingApproval &&
      {
        prompt: pendingApproval.prompt,
        requestedAt: pendingApproval.requestedAt,
        reason: pendingApproval.reason,
        executionId: pendingApproval.executionId ?? null,
      },
    latestExecution:
      latestExecution &&
      {
        executionId: latestExecution.executionId,
        status: latestExecution.status,
        updatedAt: latestExecution.updatedAt,
        prompt: latestExecution.prompt,
        summary: latestExecution.summary ?? latestExecution.error ?? "",
      },
  };
}

function renderButtonMarkup(input: {
  label: string;
  action: "cancel" | "approve" | "reject";
  sessionId: string;
}): string {
  return `<button data-action="${escapeHtml(input.action)}" data-session-id="${escapeHtml(input.sessionId)}">${escapeHtml(input.label)}</button>`;
}

function renderTableMarkup(input: {
  testId: string;
  headers: string[];
  rows: string[][];
}): string {
  if (input.rows.length === 0) {
    return `<div class="empty" data-testid="${escapeHtml(input.testId)}-empty">No entries.</div>`;
  }

  const head = input.headers.map((header) => `<th>${escapeHtml(header)}</th>`).join("");
  const body = input.rows
    .map((row) => `<tr>${row.map((cell) => `<td>${cell}</td>`).join("")}</tr>`)
    .join("");
  return `<div class="table-wrap"><table data-testid="${escapeHtml(input.testId)}"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

function renderApprovalCardsMarkup(entries: Array<Record<string, any>>): string {
  if (entries.length === 0) {
    return `<div class="empty" data-testid="approvals-table-empty">No entries.</div>`;
  }

  return `<div class="card-list" data-testid="approvals-table">${entries
    .map(
      (entry) => `<article class="compact-card">
        <header>
          <div>
            <div class="title">${escapeHtml(entry.prompt)}</div>
            <div class="meta"><code>${escapeHtml(entry.sessionId)}</code><span>${escapeHtml(entry.requestedAt)}</span></div>
          </div>
          <span class="pill">${escapeHtml(entry.agent ?? "agent")}</span>
        </header>
        <p class="muted-note">Thread: <code>${escapeHtml(entry.provider)}:${escapeHtml(entry.threadId)}</code></p>
        <div class="actions">
          ${renderButtonMarkup({ label: "Approve", action: "approve", sessionId: entry.sessionId })}
          ${renderButtonMarkup({ label: "Reject", action: "reject", sessionId: entry.sessionId })}
        </div>
      </article>`,
    )
    .join("")}</div>`;
}

function renderFailureCardsMarkup(entries: Array<Record<string, any>>): string {
  if (entries.length === 0) {
    return `<div class="empty" data-testid="failures-table-empty">No entries.</div>`;
  }

        return `<div class="card-list" data-testid="failures-table">${entries
    .map(
      (entry) => `<article class="compact-card">
        <header>
          <div class="meta"><code>${escapeHtml(entry.sessionId)}</code><span>${escapeHtml(entry.updatedAt ?? "")}</span></div>
          <span class="status-${escapeHtml(entry.status)}">${escapeHtml(entry.status)}</span>
        </header>
        <div class="title">${escapeHtml(entry.error || entry.summary || "")}</div>
        <p class="muted-note">Category: <code>${escapeHtml(entry.failureCategory ?? "error")}</code></p>
      </article>`,
    )
    .join("")}</div>`;
}

function renderCommandResolutionCard(
  label: string,
  diagnostic: RuntimeSettingsPayload["agentCommands"]["codex"],
): string {
  const resolved =
    diagnostic.status === "resolved" && diagnostic.resolvedPath
      ? `<code>${escapeHtml(diagnostic.resolvedPath)}</code>`
      : '<span class="muted-note">Not resolved on this host.</span>';
  const source = diagnostic.source ? `<span class="pill">${escapeHtml(diagnostic.source)}</span>` : "";
  return `<article class="compact-card">
    <header>
      <div class="title">${escapeHtml(label)}</div>
      ${source}
    </header>
    <p class="muted-note">Configured command: <code>${escapeHtml(diagnostic.command)}</code></p>
    <p class="muted-note">Executable token: <code>${escapeHtml(diagnostic.executable || "(empty)")}</code></p>
    <div class="title">${resolved}</div>
  </article>`;
}

function renderKeyValueList(
  entries: Array<[string, string]>,
  testId: string,
): string {
  if (entries.length === 0) {
    return `<div class="empty" data-testid="${escapeHtml(testId)}-empty">None.</div>`;
  }

  const rows = entries
    .map(
      ([key, value]) =>
        `<div><dt>${escapeHtml(key)}</dt><dd><code>${escapeHtml(value)}</code></dd></div>`,
    )
    .join("");
  return `<dl class="kv-list" data-testid="${escapeHtml(testId)}">${rows}</dl>`;
}

function renderEnvironmentSection(environment: EnvironmentPayload | undefined): string {
  if (!environment) {
    return `<div class="detail-empty" data-testid="environment-empty">Environment details are unavailable.</div>`;
  }

  const overview = renderKeyValueList(
    [
      ["Default working directory", environment.defaultWorkingDirectory],
      ["Default agent", environment.agentKind],
      ["Chat provider", environment.chatProvider],
      ["Claude projects dir", environment.claudeProjectsDir],
    ],
    "environment-overview",
  );
  const allowed = renderKeyValueList(
    environment.allowedDirectories.map((dir, index) => [`root ${index + 1}`, dir]),
    "environment-allowed",
  );
  const aliases = renderKeyValueList(
    Object.entries(environment.repoAliases).sort(([left], [right]) => left.localeCompare(right)),
    "environment-aliases",
  );
  const channelDefaults = renderKeyValueList(
    Object.entries(environment.channelDefaultWorkingDirectories).sort(([left], [right]) =>
      left.localeCompare(right),
    ),
    "environment-channel-defaults",
  );
  const envVars =
    environment.agentEnvironmentVariables.length === 0
      ? `<div class="empty">None.</div>`
      : `<ul class="env-var-list" data-testid="environment-env-vars">${environment.agentEnvironmentVariables
          .map((name) => `<li><code>${escapeHtml(name)}</code></li>`)
          .join("")}</ul>`;

  return `<div class="environment-grid" data-testid="environment-body">
    <div class="settings-block">
      <div class="section-head"><div><h2>Overview</h2></div></div>
      ${overview}
    </div>
    <div class="settings-block">
      <div class="section-head"><div><h2>Allowed Directories</h2></div></div>
      ${allowed}
    </div>
    <div class="settings-block">
      <div class="section-head"><div><h2>Repo Aliases</h2></div></div>
      ${aliases}
    </div>
    <div class="settings-block">
      <div class="section-head"><div><h2>Channel Default Directories</h2></div></div>
      ${channelDefaults}
    </div>
    <div class="settings-block">
      <div class="section-head"><div><h2>Agent Environment Variables</h2></div></div>
      <p class="muted-note">These variable names are set per session for each agent process. Values are omitted; tokens and secrets are never exposed here.</p>
      ${envVars}
    </div>
  </div>`;
}

function formatDuration(ms: number | undefined): string | undefined {
  if (typeof ms !== "number" || !Number.isFinite(ms) || ms < 0) {
    return undefined;
  }

  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) {
    return `${hours}h ${minutes}m ${seconds}s`;
  }
  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }
  return `${seconds}s`;
}

function serializeUserIdList(userIds: string[]): string {
  return userIds.join("\n");
}

function parseUserIdList(value: string | undefined): string[] {
  if (!value) {
    return [];
  }

  return value
    .split(/[;,\r\n]/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

function classifyExecutionIssue(record: ExecutionRecord): string {
  if (record.status === "cancelled") {
    return "cancelled";
  }
  if (record.status === "rejected") {
    return "rejected";
  }
  if (record.status === "waiting_for_approval") {
    return "approval";
  }
  const text = `${record.summary ?? ""} ${record.error ?? ""}`.toLowerCase();
  if (text.includes("timeout")) {
    return "timeout";
  }
  if (text.includes("approval")) {
    return "approval";
  }
  if (text.includes("cancel")) {
    return "cancelled";
  }
  if (text.includes("network")) {
    return "network";
  }
  if (text.includes("permission") || text.includes("eacces") || text.includes("eperm")) {
    return "permission";
  }
  return record.status === "failed" ? "error" : record.status;
}

function buildDashboardWarning(message: string): string {
  return `<div class="warning-banner" data-testid="dashboard-warning">${escapeHtml(message)}</div>`;
}

const ADMIN_GITHUB_URL = "https://github.com/mmiyaji/chat-agent-bridge";
const ADMIN_AUTHOR_NAME = "mmiyaji";

function renderAdminFooter(): string {
  const year = new Date().getFullYear();
  return `
    <footer class="site-footer">
      <div class="footer-links">
        <a href="/admin/terms">Terms</a>
        <a href="/admin/privacy">Privacy</a>
        <span>Author: ${escapeHtml(ADMIN_AUTHOR_NAME)}</span>
        <span>&copy; ${escapeHtml(year)}</span>
        <a href="${escapeHtml(ADMIN_GITHUB_URL)}" target="_blank" rel="noreferrer">GitHub</a>
      </div>
      <div class="footer-controls">
        <label>
          Language
          <select id="footer-language">
            <option value="en">English</option>
            <option value="ja">日本語</option>
          </select>
        </label>
        <label>
          Theme
          <select id="footer-theme">
            <option value="dark">Dark</option>
            <option value="light">Light</option>
            <option value="system">System</option>
          </select>
        </label>
      </div>
    </footer>`;
}

function renderAdminFooterScript(): string {
  return `
      const footerTheme = document.getElementById("footer-theme");
      const footerLanguage = document.getElementById("footer-language");
      const THEME_STORAGE_KEY = "bridge-admin-theme";
      const LANGUAGE_STORAGE_KEY = "bridge-admin-language";

      function applyTheme(theme) {
        const resolved = theme || "dark";
        document.documentElement.dataset.theme = resolved;
        if (footerTheme) {
          footerTheme.value = resolved;
        }
      }

      function applyLanguage(language) {
        const resolved = language || "en";
        document.documentElement.lang = resolved;
        if (footerLanguage) {
          footerLanguage.value = resolved;
        }
        const jaOption = document.querySelector("#footer-language option[value='ja']");
        if (jaOption) {
          jaOption.textContent = "日本語";
        }
      }

      applyTheme(localStorage.getItem(THEME_STORAGE_KEY) || "dark");
      applyLanguage(localStorage.getItem(LANGUAGE_STORAGE_KEY) || "en");

      footerTheme?.addEventListener("change", (event) => {
        const value = event.target.value;
        localStorage.setItem(THEME_STORAGE_KEY, value);
        applyTheme(value);
      });

      footerLanguage?.addEventListener("change", (event) => {
        const value = event.target.value;
        localStorage.setItem(LANGUAGE_STORAGE_KEY, value);
        applyLanguage(value);
      });
  `;
}

function renderAdminLegalPage(input: { title: string; body: string }): string {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>${escapeHtml(input.title)}</title>
    <style>
      :root {
        color-scheme: dark;
        --bg: #0f172a;
        --panel: rgba(15, 23, 42, 0.88);
        --panel-alt: rgba(30, 41, 59, 0.78);
        --ink: #e2e8f0;
        --muted: #94a3b8;
        --line: #334155;
        --accent: #38bdf8;
      }
      :root[data-theme="light"] {
        color-scheme: light;
        --bg: #f4f7fb;
        --panel: rgba(255, 255, 255, 0.96);
        --panel-alt: rgba(241, 245, 249, 0.92);
        --ink: #0f172a;
        --muted: #475569;
        --line: #cbd5e1;
        --accent: #0ea5e9;
      }
      body { margin: 0; background: var(--bg); color: var(--ink); font: 14px/1.6 "Segoe UI", sans-serif; }
      main { max-width: 1680px; margin: 0 auto; padding: 32px 20px 48px; }
      .shell { background: var(--panel); border: 1px solid var(--line); border-radius: 20px; padding: 24px; }
      .hero { display: flex; justify-content: space-between; gap: 20px; align-items: start; margin-bottom: 18px; }
      .hero-copy { max-width: 640px; }
      .hero-side { display: grid; gap: 12px; justify-items: end; }
      .hero-meta { color: var(--muted); font-size: 12px; }
      .nav { display: inline-flex; gap: 8px; padding: 4px; border: 1px solid var(--line); border-radius: 999px; background: var(--panel-alt); }
      .nav a { color: var(--ink); text-decoration: none; padding: 8px 14px; border-radius: 999px; }
      h1 { margin: 0 0 12px; font-size: 30px; }
      p, li { color: var(--muted); }
      a { color: var(--accent); }
      .site-footer { margin-top: 18px; display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; border-top: 1px solid var(--line); padding-top: 16px; color: var(--muted); }
      .footer-links, .footer-controls { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }
      .footer-controls label { display: inline-flex; gap: 8px; align-items: center; }
      .footer-controls select { border-radius: 999px; border: 1px solid var(--line); background: var(--panel-alt); color: var(--ink); padding: 6px 10px; }
    </style>
  </head>
  <body>
    <main>
      <section class="shell">
        <div class="hero">
          <div class="hero-copy">
            <h1>${escapeHtml(input.title)}</h1>
            ${input.body}
          </div>
          <div class="hero-side">
            <nav class="nav" aria-label="Admin navigation">
              <a href="/admin">Dashboard</a>
              <a href="/admin/settings">Settings</a>
              <a href="/admin/onboarding">Onboarding</a>
            </nav>
            <div class="hero-meta">Local admin surface</div>
          </div>
        </div>
      </section>
      ${renderAdminFooter()}
    </main>
    <script>
${renderAdminFooterScript()}
    </script>
  </body>
</html>`;
}

function renderDashboard(input: DashboardData, view: "dashboard" | "settings"): string {
  const isSettingsView = view === "settings";
  const sessionCount = input.sessions.length;
  const approvalCount = input.approvals.length;
  const failureCount = input.failures.length;
  const connectionStatus = input.connectionStatus;
  const connectionStateLabel = connectionStatus
    ? `${connectionStatus.provider} ${connectionStatus.mode}`
    : "chat transport";
  const connectionStateValue = connectionStatus
    ? connectionStatus.connected
      ? "Connected"
      : connectionStatus.state === "connecting"
        ? "Connecting"
        : connectionStatus.state === "listening"
          ? "Listening"
          : "Disconnected"
    : "Unknown";
  const connectionStatusMarkup = connectionStatus
    ? `<div class="detail-card" data-testid="connection-status-card"><strong>${escapeHtml(connectionStateLabel)}</strong><span class="status-${escapeHtml(connectionStatus.connected ? "completed" : connectionStatus.state === "connecting" ? "running" : "failed")}">${escapeHtml(connectionStateValue)}</span><p>${escapeHtml(connectionStatus.statusText)}</p>${
        connectionStatus.lastChangedAt
          ? `<code>${escapeHtml(connectionStatus.lastChangedAt)}</code>`
          : ""
      }${
        connectionStatus.lastConnectedAt
          ? `<p>Last connected: <code>${escapeHtml(connectionStatus.lastConnectedAt)}</code></p>`
          : ""
      }${
        connectionStatus.lastDisconnectedAt
          ? `<p>Last disconnected: <code>${escapeHtml(connectionStatus.lastDisconnectedAt)}</code></p>`
          : ""
      }${
        connectionStatus.lastRecoveredAt
          ? `<p>Last recovered: <code>${escapeHtml(connectionStatus.lastRecoveredAt)}</code>${formatDuration(connectionStatus.lastOutageDurationMs) ? ` after ${escapeHtml(formatDuration(connectionStatus.lastOutageDurationMs) ?? "")}` : ""}</p>`
          : ""
      }<p>Reconnects: <strong>${escapeHtml(connectionStatus.reconnectCount ?? 0)}</strong> · Disconnects: <strong>${escapeHtml(connectionStatus.disconnectCount ?? 0)}</strong></p>${
        connectionStatus.lastError
          ? `<p>Last error: ${escapeHtml(connectionStatus.lastError)}${connectionStatus.lastErrorAt ? ` <code>${escapeHtml(connectionStatus.lastErrorAt)}</code>` : ""}</p>`
          : ""
      }</div>`
    : `<div class="detail-card" data-testid="connection-status-card"><strong>chat transport</strong><span>Unknown</span><p>No adapter connection status was reported.</p></div>`;
  const renderCancelAction = (session: Record<string, any>): string => {
    return session.latestExecution?.status === "running"
      ? renderButtonMarkup({
          label: "Cancel",
          action: "cancel",
          sessionId: session.sessionId,
        })
      : `<span class="muted-note">Idle</span>`;
  };
  const INACTIVE_THRESHOLD_MS = 7 * 24 * 60 * 60 * 1000;
  const now = Date.now();
  const classifyActivity = (session: Record<string, any>): "active" | "inactive" => {
    const lastTime = session.latestExecution?.updatedAt ?? session.updatedAt;
    if (!lastTime) return "inactive";
    return now - new Date(lastTime as string).getTime() > INACTIVE_THRESHOLD_MS ? "inactive" : "active";
  };
  const sortedSessions = [...input.sessions].sort((a, b) => {
    const aActive = classifyActivity(a) === "active" ? 0 : 1;
    const bActive = classifyActivity(b) === "active" ? 0 : 1;
    return aActive - bActive;
  });
  const sessionsMarkup = renderTableMarkup({
    testId: "sessions-table",
    headers: ["Session", "Agent", "Workspace", "Thread", "State", "Action"],
    rows: sortedSessions.map((session) => {
      const activity = classifyActivity(session);
      const rowClass = activity === "inactive" ? ' class="row-inactive"' : "";
      const inactiveLabel = activity === "inactive" ? ' <span class="pill pill-muted">inactive</span>' : "";
      return [
        `<div class="session-cell"${rowClass}><strong>${escapeHtml(session.sessionId)}</strong><span>${escapeHtml(session.connectionMode)}</span></div>`,
        `<span class="pill">${escapeHtml(session.agent)}</span>`,
        `<code>${escapeHtml(session.workingDirectory)}</code>`,
        `<code>${escapeHtml(session.provider)}:${escapeHtml(session.threadId)}</code>`,
        `<div class="session-cell"><span class="status-${escapeHtml(session.latestExecution?.status ?? "idle")}">${escapeHtml(session.latestExecution?.status ?? "idle")}</span>${inactiveLabel}<span>${escapeHtml(session.latestExecution?.updatedAt ?? session.updatedAt)}</span></div>`,
        renderCancelAction(session),
      ];
    }),
  });
  const approvalsMarkup = renderApprovalCardsMarkup(input.approvals);
  const failuresMarkup = renderFailureCardsMarkup(input.failures);
  const environmentMarkup = renderEnvironmentSection(input.environment);
  const warningsMarkup =
    input.warnings && input.warnings.length > 0
      ? input.warnings.map((warning) => buildDashboardWarning(warning)).join("")
      : "";
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Chat Agent Bridge Admin</title>
    <style>
      :root {
        color-scheme: dark;
        --bg: #0f172a;
        --panel: rgba(15, 23, 42, 0.88);
        --panel-alt: rgba(30, 41, 59, 0.78);
        --ink: #e2e8f0;
        --muted: #94a3b8;
        --line: #334155;
        --accent: #38bdf8;
        --warn: #f59e0b;
        --fail: #f87171;
        --success: #4ade80;
      }
      :root[data-theme="light"] {
        color-scheme: light;
        --bg: #f4f7fb;
        --panel: rgba(255, 255, 255, 0.96);
        --panel-alt: rgba(241, 245, 249, 0.92);
        --ink: #0f172a;
        --muted: #475569;
        --line: #cbd5e1;
        --accent: #0ea5e9;
        --warn: #b45309;
        --fail: #b91c1c;
        --success: #166534;
      }
      body {
        margin: 0;
        font: 14px/1.5 "Segoe UI", sans-serif;
        color: var(--ink);
        background: var(--bg);
      }
      main {
        max-width: 1680px;
        margin: 0 auto;
        padding: 28px 20px 48px;
      }
      h1 {
        margin: 0 0 8px;
        font-size: 32px;
      }
      p {
        margin: 0;
        color: var(--muted);
      }
      .grid {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: 18px;
        margin-top: 24px;
        align-items: start;
      }
      .hero {
        display: flex;
        justify-content: space-between;
        gap: 20px;
        align-items: start;
      }
      .hero-copy {
        max-width: 760px;
      }
      .hero-copy p {
        margin-top: 6px;
      }
      .hero-side {
        display: grid;
        gap: 12px;
        justify-items: end;
      }
      .hero-meta {
        color: var(--muted);
        font-size: 12px;
      }
      .nav {
        display: inline-flex;
        gap: 8px;
        padding: 4px;
        border: 1px solid var(--line);
        border-radius: 999px;
        background: var(--panel-alt);
      }
      .nav a {
        display: inline-flex;
        align-items: center;
        border-radius: 999px;
        padding: 8px 14px;
        color: var(--ink);
        text-decoration: none;
      }
      .nav a.is-active {
        background: rgba(56, 189, 248, 0.16);
        border: 1px solid var(--line);
      }
      .stats {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }
      .stat {
        min-width: 120px;
        padding: 10px 14px;
        border: 1px solid var(--line);
        border-radius: 14px;
        background: var(--panel-alt);
      }
      .stat strong {
        display: block;
        font-size: 22px;
        line-height: 1.1;
      }
      .stat span {
        color: var(--muted);
        font-size: 12px;
      }
      section {
        background: var(--panel);
        border: 1px solid var(--line);
        border-radius: 18px;
        padding: 18px;
        overflow: hidden;
      }
      section[data-testid="sessions-panel"] {
        grid-column: 1;
        grid-row: 1;
      }
      section[data-testid="approvals-panel"] {
        grid-column: 1;
        grid-row: 3;
      }
      section[data-testid="failures-panel"] {
        grid-column: 1;
        grid-row: 3;
      }
      section[data-testid="settings-panel"] {
        grid-column: 1;
        grid-row: 2;
      }
      .secondary-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 18px;
      }
      h2 {
        margin: 0;
        font-size: 16px;
        letter-spacing: 0.02em;
      }
      .section-head {
        display: flex;
        justify-content: space-between;
        align-items: start;
        gap: 12px;
        margin-bottom: 12px;
      }
      .section-head p {
        font-size: 12px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        table-layout: auto;
      }
      section[data-testid="sessions-panel"] table {
        min-width: 980px;
      }
      th, td {
        padding: 10px 12px 10px 0;
        border-bottom: 1px solid var(--line);
        text-align: left;
        vertical-align: top;
        overflow-wrap: anywhere;
        word-break: break-word;
      }
      th {
        font-size: 12px;
        color: var(--muted);
        position: sticky;
        top: 0;
        background: var(--panel);
        z-index: 1;
      }
      .status-running { color: var(--accent); }
      .status-waiting_for_approval { color: var(--warn); }
      .status-failed, .status-rejected, .status-cancelled { color: var(--fail); }
      .status-completed { color: var(--success); }
      .muted-note {
        color: var(--muted);
        font-size: 12px;
      }
      code {
        font-family: Consolas, monospace;
        font-size: 12px;
        white-space: normal;
        overflow-wrap: anywhere;
      }
      .empty {
        color: var(--muted);
      }
      .warning-banner {
        margin: 0 0 14px;
        padding: 12px 14px;
        border-radius: 14px;
        border: 1px solid rgba(245, 158, 11, 0.55);
        background: rgba(245, 158, 11, 0.14);
        color: #fde68a;
      }
      .table-wrap {
        width: 100%;
        overflow-x: auto;
        overflow-y: auto;
        max-height: 72vh;
        padding-right: 6px;
      }
      .filters {
        display: grid;
        grid-template-columns: minmax(220px, 1.2fr) repeat(3, minmax(120px, 0.55fr));
        gap: 12px;
        margin: 14px 0 12px;
      }
      .field {
        display: grid;
        gap: 4px;
      }
      .field label {
        color: var(--muted);
        font-size: 12px;
      }
      .field input, .field select {
        width: 100%;
        box-sizing: border-box;
        border: 1px solid var(--line);
        border-radius: 12px;
        padding: 9px 12px;
        background: var(--panel-alt);
        color: var(--ink);
      }
      .table-wrap tr.is-selected {
        background: rgba(56, 189, 248, 0.12);
      }
      .session-cell {
        display: grid;
        gap: 2px;
      }
      .session-cell strong {
        font-size: 13px;
      }
      .session-cell span:last-child {
        color: var(--muted);
        font-size: 12px;
      }
      .pill {
        display: inline-flex;
        align-items: center;
        border: 1px solid var(--line);
        border-radius: 999px;
        padding: 2px 10px;
        font-size: 12px;
        background: rgba(56, 189, 248, 0.12);
      }
      .pill-muted {
        background: rgba(148, 163, 184, 0.15);
        color: var(--muted);
        border-color: var(--muted);
      }
      .row-inactive { opacity: 0.5; }
      .mini-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .card-list {
        display: grid;
        gap: 12px;
      }
      .compact-card {
        border: 1px solid var(--line);
        border-radius: 16px;
        padding: 14px 16px;
        background: var(--panel-alt);
      }
      .compact-card header {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        align-items: start;
        margin-bottom: 8px;
      }
      .compact-card p {
        margin: 0;
      }
      .compact-card .meta {
        display: grid;
        gap: 4px;
        color: var(--muted);
        font-size: 12px;
      }
      .compact-card .title {
        font-weight: 600;
        margin-bottom: 6px;
      }
      .compact-card .actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        margin-top: 12px;
      }
      .detail-grid {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 12px;
        margin-bottom: 18px;
      }
      .settings-grid {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 12px;
        margin: 12px 0;
      }
      .settings-subgrid {
        display: grid;
        grid-template-columns: repeat(4, minmax(0, 1fr));
        gap: 12px;
        margin: 12px 0;
      }
      .settings-stack {
        display: grid;
        gap: 18px;
      }
      .settings-block {
        border: 1px solid var(--line);
        border-radius: 16px;
        padding: 16px;
        background: var(--panel-alt);
      }
      .environment-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 12px;
      }
      .kv-list {
        display: grid;
        gap: 8px;
        margin: 0;
      }
      .kv-list dt {
        color: var(--muted);
        font-size: 12px;
      }
      .kv-list dd {
        margin: 2px 0 0;
      }
      .env-var-list {
        margin: 0;
        padding-left: 18px;
        display: grid;
        gap: 4px;
      }
      .settings-block > .section-head {
        margin-bottom: 10px;
      }
      .detail-card {
        border: 1px solid var(--line);
        border-radius: 14px;
        padding: 12px 14px;
        background: var(--panel-alt);
      }
      .site-footer {
        margin-top: 18px;
        display: flex;
        justify-content: space-between;
        gap: 16px;
        flex-wrap: wrap;
        border-top: 1px solid var(--line);
        padding-top: 16px;
        color: var(--muted);
      }
      .footer-links, .footer-controls {
        display: flex;
        gap: 12px;
        flex-wrap: wrap;
        align-items: center;
      }
      .site-footer a {
        color: var(--accent);
      }
      .footer-controls label {
        display: inline-flex;
        gap: 8px;
        align-items: center;
      }
      .footer-controls select {
        border-radius: 999px;
        border: 1px solid var(--line);
        background: var(--panel-alt);
        color: var(--ink);
        padding: 6px 10px;
      }
      .detail-card strong {
        display: block;
        margin-bottom: 4px;
        font-size: 12px;
        color: var(--muted);
        letter-spacing: 0.03em;
        text-transform: uppercase;
      }
      .detail-card span, .detail-card code {
        display: block;
      }
      .detail-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        margin-bottom: 14px;
      }
      .detail-empty {
        border: 1px dashed var(--line);
        border-radius: 14px;
        padding: 20px;
        color: var(--muted);
      }
      .detail-drawer {
        position: fixed;
        inset: 0;
        display: none;
        align-items: stretch;
        justify-content: flex-end;
        background: rgba(15, 23, 42, 0.46);
        z-index: 20;
      }
      .detail-drawer.is-open {
        display: flex;
      }
      .detail-drawer-shell {
        width: min(720px, 100%);
        height: 100%;
        background: var(--bg);
        border-left: 1px solid var(--line);
        padding: 24px 20px;
        overflow-y: auto;
      }
      .detail-drawer-header {
        display: flex;
        justify-content: space-between;
        align-items: start;
        gap: 12px;
        margin-bottom: 16px;
      }
      .detail-drawer-header p {
        margin-top: 6px;
      }
      .detail-close {
        margin: 0;
      }
      .history-list {
        display: grid;
        gap: 10px;
      }
      .history-item {
        border: 1px solid var(--line);
        border-radius: 14px;
        padding: 12px 14px;
        background: var(--panel-alt);
      }
      .history-item header {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        margin-bottom: 6px;
      }
      .history-item p {
        margin-top: 6px;
      }
      .toolbar {
        display: flex;
        justify-content: space-between;
        align-items: end;
        gap: 12px;
        flex-wrap: wrap;
      }
      .toolbar-group {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
        align-items: center;
      }
      .flash {
        margin-top: 12px;
        padding: 10px 14px;
        border-radius: 12px;
        border: 1px solid rgba(56, 189, 248, 0.35);
        background: rgba(56, 189, 248, 0.1);
      }
      .flash[data-kind="error"] {
        border-color: rgba(248, 113, 113, 0.45);
        background: rgba(248, 113, 113, 0.12);
        color: var(--fail);
      }
      .flash[data-kind="success"] {
        border-color: rgba(74, 222, 128, 0.35);
        background: rgba(74, 222, 128, 0.1);
        color: var(--success);
      }
      .toggle {
        display: inline-flex;
        align-items: flex-start;
        gap: 8px;
        color: var(--muted);
        font-size: 12px;
        line-height: 1.4;
      }
      .toggle input {
        margin: 2px 0 0;
        flex: 0 0 auto;
      }
      .secret-reset-list {
        display: grid;
        gap: 10px;
      }
      .secret-reset-item {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        padding: 12px 14px;
        border: 1px solid var(--line);
        border-radius: 14px;
        background: var(--bg);
      }
      .secret-reset-copy {
        display: grid;
        gap: 4px;
      }
      .secret-reset-copy strong {
        font-size: 13px;
      }
      .secret-reset-copy span {
        color: var(--muted);
        font-size: 12px;
      }
      .toggle-chip {
        position: relative;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 118px;
        padding: 8px 12px;
        border: 1px solid var(--line);
        border-radius: 999px;
        background: var(--panel-alt);
        color: var(--ink);
        cursor: pointer;
        user-select: none;
      }
      .toggle-chip input {
        position: absolute;
        inset: 0;
        opacity: 0;
        pointer-events: none;
      }
      .toggle-chip.is-active {
        border-color: var(--accent);
        background: rgba(56, 189, 248, 0.12);
      }
      button {
        border: 1px solid var(--line);
        background: var(--panel-alt);
        color: var(--ink);
        border-radius: 999px;
        padding: 8px 14px;
        cursor: pointer;
        margin: 0 6px 6px 0;
        transition: transform 120ms ease, border-color 120ms ease, background 120ms ease;
      }
      button:hover {
        transform: translateY(-1px);
        border-color: var(--accent);
        background: rgba(56, 189, 248, 0.12);
      }
      @media (max-width: 720px) {
        main {
          padding: 20px 12px 32px;
        }
        .hero {
          align-items: start;
          flex-direction: column;
        }
        .grid {
          grid-template-columns: 1fr;
        }
        .secondary-grid {
          grid-template-columns: 1fr;
        }
        section[data-testid="sessions-panel"],
        section[data-testid="approvals-panel"],
        section[data-testid="failures-panel"] {
          grid-column: auto;
          grid-row: auto;
        }
        .filters,
        .detail-grid {
          grid-template-columns: 1fr;
        }
        .settings-grid {
          grid-template-columns: 1fr;
        }
        .settings-subgrid {
          grid-template-columns: 1fr;
        }
        .environment-grid {
          grid-template-columns: 1fr;
        }
        .detail-drawer-shell {
          width: 100%;
          padding: 20px 12px;
        }
        section[data-testid="sessions-panel"] table {
          min-width: 760px;
        }
      }
    </style>
  </head>
  <body>
    <main>
      ${warningsMarkup}
      <div class="hero">
        <div class="hero-copy">
          <h1>Bridge Admin</h1>
          <p>${isSettingsView ? "Bridge configuration and connection settings." : "Sessions, approvals, and failures from the local bridge state."}</p>
        </div>
        <div class="hero-side">
          <nav class="nav" aria-label="Admin navigation">
            <a href="/admin" class="${isSettingsView ? "" : "is-active"}" data-testid="nav-dashboard">Dashboard</a>
            <a href="/admin/settings" class="${isSettingsView ? "is-active" : ""}" data-testid="nav-settings">Settings</a>
            <a href="/admin/onboarding" data-testid="nav-onboarding">Onboarding</a>
          </nav>
          <div class="stats">
            <div class="stat" data-testid="sessions-stat"><strong>${sessionCount}</strong><span>Sessions</span></div>
            <div class="stat" data-testid="approvals-stat"><strong>${approvalCount}</strong><span>Pending</span></div>
            <div class="stat" data-testid="failures-stat"><strong>${failureCount}</strong><span>Recent failures</span></div>
            <div class="stat" data-testid="connection-stat"><strong>${escapeHtml(connectionStateValue)}</strong><span>${escapeHtml(connectionStateLabel)}</span></div>
          </div>
          <div class="hero-meta">${isSettingsView ? "Runtime settings" : "Operational dashboard"}</div>
        </div>
      </div>
      <div class="toolbar"${isSettingsView ? ' style="display:none"' : ""}>
        <div class="toolbar-group">
          <label class="toggle" for="auto-refresh">
            <input id="auto-refresh" data-testid="auto-refresh" type="checkbox" />
            Auto refresh
          </label>
        </div>
        <div class="toolbar-group">
          <button id="refresh">Refresh</button>
        </div>
      </div>
      <div id="flash"></div>
      <div class="grid">
        <section data-testid="sessions-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Sessions</h2>
              <p>Primary workspace view. Long thread and session values wrap without widening the whole page.</p>
            </div>
          </div>
          <div class="filters">
            <div class="field">
              <label for="session-search">Search</label>
              <input id="session-search" data-testid="session-search" type="search" placeholder="session id, cwd, thread id" />
            </div>
            <div class="field">
              <label for="agent-filter">Agent</label>
              <select id="agent-filter" data-testid="agent-filter">
                <option value="">All</option>
              </select>
            </div>
            <div class="field">
              <label for="provider-filter">Provider</label>
              <select id="provider-filter" data-testid="provider-filter">
                <option value="">All</option>
              </select>
            </div>
            <div class="field">
              <label for="status-filter">Status</label>
              <select id="status-filter" data-testid="status-filter">
                <option value="">All</option>
              </select>
            </div>
          </div>
          <div id="sessions">${sessionsMarkup}</div>
        </section>
        <section data-testid="connection-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Chat Connection</h2>
              <p>Live transport state for the active chat adapter.</p>
            </div>
          </div>
          <div id="connection-status">${connectionStatusMarkup}</div>
        </section>
        <div class="secondary-grid"${isSettingsView ? ' style="display:none"' : ""}>
          <section data-testid="approvals-panel">
            <div class="section-head">
              <div>
                <h2>Pending Approvals</h2>
                <p>Approval queue stays grouped below the primary sessions table.</p>
              </div>
            </div>
            <div id="approvals">${approvalsMarkup}</div>
          </section>
          <section data-testid="failures-panel">
            <div class="section-head">
              <div>
                <h2>Recent Failures</h2>
                <p>Failure summaries stay nearby, but no longer compete with the sessions list for width.</p>
              </div>
            </div>
            <div id="failures">${failuresMarkup}</div>
          </section>
        </div>
        <section data-testid="environment-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Environment</h2>
              <p>Read-only view of where and how sessions run: allowed roots, aliases, channel defaults, and the bridge variables passed to agents.</p>
            </div>
          </div>
          <div id="environment">${environmentMarkup}</div>
        </section>
        <section data-testid="settings-panel"${isSettingsView ? "" : ' style="display:none"'}>
          <div class="section-head">
            <div>
              <h2>Runtime Settings</h2>
              <p>These defaults apply immediately to new sessions and future runs. Transport settings remain read-only here and still require restart.</p>
            </div>
          </div>
          <div id="settings" data-testid="settings-panel-body">
            <div class="detail-empty">Loading runtime settings.</div>
          </div>
        </section>
      </div>
      <aside id="detail-drawer" class="detail-drawer"${isSettingsView ? ' style="display:none"' : ""}>
        <div class="detail-drawer-shell">
          <div class="detail-drawer-header">
            <div>
              <h2>Session Detail</h2>
              <p>Inspect the selected session, current approval state, and recent execution history.</p>
            </div>
            <button id="detail-close" class="detail-close" type="button">Close</button>
          </div>
          <div id="detail" data-testid="session-detail">
            <div class="detail-empty">Select a session from the sessions table to inspect details and available actions.</div>
          </div>
        </div>
      </aside>
      ${renderAdminFooter()}
    </main>
    <script>
      ${renderAdminFooterScript()}
      let dashboardState = {
        sessions: [],
        approvals: [],
        failures: [],
        connectionStatus: null,
        repoAliases: {},
        selectedSessionId: null,
        filtersRestored: false,
        settings: null,
      };
      const hasDashboardView = Boolean(document.getElementById("sessions"));
      const hasSettingsView = Boolean(document.getElementById("settings"));
      let autoRefreshTimer = null;
      const FILTER_STORAGE_KEY = "bridge-admin-filters";
      const AUTO_REFRESH_STORAGE_KEY = "bridge-admin-auto-refresh";

      function canCancelSession(session) {
        return session?.latestExecution?.status === "running";
      }

      function setFlash(message, kind) {
        const flash = document.getElementById("flash");
        if (!flash) {
          return;
        }
        if (!message) {
          flash.innerHTML = "";
          return;
        }
        flash.innerHTML = '<div class="flash" data-kind="' + escapeHtml(kind || "info") + '">' + escapeHtml(message) + "</div>";
      }

      function readSavedFilters() {
        try {
          return JSON.parse(localStorage.getItem(FILTER_STORAGE_KEY) || "{}");
        } catch {
          return {};
        }
      }

      function persistFilters() {
        if (!hasDashboardView) {
          return;
        }
        const payload = {
          search: document.getElementById("session-search").value,
          agent: document.getElementById("agent-filter").value,
          provider: document.getElementById("provider-filter").value,
          status: document.getElementById("status-filter").value,
          selectedSessionId: dashboardState.selectedSessionId,
        };
        localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(payload));
      }

      function restoreFilters() {
        if (!hasDashboardView) {
          return;
        }
        const saved = readSavedFilters();
        document.getElementById("session-search").value = saved.search || "";
        document.getElementById("agent-filter").value = saved.agent || "";
        document.getElementById("provider-filter").value = saved.provider || "";
        document.getElementById("status-filter").value = saved.status || "";
        dashboardState.selectedSessionId = saved.selectedSessionId || null;
      }

      function applyAutoRefreshSetting() {
        if (!hasDashboardView) {
          return;
        }
        const enabled = localStorage.getItem(AUTO_REFRESH_STORAGE_KEY) === "1";
        const input = document.getElementById("auto-refresh");
        input.checked = enabled;
        if (autoRefreshTimer) {
          clearInterval(autoRefreshTimer);
          autoRefreshTimer = null;
        }
        if (enabled) {
          autoRefreshTimer = setInterval(() => {
            void load(false);
          }, 15000);
        }
      }

      async function fetchJson(path) {
        const response = await fetch(path);
        if (!response.ok) {
          throw new Error(path + " returned " + response.status);
        }
        return await response.json();
      }

      async function postAction(path) {
        const response = await fetch(path, { method: "POST" });
        if (!response.ok) {
          const body = await response.text();
          throw new Error(path + " returned " + response.status + ": " + body);
        }
        return await response.json();
      }

      function escapeHtml(text) {
        return String(text)
          .replaceAll("&", "&amp;")
          .replaceAll("<", "&lt;")
          .replaceAll(">", "&gt;")
          .replaceAll('"', "&quot;")
          .replaceAll("'", "&#39;");
      }

      function formatDuration(ms) {
        if (typeof ms !== "number" || !Number.isFinite(ms) || ms < 0) {
          return "";
        }
        const totalSeconds = Math.floor(ms / 1000);
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const seconds = totalSeconds % 60;
        if (hours > 0) {
          return hours + "h " + minutes + "m " + seconds + "s";
        }
        if (minutes > 0) {
          return minutes + "m " + seconds + "s";
        }
        return seconds + "s";
      }

      function renderButtonMarkup(label, sessionId, action) {
        return [
          '<button data-action="',
          escapeHtml(action),
          '" data-session-id="',
          escapeHtml(sessionId),
          '">',
          escapeHtml(label),
          "</button>",
        ].join("");
      }

      function renderSessionTableRows(sessions) {
        return sessions.map((session) => [
          "<code>" + escapeHtml(session.workingDirectory) + "</code>",
          '<div class="session-cell"><span class="status-' + escapeHtml(session.latestExecution?.status ?? "idle") + '">' +
            escapeHtml(session.latestExecution?.status ?? "idle") + '</span><span>' + escapeHtml(session.latestExecution?.updatedAt ?? session.updatedAt) + "</span></div>",
          '<div class="session-cell"><strong>' + escapeHtml(session.sessionId) + "</strong><span>" + escapeHtml(session.connectionMode) + "</span></div>",
          '<span class="pill">' + escapeHtml(session.agent) + "</span>",
          "<code>" + escapeHtml(session.provider) + ":" + escapeHtml(session.threadId) + "</code>",
          '<div class="mini-actions"><button data-session-detail="' + escapeHtml(session.sessionId) + '">Open</button>' +
            (canCancelSession(session) ? renderButtonMarkup("Cancel", session.sessionId, "cancel") : '<span class="muted-note">Idle</span>') + "</div>",
        ]);
      }

      function renderTableMarkup(testId, headers, rows) {
        if (!rows.length) {
          return '<div class="empty" data-testid="' + escapeHtml(testId) + '-empty">No entries.</div>';
        }
        const head = headers.map((header) => "<th>" + escapeHtml(header) + "</th>").join("");
        const body = rows
          .map((row) => "<tr>" + row.map((cell) => "<td>" + cell + "</td>").join("") + "</tr>")
          .join("");
        return [
          '<div class="table-wrap"><table data-testid="',
          escapeHtml(testId),
          '"><thead><tr>',
          head,
          "</tr></thead><tbody>",
          body,
          "</tbody></table></div>",
        ].join("");
      }

      function renderApprovalCards(entries) {
        if (!entries.length) {
          return '<div class="empty" data-testid="approvals-table-empty">No entries.</div>';
        }

        return '<div class="card-list" data-testid="approvals-table">' + entries.map((entry) =>
          '<article class="compact-card">' +
            '<header><div><div class="title">' + escapeHtml(entry.prompt) + '</div><div class="meta"><code>' + escapeHtml(entry.sessionId) + '</code><span>' + escapeHtml(entry.requestedAt) + '</span></div></div><span class="pill">' + escapeHtml(entry.agent || "agent") + '</span></header>' +
            '<p class="muted-note">Thread: <code>' + escapeHtml(entry.provider) + ":" + escapeHtml(entry.threadId) + "</code></p>" +
            '<div class="actions">' +
              renderButtonMarkup("Approve", entry.sessionId, "approve") +
              renderButtonMarkup("Reject", entry.sessionId, "reject") +
            "</div>" +
          "</article>"
        ).join("") + "</div>";
      }

      function renderFailureCards(entries) {
        if (!entries.length) {
          return '<div class="empty" data-testid="failures-table-empty">No entries.</div>';
        }

        return '<div class="card-list" data-testid="failures-table">' + entries.map((entry) =>
          '<article class="compact-card">' +
            '<header><div class="meta"><code>' + escapeHtml(entry.sessionId) + '</code><span>' + escapeHtml(entry.updatedAt || "") + '</span></div><span class="status-' + escapeHtml(entry.status) + '">' + escapeHtml(entry.status) + '</span></header>' +
            '<div class="title">' + escapeHtml(entry.error || entry.summary || "") + '</div>' +
            '<p class="muted-note">Category: <code>' + escapeHtml(entry.failureCategory || "error") + '</code></p>' +
          "</article>"
        ).join("") + "</div>";
      }

      function renderConnectionStatus(status) {
        if (!status) {
          return '<div class="detail-card" data-testid="connection-status-card"><strong>chat transport</strong><span>Unknown</span><p>No adapter connection status was reported.</p></div>';
        }
        const stateValue = status.connected
          ? "Connected"
          : status.state === "connecting"
            ? "Connecting"
            : status.state === "listening"
              ? "Listening"
              : "Disconnected";
        const statusClass = status.connected
          ? "completed"
          : status.state === "connecting"
            ? "running"
            : "failed";
        return '<div class="detail-card" data-testid="connection-status-card">' +
          '<strong>' + escapeHtml(status.provider + " " + status.mode) + '</strong>' +
          '<span class="status-' + escapeHtml(statusClass) + '">' + escapeHtml(stateValue) + '</span>' +
          '<p>' + escapeHtml(status.statusText || "") + '</p>' +
          (status.lastChangedAt ? '<code>' + escapeHtml(status.lastChangedAt) + '</code>' : "") +
          (status.lastConnectedAt ? '<p>Last connected: <code>' + escapeHtml(status.lastConnectedAt) + '</code></p>' : '') +
          (status.lastDisconnectedAt ? '<p>Last disconnected: <code>' + escapeHtml(status.lastDisconnectedAt) + '</code></p>' : '') +
          (status.lastRecoveredAt ? '<p>Last recovered: <code>' + escapeHtml(status.lastRecoveredAt) + '</code>' + (formatDuration(status.lastOutageDurationMs) ? ' after ' + escapeHtml(formatDuration(status.lastOutageDurationMs)) : '') + '</p>' : '') +
          '<p>Reconnects: <strong>' + escapeHtml(status.reconnectCount ?? 0) + '</strong> · Disconnects: <strong>' + escapeHtml(status.disconnectCount ?? 0) + '</strong></p>' +
          (status.lastError ? '<p>Last error: ' + escapeHtml(status.lastError) + (status.lastErrorAt ? ' <code>' + escapeHtml(status.lastErrorAt) + '</code>' : '') + '</p>' : "") +
          "</div>";
      }

      function renderSecretResetItem(id, label, description) {
        return '<div class="secret-reset-item">' +
          '<div class="secret-reset-copy"><strong>' + escapeHtml(label) + '</strong><span>' + escapeHtml(description) + "</span></div>" +
          '<label class="toggle-chip" data-secret-toggle-label="' + escapeHtml(id) + '">' +
            '<input id="' + escapeHtml(id) + '" data-testid="' + escapeHtml(id) + '" data-secret-toggle="1" type="checkbox" />' +
            '<span>Clear on save</span>' +
          "</label>" +
        "</div>";
      }

      function renderFieldWithHint(fieldMarkup, hint) {
        return '<div class="field-with-hint">' +
          fieldMarkup +
          '<div class="hint">' + hint + "</div>" +
        "</div>";
      }

      function renderCommandResolutionCard(label, diagnostic) {
        const safeDiagnostic = diagnostic || {
          command: "",
          executable: "",
          status: "not_found",
          resolvedPath: "",
          source: "",
        };
        const resolved =
          safeDiagnostic.status === "resolved" && safeDiagnostic.resolvedPath
            ? "<code>" + escapeHtml(safeDiagnostic.resolvedPath) + "</code>"
            : '<span class="muted-note">Not resolved on this host.</span>';
        const source = safeDiagnostic.source
          ? '<span class="pill">' + escapeHtml(safeDiagnostic.source) + "</span>"
          : "";
        return '<article class="compact-card">' +
          "<header>" +
            '<div class="title">' + escapeHtml(label) + "</div>" +
            source +
          "</header>" +
          '<p class="muted-note">Configured command: <code>' + escapeHtml(safeDiagnostic.command) + "</code></p>" +
          '<p class="muted-note">Executable token: <code>' + escapeHtml(safeDiagnostic.executable || "(empty)") + "</code></p>" +
          '<div class="title">' + resolved + "</div>" +
        "</article>";
      }

      function syncSecretToggleStates() {
        document.querySelectorAll("[data-secret-toggle-label]").forEach((label) => {
          const input = label.querySelector("input[data-secret-toggle='1']");
          if (!(input instanceof HTMLInputElement)) {
            return;
          }
          label.classList.toggle("is-active", input.checked);
        });
      }

      function collectOptions(values) {
        return Array.from(new Set(values.filter(Boolean))).sort();
      }

      function syncFilterOptions() {
        const agentFilter = document.getElementById("agent-filter");
        const providerFilter = document.getElementById("provider-filter");
        const statusFilter = document.getElementById("status-filter");
        const agents = collectOptions(dashboardState.sessions.map((session) => session.agent));
        const providers = collectOptions(dashboardState.sessions.map((session) => session.provider));
        const statuses = collectOptions(
          dashboardState.sessions.map((session) => session.latestExecution?.status ?? "idle"),
        );
        for (const [element, values] of [[agentFilter, agents], [providerFilter, providers], [statusFilter, statuses]]) {
          const currentValue = element.value;
          element.innerHTML = '<option value="">All</option>' + values
            .map((value) => '<option value="' + escapeHtml(value) + '">' + escapeHtml(value) + "</option>")
            .join("");
          element.value = values.includes(currentValue) ? currentValue : "";
        }
      }

      function getFilteredSessions() {
        const search = document.getElementById("session-search").value.trim().toLowerCase();
        const agent = document.getElementById("agent-filter").value;
        const provider = document.getElementById("provider-filter").value;
        const status = document.getElementById("status-filter").value;
        return dashboardState.sessions.filter((session) => {
          const haystack = [
            session.sessionId,
            session.workingDirectory,
            session.threadId,
            session.channelId,
            session.connectionMode,
            session.latestExecution?.summary ?? "",
          ]
            .join(" ")
            .toLowerCase();
          if (search && !haystack.includes(search)) {
            return false;
          }
          if (agent && session.agent !== agent) {
            return false;
          }
          if (provider && session.provider !== provider) {
            return false;
          }
          if (status && (session.latestExecution?.status ?? "idle") !== status) {
            return false;
          }
          return true;
        });
      }

      function renderSessions() {
        if (!hasDashboardView) {
          return;
        }
        const filtered = getFilteredSessions();
        document.getElementById("sessions").innerHTML = renderTableMarkup(
          "sessions-table",
          ["Workspace", "State", "Session", "Agent", "Thread", "Action"],
          renderSessionTableRows(filtered),
        );
        const rows = document.querySelectorAll('[data-testid="sessions-table"] tbody tr');
        rows.forEach((row, index) => {
          if (filtered[index]?.sessionId === dashboardState.selectedSessionId) {
            row.classList.add("is-selected");
          }
        });
        document.querySelector('[data-testid="sessions-stat"] strong').textContent = String(filtered.length);
        const connectionStat = document.querySelector('[data-testid="connection-stat"] strong');
        const connectionStatLabel = document.querySelector('[data-testid="connection-stat"] span');
        const connection = dashboardState.connectionStatus;
        if (connectionStat) {
          connectionStat.textContent = connection
            ? connection.connected
              ? "Connected"
              : connection.state === "connecting"
                ? "Connecting"
                : connection.state === "listening"
                  ? "Listening"
                  : "Disconnected"
            : "Unknown";
        }
        if (connectionStatLabel) {
          connectionStatLabel.textContent = connection
            ? connection.provider + " " + connection.mode
            : "chat transport";
        }
        const connectionRoot = document.getElementById("connection-status");
        if (connectionRoot) {
          connectionRoot.innerHTML = renderConnectionStatus(connection);
        }
        persistFilters();
      }

      function openDetailDrawer() {
        const drawer = document.getElementById("detail-drawer");
        if (!drawer) {
          return;
        }
        drawer.classList.add("is-open");
      }

      function closeDetailDrawer() {
        const drawer = document.getElementById("detail-drawer");
        if (!drawer) {
          return;
        }
        drawer.classList.remove("is-open");
      }

      function filterDetailHistory() {
        const input = document.getElementById("detail-history-search");
        if (!(input instanceof HTMLInputElement)) {
          return;
        }
        const query = input.value.trim().toLowerCase();
        document.querySelectorAll(".history-item").forEach((item) => {
          if (!(item instanceof HTMLElement)) {
            return;
          }
          const visible = !query || item.textContent.toLowerCase().includes(query);
          item.style.display = visible ? "" : "none";
        });
      }

      function renderSessionDetail(payload) {
        if (!hasDashboardView) {
          return;
        }
        const session = payload?.session;
        const history = payload?.executionHistory ?? [];
        if (!session) {
          document.getElementById("detail").innerHTML =
            '<div class="detail-empty">Select a session from the sessions table to inspect details and available actions.</div>';
          closeDetailDrawer();
          return;
        }
        const approvalActions = session.pendingApproval
          ? renderButtonMarkup("Approve", session.sessionId, "approve") + renderButtonMarkup("Reject", session.sessionId, "reject")
          : "";
        const cancelAction = canCancelSession(session)
          ? renderButtonMarkup("Cancel session", session.sessionId, "cancel")
          : "";
        const sessionCommands = [
          '<div class="field"><label for="detail-agent">Agent</label><select id="detail-agent" data-session-id="' + escapeHtml(session.sessionId) + '"><option value="codex"' + (session.agent === "codex" ? " selected" : "") + '>codex</option><option value="claude-code"' + (session.agent === "claude-code" ? " selected" : "") + '>claude</option><option value="cursor"' + (session.agent === "cursor" ? " selected" : "") + '>cursor</option><option value="gemini"' + (session.agent === "gemini" ? " selected" : "") + '>gemini</option></select></div>',
          '<div class="field"><label for="detail-mode">Approval</label><select id="detail-mode" data-session-id="' + escapeHtml(session.sessionId) + '"><option value="manual"' + (session.approvalPolicyOverride === "manual" ? " selected" : "") + '>manual</option><option value="never"' + (session.approvalPolicyOverride === "never" ? " selected" : "") + '>allow</option></select></div>',
        ].join("");
        const repoButtons = Object.entries(dashboardState.repoAliases || {})
          .sort((left, right) => left[0].localeCompare(right[0]))
          .map(([alias]) =>
            '<button data-command="repo ' + escapeHtml(alias) + '" data-session-id="' + escapeHtml(session.sessionId) + '">repo ' + escapeHtml(alias) + '</button>'
          )
          .join("");
        const pendingMarkup = session.pendingApproval
          ? '<div class="detail-card"><strong>Pending approval</strong><span>' + escapeHtml(session.pendingApproval.requestedAt) + '</span><p>' + escapeHtml(session.pendingApproval.prompt) + "</p></div>"
          : "";
        const latestRetry = session.latestExecution?.prompt
          ? '<button data-command="retry-last" data-session-id="' + escapeHtml(session.sessionId) + '">Retry latest</button>'
          : "";
        const historyMarkup = history.length
          ? '<div class="field"><label for="detail-history-search">History search</label><input id="detail-history-search" data-testid="detail-history-search" type="search" placeholder="prompt, result, status" /></div><div class="history-list">' + history.map((entry) =>
              '<article class="history-item"><header><strong>' + escapeHtml(entry.executionId) + '</strong><span class="status-' + escapeHtml(entry.status) + '">' + escapeHtml(entry.status) + '</span></header><code>' +
              escapeHtml(entry.updatedAt) + '</code><p><strong>Prompt:</strong> ' + escapeHtml(entry.prompt || "") + '</p><p><strong>Result:</strong> ' + escapeHtml(entry.summary || entry.error || "") + '</p><p><strong>Category:</strong> ' + escapeHtml(entry.failureCategory || "n/a") + "</p></article>"
            ).join("") + "</div>"
          : '<div class="detail-empty">No execution history recorded for this session.</div>';
        document.getElementById("detail").innerHTML =
          '<div class="detail-actions">' +
            cancelAction +
            approvalActions +
            latestRetry +
            '<button data-command="new-thread" data-session-id="' + escapeHtml(session.sessionId) + '">New thread</button>' +
            '<button data-command="new allow" data-session-id="' + escapeHtml(session.sessionId) + '">New allow</button>' +
            '<button data-command="new manual" data-session-id="' + escapeHtml(session.sessionId) + '">New manual</button>' +
            '<button data-command="fork codex" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Codex</button>' +
            '<button data-command="fork claude" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Claude</button>' +
            '<button data-command="fork cursor" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Cursor</button>' +
            '<button data-command="fork gemini" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Gemini</button>' +
            '<button data-command="attach-last" data-session-id="' + escapeHtml(session.sessionId) + '">Attach latest</button>' +
            '<button data-command="detach" data-session-id="' + escapeHtml(session.sessionId) + '">Detach</button>' +
          "</div>" +
          '<div class="detail-actions">' +
            repoButtons +
          "</div>" +
          '<div class="field"><label for="detail-command">Custom command</label><div class="inline-form"><input id="detail-command" data-session-id="' + escapeHtml(session.sessionId) + '" type="text" placeholder="attach <id>, mode allow, repo <alias>, fork gemini" /><button id="detail-command-run" data-session-id="' + escapeHtml(session.sessionId) + '">Run</button></div></div>' +
          '<div class="filters">' + sessionCommands + "</div>" +
          '<div class="detail-grid">' +
            '<div class="detail-card"><strong>Session</strong><code>' + escapeHtml(session.sessionId) + "</code></div>" +
            '<div class="detail-card"><strong>Agent</strong><span>' + escapeHtml(session.agent) + "</span></div>" +
            '<div class="detail-card"><strong>Provider</strong><span>' + escapeHtml(session.provider) + "</span><code>" + escapeHtml(session.threadId) + "</code></div>" +
            '<div class="detail-card"><strong>Approval</strong><span>' + escapeHtml(session.approvalPolicyOverride) + "</span></div>" +
            '<div class="detail-card"><strong>Workspace</strong><code>' + escapeHtml(session.workingDirectory) + "</code></div>" +
            '<div class="detail-card"><strong>Connection</strong><span>' + escapeHtml(session.connectionMode) + "</span></div>" +
            '<div class="detail-card"><strong>Updated</strong><code>' + escapeHtml(session.updatedAt) + "</code></div>" +
            '<div class="detail-card"><strong>Latest status</strong><span class="status-' + escapeHtml(session.latestExecution?.status ?? "idle") + '">' + escapeHtml(session.latestExecution?.status ?? "idle") + "</span></div>" +
            pendingMarkup +
          "</div>" +
          '<div class="section-head"><div><h2>Execution History</h2><p>Most recent 20 executions for this session.</p></div></div>' +
          historyMarkup;
        const historySearch = document.getElementById("detail-history-search");
        if (historySearch) {
          historySearch.addEventListener("input", filterDetailHistory);
        }
        openDetailDrawer();
      }

      function renderSettings() {
        if (!hasSettingsView) {
          return;
        }
        const settings = dashboardState.settings;
        if (!settings) {
          document.getElementById("settings").innerHTML =
            '<div class="detail-empty">Loading runtime settings.</div>';
          return;
        }
        const agentCommands = settings.agentCommands || {
          codex: { command: "codex", executable: "codex", status: "not_found" },
          "claude-code": { command: "claude", executable: "claude", status: "not_found" },
          cursor: { command: settings.cursorCommand || "cursor-agent --trust", executable: "cursor-agent", status: "not_found" },
          gemini: { command: settings.geminiCommand || "gemini --approval-mode yolo", executable: "gemini", status: "not_found" },
        };
        document.getElementById("settings").innerHTML =
          '<div class="settings-stack">' +
          '<section class="settings-block">' +
          '<div class="section-head"><div><h2>Defaults</h2><p>These apply to new sessions unless a thread overrides them.</p></div></div>' +
          '<div class="settings-grid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-agent">Default agent</label><select id="settings-agent" data-testid="settings-agent"><option value="codex"' + (settings.agentKind === "codex" ? " selected" : "") + '>codex</option><option value="claude-code"' + (settings.agentKind === "claude-code" ? " selected" : "") + '>claude</option><option value="cursor"' + (settings.agentKind === "cursor" ? " selected" : "") + '>cursor</option><option value="gemini"' + (settings.agentKind === "gemini" ? " selected" : "") + '>gemini</option></select></div>',
              'Used when a new session starts and nothing in the thread overrides it.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-approval">Default approval</label><select id="settings-approval" data-testid="settings-approval"><option value="manual"' + (settings.defaultApprovalPolicy === "manual" ? " selected" : "") + '>manual</option><option value="on-failure"' + (settings.defaultApprovalPolicy === "on-failure" ? " selected" : "") + '>on-failure</option><option value="never"' + (settings.defaultApprovalPolicy === "never" ? " selected" : "") + '>allow</option></select></div>',
              '<code>manual</code> always asks before write actions. <code>on-failure</code> retries with approval only when needed. <code>allow</code> runs without approval.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-network">Network access</label><select id="settings-network" data-testid="settings-network"><option value="enabled"' + (settings.networkAccess === "enabled" ? " selected" : "") + '>enabled</option><option value="disabled"' + (settings.networkAccess === "disabled" ? " selected" : "") + '>disabled</option></select></div>',
              'Controls whether future agent runs may access external network resources.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-auto-split-count">Auto-split after user messages</label><input id="settings-auto-split-count" data-testid="settings-auto-split-count" type="number" min="1" value="' + escapeHtml(settings.autoSplitThreadUserMessageCount) + '" /></div>',
              'Starts a fresh agent session after this many user messages in one thread to avoid overly long context.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Codex</h2><p>Applied to future Codex runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-codex-model">Model</label><input id="settings-codex-model" data-testid="settings-codex-model" type="text" value="' + escapeHtml(settings.codexModel) + '" /></div>',
              'The model name passed to Codex for new runs.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-codex-turns">Max turns</label><input id="settings-codex-turns" data-testid="settings-codex-turns" type="number" min="1" value="' + escapeHtml(settings.codexMaxTurns) + '" /></div>',
              'Upper bound for tool or reasoning turns in one request.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-codex-timeout">Command timeout ms</label><input id="settings-codex-timeout" data-testid="settings-codex-timeout" type="number" min="1" value="' + escapeHtml(settings.codexCommandTimeoutMs) + '" /></div>',
              'Idle timeout. Long tasks may continue as long as output keeps flowing.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-codex-runtime">Max runtime ms</label><input id="settings-codex-runtime" data-testid="settings-codex-runtime" type="number" min="1" value="' + escapeHtml(settings.codexMaxRuntimeMs) + '" /></div>',
              'Hard stop for the total wall-clock runtime of one Codex request.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-codex-output">Max output length</label><input id="settings-codex-output" data-testid="settings-codex-output" type="number" min="1" value="' + escapeHtml(settings.codexMaxOutputLength) + '" /></div>',
              'Truncates very large CLI output before it is stored or posted back.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Cursor</h2><p>Applied to future Cursor runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-cursor-command">Command</label><input id="settings-cursor-command" data-testid="settings-cursor-command" type="text" value="' + escapeHtml(settings.cursorCommand) + '" /></div>',
              'A non-interactive Cursor CLI command such as <code>cursor-agent --trust</code>. The default includes <code>--trust</code> for workspace trust.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-cursor-model">Model</label><input id="settings-cursor-model" data-testid="settings-cursor-model" type="text" value="' + escapeHtml(settings.cursorModel) + '" /></div>',
              'Model passed as <code>--model</code>. Use <code>Auto</code> for plans that cannot use named models.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-cursor-timeout">Command timeout ms</label><input id="settings-cursor-timeout" data-testid="settings-cursor-timeout" type="number" min="1" value="' + escapeHtml(settings.cursorCommandTimeoutMs) + '" /></div>',
              'Idle timeout for Cursor runs.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-cursor-runtime">Max runtime ms</label><input id="settings-cursor-runtime" data-testid="settings-cursor-runtime" type="number" min="1" value="' + escapeHtml(settings.cursorMaxRuntimeMs) + '" /></div>',
              'Hard stop for total runtime, even if Cursor still emits output.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Gemini CLI</h2><p>Applied to future Gemini runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-gemini-command">Command</label><input id="settings-gemini-command" data-testid="settings-gemini-command" type="text" value="' + escapeHtml(settings.geminiCommand) + '" /></div>',
              'A non-interactive Gemini CLI command such as <code>gemini --approval-mode yolo</code>.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-gemini-model">Model</label><input id="settings-gemini-model" data-testid="settings-gemini-model" type="text" value="' + escapeHtml(settings.geminiModel || "") + '" /></div>',
              'Optional model passed as <code>--model</code>. Leave blank to use the CLI default.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-gemini-timeout">Command timeout ms</label><input id="settings-gemini-timeout" data-testid="settings-gemini-timeout" type="number" min="1" value="' + escapeHtml(settings.geminiCommandTimeoutMs) + '" /></div>',
              'Idle timeout for Gemini runs.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-gemini-runtime">Max runtime ms</label><input id="settings-gemini-runtime" data-testid="settings-gemini-runtime" type="number" min="1" value="' + escapeHtml(settings.geminiMaxRuntimeMs) + '" /></div>',
              'Hard stop for total runtime, even if Gemini still emits output.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Claude Code</h2><p>Applied to future Claude runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-claude-model">Model</label><input id="settings-claude-model" data-testid="settings-claude-model" type="text" value="' + escapeHtml(settings.claudeModel) + '" /></div>',
              'The model name passed to Claude Code for new runs.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-claude-turns">Max turns</label><input id="settings-claude-turns" data-testid="settings-claude-turns" type="number" min="0" value="' + escapeHtml(settings.claudeMaxTurns) + '" /></div>',
              'Upper bound for one Claude request (0 = unlimited).'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-claude-timeout">Command timeout ms</label><input id="settings-claude-timeout" data-testid="settings-claude-timeout" type="number" min="1" value="' + escapeHtml(settings.claudeCommandTimeoutMs) + '" /></div>',
              'Idle timeout for Claude runs.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-claude-runtime">Max runtime ms</label><input id="settings-claude-runtime" data-testid="settings-claude-runtime" type="number" min="1" value="' + escapeHtml(settings.claudeMaxRuntimeMs) + '" /></div>',
              'Hard stop for total runtime, even if the run still emits progress.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>CLI resolution</h2><p>Shows which executable each agent command resolves to on this host.</p></div></div>' +
          '<div class="card-list">' +
            renderCommandResolutionCard("Codex", agentCommands.codex) +
            renderCommandResolutionCard("Claude Code", agentCommands["claude-code"]) +
            renderCommandResolutionCard("Cursor", agentCommands.cursor) +
            renderCommandResolutionCard("Gemini CLI", agentCommands.gemini) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Bridge transport</h2><p>Saved immediately, applied after bridge restart.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-provider">Chat provider</label><select id="settings-provider" data-testid="settings-provider"><option value="slack"' + (settings.chatProvider === "slack" ? " selected" : "") + '>slack</option><option value="mattermost"' + (settings.chatProvider === "mattermost" ? " selected" : "") + '>mattermost</option></select></div>',
              'Chooses which chat adapter the bridge should listen to after restart.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-slack-mode">Slack mode</label><select id="settings-slack-mode" data-testid="settings-slack-mode"><option value="http"' + (settings.slackMode === "http" ? " selected" : "") + '>http</option><option value="socket"' + (settings.slackMode === "socket" ? " selected" : "") + '>socket</option></select></div>',
              '<code>socket</code> avoids public callback URLs. <code>http</code> expects Slack to call your bridge over HTTP.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mattermost-mode">Mattermost mode</label><select id="settings-mattermost-mode" data-testid="settings-mattermost-mode"><option value="http"' + (settings.mattermostMode === "http" ? " selected" : "") + '>http</option><option value="websocket"' + (settings.mattermostMode === "websocket" ? " selected" : "") + '>websocket</option></select></div>',
              '<code>websocket</code> keeps a live connection to Mattermost. <code>http</code> is for webhook-style entrypoints.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-workdir">Default workdir</label><input id="settings-workdir" data-testid="settings-workdir" type="text" value="' + escapeHtml(settings.defaultWorkingDirectory) + '" /></div>',
              'Base repository or folder used when a new thread does not choose another workspace.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-allowed-dirs">Allowed directory roots</label><textarea id="settings-allowed-dirs" data-testid="settings-allowed-dirs" rows="4" placeholder="C:/repo&#10;C:/work">' + escapeHtml(settings.allowedDirectories || "") + '</textarea></div>',
              'One absolute path per line. Changes apply immediately and repo aliases are recomputed. If the <code>CAB_ALLOWED_DIRS</code> environment variable is set, it takes precedence again at the next restart.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-host">Bridge host</label><input id="settings-host" data-testid="settings-host" type="text" value="' + escapeHtml(settings.host) + '" /></div>',
              'The network interface used by the main bridge HTTP server when HTTP mode is active.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-port">Bridge port</label><input id="settings-port" data-testid="settings-port" type="number" min="1" value="' + escapeHtml(settings.port) + '" /></div>',
              'Main bridge port used for provider callbacks or local HTTP access.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-admin-host">Admin host</label><input id="settings-admin-host" data-testid="settings-admin-host" type="text" value="' + escapeHtml(settings.adminHost) + '" /></div>',
              'The interface for the admin UI only. Keep this local unless you intentionally expose it.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-admin-port">Admin port</label><input id="settings-admin-port" data-testid="settings-admin-port" type="number" min="1" value="' + escapeHtml(settings.adminPort) + '" /></div>',
              'Port for the admin dashboard and runtime APIs.'
            ) +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Connections</h2><p>Secrets stay masked. Enter a new value only when rotating it.</p></div></div>' +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-server">Mattermost server URL</label><input id="settings-mm-server" data-testid="settings-mm-server" type="text" value="' + escapeHtml(settings.mattermostServerUrl || "") + '" /></div>',
              'Base URL of your Mattermost workspace, for example <code>https://chat.example.com</code>.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-action-base">Mattermost action base URL</label><input id="settings-mm-action-base" data-testid="settings-mm-action-base" type="text" value="' + escapeHtml(settings.mattermostActionBaseUrl || "") + '" /></div>',
              'Public bridge base URL for button clicks. The bridge appends <code>/mattermost/actions</code> automatically.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-actor-mode">Mattermost actor mode</label><select id="settings-mm-actor-mode" data-testid="settings-mm-actor-mode"><option value="bot"' + (settings.mattermostActorMode === "bot" ? " selected" : "") + '>bot</option><option value="self"' + (settings.mattermostActorMode === "self" ? " selected" : "") + '>self</option></select></div>',
              '<code>bot</code> is for bot or service-account tokens. <code>self</code> is for your own account token and only reacts in your self-DM.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-slack-bot-token">Slack bot token</label><input id="settings-slack-bot-token" data-testid="settings-slack-bot-token" type="password" placeholder="' + (settings.hasSlackBotToken ? "stored" : "not set") + '" /></div>',
              'Used to post replies and call Slack APIs. Usually starts with <code>xoxb-</code>.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-slack-app-token">Slack app token</label><input id="settings-slack-app-token" data-testid="settings-slack-app-token" type="password" placeholder="' + (settings.hasSlackAppToken ? "stored" : "not set") + '" /></div>',
              'Required for Slack Socket Mode. Usually starts with <code>xapp-</code>.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-slack-signing-secret">Slack signing secret</label><input id="settings-slack-signing-secret" data-testid="settings-slack-signing-secret" type="password" placeholder="' + (settings.hasSlackSigningSecret ? "stored" : "not set") + '" /></div>',
              'Used to verify inbound HTTP requests from Slack. Not needed for pure Socket Mode operation.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-bot-token">Mattermost bot token</label><input id="settings-mm-bot-token" data-testid="settings-mm-bot-token" type="password" placeholder="' + (settings.hasMattermostBotToken ? "stored" : "not set") + '" /></div>',
              'This field accepts the bearer token used for Mattermost API access. In <code>self</code> mode this is where your PAT goes.'
            ) +
          "</div>" +
          '<div class="settings-subgrid">' +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-webhook-token">Mattermost webhook token</label><input id="settings-mm-webhook-token" data-testid="settings-mm-webhook-token" type="password" placeholder="' + (settings.hasMattermostWebhookToken ? "stored" : "not set") + '" /></div>',
              'Only needed for HTTP webhook entrypoints. Websocket mode usually does not use this token.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-slack-allowed-user-ids">Slack allowed user IDs</label><textarea id="settings-slack-allowed-user-ids" data-testid="settings-slack-allowed-user-ids" rows="4" placeholder="U01234567&#10;U07654321">' + escapeHtml(settings.slackAllowedUserIds || "") + '</textarea></div>',
              'Optional. When set, only these Slack user IDs can trigger the bridge. One ID per line, or separate with commas / semicolons.'
            ) +
            renderFieldWithHint(
              '<div class="field"><label for="settings-mm-allowed-user-ids">Mattermost allowed user IDs</label><textarea id="settings-mm-allowed-user-ids" data-testid="settings-mm-allowed-user-ids" rows="4" placeholder="user-id-1&#10;user-id-2">' + escapeHtml(settings.mattermostAllowedUserIds || "") + '</textarea></div>',
              'Optional. Applied only in Mattermost bot mode. One user ID per line, or separate with commas / semicolons.'
            ) +
          "</div>" +
          '<div class="section-head"><div><h2>Secret cleanup</h2><p>These do not clear immediately. They are applied only when you save settings.</p></div></div>' +
          '<div class="secret-reset-list">' +
            renderSecretResetItem("clear-slack-bot-token", "Slack bot token", "Remove the stored bot token on the next save.") +
            renderSecretResetItem("clear-slack-app-token", "Slack app token", "Remove the stored app token on the next save.") +
            renderSecretResetItem("clear-slack-signing-secret", "Slack signing secret", "Remove the stored signing secret on the next save.") +
            renderSecretResetItem("clear-mm-bot-token", "Mattermost bearer token", "Remove the stored Mattermost bearer token on the next save.") +
            renderSecretResetItem("clear-mm-webhook-token", "Mattermost webhook token", "Remove the stored webhook token on the next save.") +
          "</div>" +
          "</section>" +
          '<section class="settings-block"><div class="section-head"><div><h2>Current summary</h2><p>Read-only values from the currently loaded runtime settings.</p></div></div>' +
          '<div class="detail-grid">' +
            '<div class="detail-card"><strong>Chat provider</strong><span>' + escapeHtml(settings.chatProvider) + "</span></div>" +
            '<div class="detail-card"><strong>Slack mode</strong><span>' + escapeHtml(settings.slackMode) + "</span></div>" +
            '<div class="detail-card"><strong>Mattermost mode</strong><span>' + escapeHtml(settings.mattermostMode) + "</span></div>" +
            '<div class="detail-card"><strong>Mattermost actor mode</strong><span>' + escapeHtml(settings.mattermostActorMode) + "</span></div>" +
            '<div class="detail-card"><strong>Default workdir</strong><code>' + escapeHtml(settings.defaultWorkingDirectory) + "</code></div>" +
            '<div class="detail-card"><strong>Settings file</strong><code>' + escapeHtml(settings.settingsFile) + "</code></div>" +
            '<div class="detail-card"><strong>Last saved</strong><span>' + escapeHtml(settings.settingsSavedAt || "not saved yet") + "</span></div>" +
          "</div>" +
          '<div class="detail-actions"><button id="save-settings" data-testid="save-settings">Save runtime settings</button><button id="restart-bridge" data-testid="restart-bridge">Restart bridge</button></div>' +
          "</section>" +
          "</div>";
        syncSecretToggleStates();
        applyLanguage(localStorage.getItem(LANGUAGE_STORAGE_KEY) || "en");
      }

      async function loadSessionDetail(sessionId) {
        if (!hasDashboardView) {
          return;
        }
        dashboardState.selectedSessionId = sessionId;
        renderSessions();
        try {
          const payload = await fetchJson("/admin/api/sessions/" + encodeURIComponent(sessionId));
          renderSessionDetail(payload);
          setFlash("", "info");
          persistFilters();
        } catch (error) {
          document.getElementById("detail").innerHTML =
            '<div class="detail-empty">Failed to load session detail.</div>';
          openDetailDrawer();
          setFlash("Failed to load session detail.", "error");
          throw error;
        }
      }

      async function load(allowDetailReload = true) {
        if (!hasDashboardView) {
          return;
        }
        const [sessionsPayload, approvalsPayload, failuresPayload] = await Promise.all([
          fetchJson("/admin/api/sessions"),
          fetchJson("/admin/api/approvals"),
          fetchJson("/admin/api/failures?limit=20"),
        ]);
        dashboardState.sessions = sessionsPayload.sessions;
        dashboardState.connectionStatus = sessionsPayload.connectionStatus ?? null;
        dashboardState.repoAliases = sessionsPayload.repoAliases ?? {};
        dashboardState.approvals = approvalsPayload.approvals;
        dashboardState.failures = failuresPayload.failures;
        syncFilterOptions();
        if (!dashboardState.filtersRestored) {
          restoreFilters();
          syncFilterOptions();
          dashboardState.filtersRestored = true;
        }
        renderSessions();

        document.getElementById("approvals").innerHTML = renderApprovalCards(approvalsPayload.approvals);

        document.getElementById("failures").innerHTML = renderFailureCards(failuresPayload.failures);
        document.querySelector('[data-testid="approvals-stat"] strong').textContent = String(approvalsPayload.approvals.length);
        document.querySelector('[data-testid="failures-stat"] strong').textContent = String(failuresPayload.failures.length);
        const selectedStillExists = dashboardState.sessions.some((session) => session.sessionId === dashboardState.selectedSessionId);
        if (allowDetailReload && dashboardState.selectedSessionId && selectedStillExists) {
          await loadSessionDetail(dashboardState.selectedSessionId);
        } else if (allowDetailReload && !dashboardState.selectedSessionId && dashboardState.sessions[0]) {
          await loadSessionDetail(dashboardState.sessions[0].sessionId);
        } else if (dashboardState.selectedSessionId && !selectedStillExists) {
          dashboardState.selectedSessionId = null;
          renderSessionDetail(null);
          renderSessions();
        }
      }

      async function loadSettings() {
        if (!hasSettingsView) {
          return;
        }
        try {
          const payload = await fetchJson("/admin/api/settings");
          dashboardState.settings = payload.settings;
          renderSettings();
        } catch (error) {
          document.getElementById("settings").innerHTML =
            '<div class="detail-empty">Failed to load runtime settings.</div>';
          setFlash(error instanceof Error ? error.message : String(error), "error");
        }
      }

      async function saveSettings() {
        const payload = {
          agentKind: document.getElementById("settings-agent").value,
          defaultApprovalPolicy: document.getElementById("settings-approval").value,
          networkAccess: document.getElementById("settings-network").value,
          autoSplitThreadUserMessageCount: Number.parseInt(document.getElementById("settings-auto-split-count").value, 10),
          codexModel: document.getElementById("settings-codex-model").value.trim(),
          codexMaxTurns: Number.parseInt(document.getElementById("settings-codex-turns").value, 10),
          codexCommandTimeoutMs: Number.parseInt(document.getElementById("settings-codex-timeout").value, 10),
          codexMaxRuntimeMs: Number.parseInt(document.getElementById("settings-codex-runtime").value, 10),
          codexMaxOutputLength: Number.parseInt(document.getElementById("settings-codex-output").value, 10),
          cursorCommand: document.getElementById("settings-cursor-command").value.trim(),
          cursorModel: document.getElementById("settings-cursor-model").value.trim(),
          cursorCommandTimeoutMs: Number.parseInt(document.getElementById("settings-cursor-timeout").value, 10),
          cursorMaxRuntimeMs: Number.parseInt(document.getElementById("settings-cursor-runtime").value, 10),
          geminiCommand: document.getElementById("settings-gemini-command").value.trim(),
          geminiModel: document.getElementById("settings-gemini-model").value.trim(),
          geminiCommandTimeoutMs: Number.parseInt(document.getElementById("settings-gemini-timeout").value, 10),
          geminiMaxRuntimeMs: Number.parseInt(document.getElementById("settings-gemini-runtime").value, 10),
          claudeModel: document.getElementById("settings-claude-model").value.trim(),
          claudeMaxTurns: Number.parseInt(document.getElementById("settings-claude-turns").value, 10),
          claudeCommandTimeoutMs: Number.parseInt(document.getElementById("settings-claude-timeout").value, 10),
          claudeMaxRuntimeMs: Number.parseInt(document.getElementById("settings-claude-runtime").value, 10),
          chatProvider: document.getElementById("settings-provider").value,
          slackMode: document.getElementById("settings-slack-mode").value,
          mattermostMode: document.getElementById("settings-mattermost-mode").value,
          defaultWorkingDirectory: document.getElementById("settings-workdir").value.trim(),
          allowedDirectories: document.getElementById("settings-allowed-dirs").value.trim(),
          host: document.getElementById("settings-host").value.trim(),
          port: Number.parseInt(document.getElementById("settings-port").value, 10),
          adminHost: document.getElementById("settings-admin-host").value.trim(),
          adminPort: Number.parseInt(document.getElementById("settings-admin-port").value, 10),
          mattermostServerUrl: document.getElementById("settings-mm-server").value.trim(),
          mattermostActionBaseUrl: document.getElementById("settings-mm-action-base").value.trim(),
          mattermostActorMode: document.getElementById("settings-mm-actor-mode").value,
          slackAllowedUserIds: document.getElementById("settings-slack-allowed-user-ids").value.trim(),
          mattermostAllowedUserIds: document.getElementById("settings-mm-allowed-user-ids").value.trim(),
          slackBotToken: document.getElementById("settings-slack-bot-token").value.trim() || undefined,
          slackAppToken: document.getElementById("settings-slack-app-token").value.trim() || undefined,
          slackSigningSecret: document.getElementById("settings-slack-signing-secret").value.trim() || undefined,
          mattermostBotToken: document.getElementById("settings-mm-bot-token").value.trim() || undefined,
          mattermostWebhookToken: document.getElementById("settings-mm-webhook-token").value.trim() || undefined,
          clearSlackBotToken: document.getElementById("clear-slack-bot-token").checked,
          clearSlackAppToken: document.getElementById("clear-slack-app-token").checked,
          clearSlackSigningSecret: document.getElementById("clear-slack-signing-secret").checked,
          clearMattermostBotToken: document.getElementById("clear-mm-bot-token").checked,
          clearMattermostWebhookToken: document.getElementById("clear-mm-webhook-token").checked,
        };
        const response = await fetch("/admin/api/settings", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(payload),
        });
        if (!response.ok) {
          throw new Error(await response.text());
        }
        dashboardState.settings = (await response.json()).settings;
        renderSettings();
        setFlash("Runtime settings updated.", "info");
      }

      async function restartBridge() {
        const confirmed = window.confirm("Restart the bridge process now?");
        if (!confirmed) {
          return;
        }
        const response = await fetch("/admin/api/bridge/restart", {
          method: "POST",
        });
        if (!response.ok) {
          throw new Error(await response.text());
        }
        setFlash("Bridge restart requested. The admin page may disconnect for a few seconds.", "info");
      }

      async function runAction(sessionId, action) {
        try {
          const confirmed =
            !["cancel", "reject"].includes(action) ||
            window.confirm('Run "' + action + '" for session ' + sessionId + "?");
          if (!confirmed) {
            return;
          }
          await postAction("/admin/api/sessions/" + encodeURIComponent(sessionId) + "/" + action);
          setFlash(action + " queued for " + sessionId, "info");
          await load();
        } catch (error) {
          setFlash(error instanceof Error ? error.message : "Action failed.", "error");
        }
      }

      async function runCommand(sessionId, command, label) {
        try {
          const destructive = ["detach", "new-thread"].includes(command) || command.startsWith("fork ");
          if (destructive && !window.confirm('Run "' + command + '" for session ' + sessionId + "?")) {
            return;
          }
          await fetch("/admin/api/sessions/" + encodeURIComponent(sessionId) + "/command", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ command }),
          }).then(async (response) => {
            if (!response.ok) {
              throw new Error(await response.text());
            }
            return response.json();
          });
          setFlash((label || command) + " queued for " + sessionId, "info");
          await load();
        } catch (error) {
          setFlash(error instanceof Error ? error.message : "Command failed.", "error");
        }
      }

      applyAutoRefreshSetting();
      const refreshButton = document.getElementById("refresh");
      if (refreshButton) {
        refreshButton.addEventListener("click", () => {
          void Promise.all([load(), loadSettings()]);
        });
      }
      const detailCloseButton = document.getElementById("detail-close");
      detailCloseButton?.addEventListener("click", () => {
        closeDetailDrawer();
      });
      const detailDrawer = document.getElementById("detail-drawer");
      detailDrawer?.addEventListener("click", (event) => {
        if (event.target === detailDrawer) {
          closeDetailDrawer();
        }
      });
      const autoRefreshInput = document.getElementById("auto-refresh");
      if (autoRefreshInput) {
        autoRefreshInput.addEventListener("change", (event) => {
          const enabled = Boolean(event.target.checked);
          localStorage.setItem(AUTO_REFRESH_STORAGE_KEY, enabled ? "1" : "0");
          applyAutoRefreshSetting();
        });
      }
      const searchInput = document.getElementById("session-search");
      if (searchInput) {
        searchInput.addEventListener("input", () => {
          renderSessions();
        });
      }
      const agentFilter = document.getElementById("agent-filter");
      if (agentFilter) {
        agentFilter.addEventListener("change", () => {
          renderSessions();
        });
      }
      const providerFilter = document.getElementById("provider-filter");
      if (providerFilter) {
        providerFilter.addEventListener("change", () => {
          renderSessions();
        });
      }
      const statusFilter = document.getElementById("status-filter");
      if (statusFilter) {
        statusFilter.addEventListener("change", () => {
          renderSessions();
        });
      }

      document.addEventListener("click", (event) => {
        const target = event.target;
        if (!(target instanceof HTMLElement)) {
          return;
        }
        const detailButton = target.closest("button[data-session-detail]");
        if (detailButton instanceof HTMLElement) {
          const sessionId = detailButton.dataset.sessionDetail;
          if (sessionId) {
            void loadSessionDetail(sessionId);
          }
          return;
        }
        const commandButton = target.closest("button[data-command][data-session-id]");
        if (commandButton instanceof HTMLElement) {
          const sessionId = commandButton.dataset.sessionId;
          const command = commandButton.dataset.command;
          if (sessionId && command) {
            void runCommand(sessionId, command, commandButton.textContent || command);
          }
          return;
        }
        if (target.id === "detail-command-run" && target.dataset.sessionId) {
          const commandInput = document.getElementById("detail-command");
          const command = commandInput instanceof HTMLInputElement ? commandInput.value.trim() : "";
          if (command) {
            void runCommand(target.dataset.sessionId, command, "custom command");
          }
          return;
        }
        const button = target.closest("button[data-action][data-session-id]");
        if (!(button instanceof HTMLElement)) {
          if (target.id === "save-settings") {
            void saveSettings().catch((error) => {
              setFlash(error instanceof Error ? error.message : "Settings update failed.", "error");
            });
            return;
          }
          if (target.id === "restart-bridge") {
            void restartBridge().catch((error) => {
              setFlash(error instanceof Error ? error.message : "Bridge restart failed.", "error");
            });
          }
          return;
        }
        const sessionId = button.dataset.sessionId;
        const action = button.dataset.action;
        if (!sessionId || !action) {
          return;
        }
        void runAction(sessionId, action);
      });
      document.addEventListener("change", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement && target.dataset.secretToggle === "1") {
          syncSecretToggleStates();
          return;
        }
        if (!(target instanceof HTMLSelectElement)) {
          return;
        }
        if (target.id === "detail-agent" && target.dataset.sessionId) {
          void runCommand(target.dataset.sessionId, "agent " + target.value, "agent change");
          return;
        }
        if (target.id === "detail-mode" && target.dataset.sessionId) {
          void runCommand(target.dataset.sessionId, "mode " + target.value, "mode change");
        }
      });

      void Promise.all([load(), loadSettings()]);
    </script>
  </body>
</html>`;
}

function renderOnboardingPage(settings: RuntimeSettingsPayload): string {
  const provider = settings.chatProvider;
  const mattermostActorMode = settings.mattermostActorMode ?? "bot";
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Chat Agent Bridge Onboarding</title>
    <style>
      :root {
        color-scheme: dark;
        font-family: "Segoe UI", sans-serif;
        --bg: #0f172a;
        --panel: rgba(15, 23, 42, 0.9);
        --panel-alt: rgba(30, 41, 59, 0.7);
        --ink: #e2e8f0;
        --muted: #94a3b8;
        --line: #334155;
        --accent: #38bdf8;
      }
      :root[data-theme="light"] {
        color-scheme: light;
        --bg: #f4f7fb;
        --panel: rgba(255, 255, 255, 0.96);
        --panel-alt: rgba(241, 245, 249, 0.92);
        --ink: #0f172a;
        --muted: #475569;
        --line: #cbd5e1;
        --accent: #0ea5e9;
      }
      body { margin: 0; background: var(--bg); color: var(--ink); }
      main { max-width: 960px; margin: 0 auto; padding: 32px 20px 48px; }
      .hero { display: flex; justify-content: space-between; gap: 20px; align-items: start; margin-bottom: 24px; }
      .hero-copy { max-width: 640px; }
      .hero h1 { margin: 0 0 8px; font-size: 32px; }
      .hero p { margin: 0; color: var(--muted); line-height: 1.6; }
      .hero-side { display: grid; gap: 12px; justify-items: end; }
      .hero-meta { color: var(--muted); font-size: 12px; }
      .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 20px; margin-top: 20px; }
      .steps { display: grid; gap: 16px; }
      .step h2 { margin: 0 0 6px; font-size: 18px; }
      .step p { margin: 0 0 12px; color: var(--muted); }
      .grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); }
      label { display: grid; gap: 6px; font-size: 14px; color: var(--muted); }
      input, select { width: 100%; box-sizing: border-box; border-radius: 10px; border: 1px solid var(--line); background: var(--panel-alt); color: var(--ink); padding: 10px 12px; }
      .hint { font-size: 13px; color: var(--muted); margin-top: 6px; line-height: 1.5; }
      .card { border: 1px solid var(--line); border-radius: 14px; padding: 16px; background: var(--panel-alt); }
      .actions { display: flex; gap: 12px; flex-wrap: wrap; margin-top: 20px; }
      button, .link-button { border: 0; border-radius: 999px; padding: 10px 16px; background: var(--accent); color: #082f49; font-weight: 700; cursor: pointer; text-decoration: none; display: inline-flex; align-items: center; }
      .secondary { background: transparent; border: 1px solid var(--line); color: var(--ink); }
      .flash { margin-top: 16px; min-height: 22px; color: var(--accent); }
      .hidden { display: none; }
      code { background: rgba(15, 23, 42, 0.9); padding: 2px 6px; border-radius: 6px; }
      .nav { display: inline-flex; gap: 8px; padding: 4px; border: 1px solid var(--line); border-radius: 999px; background: var(--panel-alt); margin-bottom: 20px; }
      .nav a { color: var(--ink); text-decoration: none; padding: 8px 14px; border-radius: 999px; }
      .nav a.is-active { background: rgba(56, 189, 248, 0.16); border: 1px solid var(--line); }
      .site-footer { margin-top: 18px; display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; border-top: 1px solid var(--line); padding-top: 16px; color: var(--muted); }
      .footer-links, .footer-controls { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }
      .site-footer a { color: var(--accent); }
      .footer-controls label { display: inline-flex; gap: 8px; align-items: center; }
      .footer-controls select { border-radius: 999px; border: 1px solid var(--line); background: var(--panel-alt); color: var(--ink); padding: 6px 10px; }
      @media (max-width: 720px) {
        .hero {
          flex-direction: column;
        }
        .hero-side {
          justify-items: start;
        }
      }
    </style>
  </head>
  <body>
    <main>
      <section class="hero">
        <div class="hero-copy">
          <h1>Bridge Onboarding</h1>
          <p>Set the minimum runtime configuration without digging through every setting. Start here, save, then move to the full settings page only if you need deeper tuning.</p>
        </div>
        <div class="hero-side">
          <nav class="nav" aria-label="Admin navigation">
            <a href="/admin">Dashboard</a>
            <a href="/admin/settings">Settings</a>
            <a href="/admin/onboarding" class="is-active">Onboarding</a>
          </nav>
          <div class="hero-meta">Guided setup</div>
        </div>
      </section>
      <section class="panel steps">
        <div class="step">
          <h2>1. Choose how chat reaches the bridge</h2>
          <div class="grid">
            <label>
              Chat provider
              <select id="provider">
                <option value="slack"${provider === "slack" ? " selected" : ""}>Slack</option>
                <option value="mattermost"${provider === "mattermost" ? " selected" : ""}>Mattermost</option>
              </select>
            </label>
            <label>
              Default agent
              <select id="agent">
                <option value="codex"${settings.agentKind === "codex" ? " selected" : ""}>Codex</option>
                <option value="claude-code"${settings.agentKind === "claude-code" ? " selected" : ""}>Claude Code</option>
                <option value="cursor"${settings.agentKind === "cursor" ? " selected" : ""}>Cursor</option>
                <option value="gemini"${settings.agentKind === "gemini" ? " selected" : ""}>Gemini CLI</option>
              </select>
            </label>
            <label>
              Default approval
              <select id="approval">
                <option value="manual"${settings.defaultApprovalPolicy === "manual" ? " selected" : ""}>manual</option>
                <option value="on-failure"${settings.defaultApprovalPolicy === "on-failure" ? " selected" : ""}>on-failure</option>
                <option value="never"${settings.defaultApprovalPolicy === "never" ? " selected" : ""}>allow</option>
              </select>
            </label>
            <label>
              Network access
              <select id="network">
                <option value="enabled"${settings.networkAccess === "enabled" ? " selected" : ""}>enabled</option>
                <option value="disabled"${settings.networkAccess === "disabled" ? " selected" : ""}>disabled</option>
              </select>
            </label>
          </div>
        </div>

        <div class="step">
          <h2>2. Choose transport mode</h2>
          <div id="slack-card" class="card">
            <div class="grid">
              <label>
                Slack mode
                <select id="slack-mode">
                  <option value="http"${settings.slackMode === "http" ? " selected" : ""}>http</option>
                  <option value="socket"${settings.slackMode === "socket" ? " selected" : ""}>socket</option>
                </select>
              </label>
            </div>
            <div class="hint">Use <code>socket</code> when you want to avoid tunnels and public callback URLs.</div>
          </div>

          <div id="mattermost-card" class="card">
            <div class="grid">
              <label>
                Mattermost mode
                <select id="mattermost-mode">
                  <option value="http"${settings.mattermostMode === "http" ? " selected" : ""}>http</option>
                  <option value="websocket"${settings.mattermostMode === "websocket" ? " selected" : ""}>websocket</option>
                </select>
              </label>
              <label>
                Mattermost actor mode
                <select id="mattermost-actor-mode">
                  <option value="bot"${mattermostActorMode === "bot" ? " selected" : ""}>bot</option>
                  <option value="self"${mattermostActorMode === "self" ? " selected" : ""}>self</option>
                </select>
              </label>
            </div>
            <div class="hint"><code>bot</code> is for bot or service-account tokens. <code>self</code> is for your own Mattermost account token when you cannot create a bot. In <code>self</code> mode the bridge only reacts in your self-DM, and you still need to mention yourself to trigger it.</div>
          </div>
        </div>

        <div class="step">
          <h2>3. Fill only the required connection values</h2>
          <div id="slack-secrets" class="card">
            <div class="grid">
              <label>
                Slack bot token
                <input id="slack-bot-token" type="password" placeholder="${settings.hasSlackBotToken ? "stored" : "xoxb-..."}" />
              </label>
              <label>
                Slack app token
                <input id="slack-app-token" type="password" placeholder="${settings.hasSlackAppToken ? "stored" : "xapp-..."}" />
              </label>
              <label>
                Slack signing secret
                <input id="slack-signing-secret" type="password" placeholder="${settings.hasSlackSigningSecret ? "stored" : "optional for socket mode"}" />
              </label>
            </div>
          </div>

          <div id="mattermost-secrets" class="card">
            <div class="grid">
              <label>
                Mattermost server URL
                <input id="mattermost-server-url" type="text" value="${escapeHtml(settings.mattermostServerUrl || "")}" placeholder="http://mattermost.example:8065" />
              </label>
              <label>
                Mattermost bearer token
                <input id="mattermost-bot-token" type="password" placeholder="${settings.hasMattermostBotToken ? "stored" : "token"}" />
              </label>
              <label>
                Mattermost webhook token
                <input id="mattermost-webhook-token" type="password" placeholder="${settings.hasMattermostWebhookToken ? "stored" : "required in http mode"}" />
              </label>
              <label>
                Mattermost action base URL
                <input id="mattermost-action-base-url" type="text" value="${escapeHtml(settings.mattermostActionBaseUrl || "")}" placeholder="http://bridge-host:8787" />
              </label>
            </div>
            <div class="hint"><strong>PAT goes into</strong> <code>Mattermost bearer token</code>. Use a bot token in <code>bot</code> mode, or your own Personal Access Token in <code>self</code> mode.</div>
            <div class="hint"><code>Mattermost webhook token</code> is only needed for HTTP slash/webhook entrypoints. In websocket mode you usually do not need it.</div>
            <div class="hint"><code>Mattermost action base URL</code> is the public base URL that Mattermost can call for button clicks. Enter only the base, for example <code>https://bridge.example.com</code>; the bridge appends <code>/mattermost/actions</code> automatically.</div>
          </div>
        </div>

        <div class="step">
          <h2>4. Confirm workspace defaults</h2>
          <div class="grid">
            <label>
              Default workdir
              <input id="default-workdir" type="text" value="${escapeHtml(settings.defaultWorkingDirectory)}" />
            </label>
            <label>
              Bridge host
              <input id="host" type="text" value="${escapeHtml(settings.host)}" />
            </label>
            <label>
              Bridge port
              <input id="port" type="number" min="1" value="${escapeHtml(settings.port)}" />
            </label>
            <label>
              Admin port
              <input id="admin-port" type="number" min="1" value="${escapeHtml(settings.adminPort)}" />
            </label>
          </div>
        </div>

        <div class="actions">
          <button id="save-onboarding">Apply onboarding settings</button>
          <a class="link-button secondary" href="/admin/settings">Open full settings</a>
          <a class="link-button secondary" href="/admin">Back to dashboard</a>
        </div>
        <div id="flash" class="flash"></div>
      </section>
      ${renderAdminFooter()}
    </main>
    <script>
${renderAdminFooterScript()}
      const provider = document.getElementById("provider");
      const slackCard = document.getElementById("slack-card");
      const slackSecrets = document.getElementById("slack-secrets");
      const mattermostCard = document.getElementById("mattermost-card");
      const mattermostSecrets = document.getElementById("mattermost-secrets");
      const flash = document.getElementById("flash");

      function renderProviderState() {
        const isSlack = provider.value === "slack";
        slackCard.classList.toggle("hidden", !isSlack);
        slackSecrets.classList.toggle("hidden", !isSlack);
        mattermostCard.classList.toggle("hidden", isSlack);
        mattermostSecrets.classList.toggle("hidden", isSlack);
      }

      provider.addEventListener("change", renderProviderState);
      renderProviderState();

      document.getElementById("save-onboarding").addEventListener("click", async () => {
        const payload = {
          chatProvider: provider.value,
          agentKind: document.getElementById("agent").value,
          defaultApprovalPolicy: document.getElementById("approval").value,
          networkAccess: document.getElementById("network").value,
          slackMode: document.getElementById("slack-mode").value,
          mattermostMode: document.getElementById("mattermost-mode").value,
          mattermostActorMode: document.getElementById("mattermost-actor-mode").value,
          defaultWorkingDirectory: document.getElementById("default-workdir").value.trim(),
          host: document.getElementById("host").value.trim(),
          port: Number.parseInt(document.getElementById("port").value, 10),
          adminHost: "${escapeHtml(settings.adminHost)}",
          adminPort: Number.parseInt(document.getElementById("admin-port").value, 10),
          slackBotToken: document.getElementById("slack-bot-token").value.trim() || undefined,
          slackAppToken: document.getElementById("slack-app-token").value.trim() || undefined,
          slackSigningSecret: document.getElementById("slack-signing-secret").value.trim() || undefined,
          mattermostServerUrl: document.getElementById("mattermost-server-url").value.trim() || undefined,
          mattermostBotToken: document.getElementById("mattermost-bot-token").value.trim() || undefined,
          mattermostWebhookToken: document.getElementById("mattermost-webhook-token").value.trim() || undefined,
          mattermostActionBaseUrl: document.getElementById("mattermost-action-base-url").value.trim() || undefined
        };

        const response = await fetch("/admin/api/settings", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(payload)
        });

        if (!response.ok) {
          flash.textContent = "Failed to save onboarding settings.";
          return;
        }

        flash.textContent = "Onboarding settings applied. Restart the bridge if you changed transport settings.";
      });
    </script>
  </body>
</html>`;
}

export class AdminServer {
  private readonly config: AdminServerDeps["config"];
  private readonly stateStore: StateStore;
  private readonly auditLogger: AuditLogger;
  private readonly bridge: BridgeService;
  private server: http.Server | undefined;
  private readonly sockets = new Set<{ destroy(): void; on(event: "close", listener: () => void): void }>();

  constructor(deps: AdminServerDeps) {
    this.config = deps.config;
    this.stateStore = deps.stateStore;
    this.auditLogger = deps.auditLogger;
    this.bridge = deps.bridge;
  }

  async start(): Promise<void> {
    this.server = http.createServer(async (request, response) => {
      try {
        await this.handleRequest(request, response);
      } catch (error) {
        await this.auditLogger.log({
          type: "admin.request.failed",
          method: request.method ?? "UNKNOWN",
          url: request.url ?? "/",
          error: error instanceof Error ? error.message : String(error),
        });

        if (!response.headersSent) {
          writeJson(response, 500, { error: "Internal server error" });
        } else {
          response.end();
        }
        }
      });
      this.server?.on("connection", (socket) => {
        this.sockets.add(socket);
        socket.on("close", () => {
          this.sockets.delete(socket);
        });
      });

      const host = this.config.adminHost ?? this.config.host ?? "0.0.0.0";
    const port = this.config.adminPort ?? this.config.port + 1;
    await new Promise<void>((resolve, reject) => {
      this.server?.once("error", reject);
      this.server?.listen(port, host, () => resolve());
    });

    console.log(`Bridge admin listening on http://${host}:${port}/admin`);
  }

  async stop(): Promise<void> {
    if (!this.server) {
      return;
    }

    const server = this.server;
    const closePromise = new Promise<void>((resolve, reject) => {
      server.close((error) => {
        if (error) {
          reject(error);
          return;
        }

        resolve();
      });
    });
    for (const socket of this.sockets) {
      socket.destroy();
    }
    this.sockets.clear();
    await closePromise;
    this.server = undefined;
  }

  getListeningPort(): number | undefined {
    const address = this.server?.address();
    if (!address || typeof address === "string") {
      return undefined;
    }

    return address.port;
  }

  private async handleRequest(request: IncomingMessage, response: ServerResponse): Promise<void> {
    const url = new URL(request.url ?? "/", "http://localhost");
    if (request.method === "GET" && url.pathname === "/admin/healthz") {
      writeJson(response, 200, { ok: true });
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/metrics") {
      await this.handleMetrics(response);
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin") {
      writeHtml(response, 200, renderDashboard(await this.readDashboardData(), "dashboard"));
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/settings") {
      writeHtml(response, 200, renderDashboard(await this.readDashboardData(), "settings"));
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/onboarding") {
      writeHtml(response, 200, renderOnboardingPage(this.readRuntimeSettings()));
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/terms") {
      writeHtml(
        response,
        200,
        renderAdminLegalPage({
          title: "Terms of Use",
          body: `
            <p>This admin surface is provided for operating a local bridge on infrastructure you control.</p>
            <ul>
              <li>Use it only with repositories and credentials you are authorized to operate.</li>
              <li>Keep bearer tokens, PATs, and signing secrets out of screenshots and shared recordings.</li>
              <li>Interactive actions can trigger local agent work, so keep access to this admin screen restricted.</li>
            </ul>`,
        }),
      );
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/privacy") {
      writeHtml(
        response,
        200,
        renderAdminLegalPage({
          title: "Privacy Policy",
          body: `
            <p>This bridge stores local runtime configuration, session state, and audit logs on the machine where it runs.</p>
            <ul>
              <li>Chat thread metadata, approval actions, and execution summaries may be written to local state and audit files.</li>
              <li>Secrets entered from the admin UI are persisted locally in the configured settings file.</li>
              <li>No additional cloud service is introduced by this admin page beyond the chat providers and agent tools you already configure.</li>
            </ul>`,
        }),
      );
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/sessions") {
      await this.handleListSessions(response);
      return;
    }

    if (request.method === "GET" && url.pathname.startsWith("/admin/api/sessions/")) {
      const sessionId = decodeURIComponent(url.pathname.slice("/admin/api/sessions/".length));
      await this.handleGetSession(response, sessionId);
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/approvals") {
      await this.handleListApprovals(response);
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/failures") {
      const rawLimit = url.searchParams.get("limit");
      const limit = rawLimit ? Number.parseInt(rawLimit, 10) : 20;
      await this.handleListFailures(response, Number.isFinite(limit) ? limit : 20);
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/settings") {
      this.handleGetSettings(response);
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/api/environment") {
      await this.handleGetEnvironment(response);
      return;
    }

    if (request.method === "POST" && url.pathname.startsWith("/admin/api/sessions/")) {
      const suffix = url.pathname.slice("/admin/api/sessions/".length);
      const [encodedSessionId, action] = suffix.split("/", 2);
      if (!encodedSessionId || !action) {
        writeJson(response, 404, { error: "Not found" });
        return;
      }

      const sessionId = decodeURIComponent(encodedSessionId);
      if (action === "command") {
        await this.handleSessionCommand(request, response, sessionId);
        return;
      }
      if (action === "cancel" || action === "approve" || action === "reject") {
        await this.handleSessionAction(response, sessionId, action);
        return;
      }
    }

    if (request.method === "POST" && url.pathname === "/admin/api/settings") {
      await this.handleUpdateSettings(request, response);
      return;
    }

    if (request.method === "POST" && url.pathname === "/admin/api/bridge/restart") {
      await this.handleBridgeRestart(response);
      return;
    }

    writeJson(response, 404, { error: "Not found" });
  }

  private async readDashboardData(limit = 20): Promise<DashboardData> {
    const warnings: string[] = [];
    let sessions: SessionBinding[] = [];
    let latestRecords: ExecutionRecord[] = [];

    try {
      sessions = await this.stateStore.listSessions();
    } catch (error) {
      warnings.push("Session state could not be fully loaded. Showing a degraded dashboard.");
      await this.auditLogger.log({
        type: "admin.dashboard.degraded",
        area: "sessions",
        error: error instanceof Error ? error.message : String(error),
      });
    }

    try {
      latestRecords = await this.stateStore.listExecutionRecords({
        limit: Math.max(sessions.length * 5, 50),
      });
    } catch (error) {
      warnings.push(
        "Execution history could not be fully loaded. Recent statuses may be incomplete.",
      );
      await this.auditLogger.log({
        type: "admin.dashboard.degraded",
        area: "executions",
        error: error instanceof Error ? error.message : String(error),
      });
    }
    const latestBySession = new Map<string, ExecutionRecord>();
    for (const record of latestRecords) {
      if (!latestBySession.has(record.sessionId)) {
        latestBySession.set(record.sessionId, record);
      }
    }

    const summarizedSessions = sessions
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .map((session) =>
        summarizeSession(
          session,
          latestBySession.get(session.sessionId),
          this.bridge.describeSessionEnvironment(session),
        ),
      );
    const approvals = sessions
      .map((session) => {
        const pending = readPendingApproval(session.bridgeState);
        if (!pending) {
          return undefined;
        }

        return {
          sessionId: session.sessionId,
          agent: session.agent,
          provider: session.thread.provider,
          channelId: session.thread.channelId,
          threadId: session.thread.threadId,
          prompt: pending.prompt,
          requestedAt: pending.requestedAt,
          reason: pending.reason,
          executionId: pending.executionId ?? null,
        };
      })
      .filter((entry): entry is NonNullable<typeof entry> => Boolean(entry))
      .sort((left, right) => right.requestedAt.localeCompare(left.requestedAt));
    const failures = latestRecords
      .filter((record) => {
        return (
          record.status === "failed" ||
          record.status === "cancelled" ||
          record.status === "rejected"
        );
      })
      .slice(0, Math.max(limit, 1))
      .map((record) => ({
        ...record,
        failureCategory: classifyExecutionIssue(record),
      }));

    const connectionStatus = this.bridge.getChatConnectionStatus();
    let environment: EnvironmentPayload | undefined;
    try {
      environment = await this.readEnvironmentData();
    } catch (error) {
      warnings.push("Environment details could not be loaded.");
      await this.auditLogger.log({
        type: "admin.dashboard.degraded",
        area: "environment",
        error: error instanceof Error ? error.message : String(error),
      });
    }
    return {
      sessions: summarizedSessions,
      approvals,
      failures,
      repoAliases: this.config.repoAliases,
      ...(environment ? { environment } : {}),
      ...(warnings.length > 0 ? { warnings } : {}),
      ...(connectionStatus ? { connectionStatus } : {}),
    };
  }

  private async handleListSessions(response: ServerResponse): Promise<void> {
    const payload = (await this.readDashboardData()).sessions;
    await this.auditLogger.log({
      type: "admin.api.sessions",
      count: payload.length,
    });
    const connectionStatus = this.bridge.getChatConnectionStatus();
    writeJson(response, 200, {
      sessions: payload,
      repoAliases: this.config.repoAliases,
      ...(connectionStatus ? { connectionStatus } : {}),
    });
  }

  private async handleGetSession(response: ServerResponse, sessionId: string): Promise<void> {
    let sessions: SessionBinding[];
    try {
      sessions = await this.stateStore.listSessions();
    } catch (error) {
      await this.auditLogger.log({
        type: "admin.api.session.degraded",
        sessionId,
        area: "sessions",
        error: error instanceof Error ? error.message : String(error),
      });
      writeJson(response, 503, { error: "Session state is temporarily unavailable" });
      return;
    }
    const session = sessions.find((entry) => entry.sessionId === sessionId);
    if (!session) {
      writeJson(response, 404, { error: "Session not found" });
      return;
    }

    let records: ExecutionRecord[] = [];
    try {
      records = await this.stateStore.listExecutionRecords({
        sessionId,
        limit: 20,
      });
    } catch (error) {
      await this.auditLogger.log({
        type: "admin.api.session.degraded",
        sessionId,
        area: "executions",
        error: error instanceof Error ? error.message : String(error),
      });
    }
    writeJson(response, 200, {
      session: summarizeSession(session, records[0], this.bridge.describeSessionEnvironment(session)),
      executionHistory: records.map((record) => ({
        ...record,
        failureCategory: classifyExecutionIssue(record),
      })),
    });
  }

  private async handleListApprovals(response: ServerResponse): Promise<void> {
    writeJson(response, 200, {
      approvals: (await this.readDashboardData()).approvals,
    });
  }

  private async handleListFailures(response: ServerResponse, limit: number): Promise<void> {
    writeJson(response, 200, {
      failures: (await this.readDashboardData(limit)).failures,
    });
  }

  private async readEnvironmentData(): Promise<EnvironmentPayload> {
    let channelDefaultWorkingDirectories: Record<string, string> = {};
    try {
      channelDefaultWorkingDirectories =
        await this.stateStore.listChannelDefaultWorkingDirectories();
    } catch (error) {
      await this.auditLogger.log({
        type: "admin.api.environment.degraded",
        area: "channel-defaults",
        error: error instanceof Error ? error.message : String(error),
      });
    }

    return {
      defaultWorkingDirectory: this.config.defaultWorkingDirectory,
      allowedDirectories: [...this.config.allowedDirectories],
      repoAliases: { ...this.config.repoAliases },
      channelDefaultWorkingDirectories,
      claudeProjectsDir: this.config.claudeProjectsDir,
      agentKind: this.config.agentKind,
      chatProvider: this.config.chatProvider,
      agentEnvironmentVariables: this.bridge.getAgentEnvironmentVariableNames(),
    };
  }

  private async handleGetEnvironment(response: ServerResponse): Promise<void> {
    const environment = await this.readEnvironmentData();
    await this.auditLogger.log({
      type: "admin.api.environment",
    });
    writeJson(response, 200, { environment });
  }

  private readRuntimeSettings(): RuntimeSettingsPayload {
    let settingsSavedAt: string | undefined;
    try {
      settingsSavedAt = fs.existsSync(this.config.settingsFile)
        ? fs.statSync(this.config.settingsFile).mtime.toISOString()
        : undefined;
    } catch {
      settingsSavedAt = undefined;
    }
    return {
      agentKind: this.config.agentKind,
      defaultApprovalPolicy: this.config.defaultApprovalPolicy,
      networkAccess: this.config.networkAccess,
      chatProvider: this.config.chatProvider,
      slackAllowedUserIds: serializeUserIdList(this.config.slackAllowedUserIds),
      mattermostAllowedUserIds: serializeUserIdList(this.config.mattermostAllowedUserIds),
      slackMode: this.config.slackMode,
      mattermostMode: this.config.mattermostMode,
      mattermostActorMode: this.config.mattermostActorMode ?? "bot",
      defaultWorkingDirectory: this.config.defaultWorkingDirectory,
      allowedDirectories: this.config.allowedDirectories.join("\n"),
      host: this.config.host,
      port: this.config.port,
      adminHost: this.config.adminHost ?? this.config.host,
      adminPort: this.config.adminPort ?? this.config.port + 1,
      mattermostDmOnly: this.config.mattermostDmOnly ?? false,
      hasSlackBotToken: Boolean(this.config.slackBotToken),
      hasSlackAppToken: Boolean(this.config.slackAppToken),
      hasSlackSigningSecret: Boolean(this.config.slackSigningSecret),
      hasMattermostBotToken: Boolean(this.config.mattermostBotToken),
      hasMattermostWebhookToken: Boolean(this.config.mattermostWebhookToken),
      codexModel: this.config.codexModel,
      codexMaxTurns: this.config.codexMaxTurns,
      codexCommandTimeoutMs: this.config.codexCommandTimeoutMs,
      codexMaxRuntimeMs: this.config.codexMaxRuntimeMs,
      codexMaxOutputLength: this.config.codexMaxOutputLength,
      cursorCommand: this.config.cursorCommand ?? "cursor-agent --trust",
      cursorModel: this.config.cursorModel ?? "Auto",
      cursorCommandTimeoutMs: this.config.cursorCommandTimeoutMs ?? 600000,
      cursorMaxRuntimeMs: this.config.cursorMaxRuntimeMs ?? 3600000,
      geminiCommand: this.config.geminiCommand ?? "gemini --approval-mode yolo",
      geminiModel: this.config.geminiModel,
      geminiCommandTimeoutMs: this.config.geminiCommandTimeoutMs ?? 600000,
      geminiMaxRuntimeMs: this.config.geminiMaxRuntimeMs ?? 3600000,
      claudeModel: this.config.claudeModel,
      claudeMaxTurns: this.config.claudeMaxTurns,
      claudeCommandTimeoutMs: this.config.claudeCommandTimeoutMs,
      claudeMaxRuntimeMs: this.config.claudeMaxRuntimeMs,
      agentCommands: {
        codex: resolveCommand("codex", process.cwd()),
        "claude-code": resolveCommand("claude", process.cwd()),
        cursor: resolveCommand(this.config.cursorCommand ?? "cursor-agent --trust", process.cwd()),
        gemini: resolveCommand(this.config.geminiCommand ?? "gemini --approval-mode yolo", process.cwd()),
      },
      autoSplitThreadUserMessageCount: this.config.autoSplitThreadUserMessageCount,
      settingsFile: this.config.settingsFile,
      ...(this.config.mattermostServerUrl
        ? { mattermostServerUrl: this.config.mattermostServerUrl }
        : {}),
      ...(this.config.mattermostActionBaseUrl
        ? { mattermostActionBaseUrl: this.config.mattermostActionBaseUrl }
        : {}),
      ...(settingsSavedAt ? { settingsSavedAt } : {}),
    };
  }

  private persistSettingsFile(): void {
    const payload = {
      allowedDirs: this.config.allowedDirectories.join(";"),
      chatProvider: this.config.chatProvider,
      agentKind: this.config.agentKind,
      defaultApprovalPolicy: this.config.defaultApprovalPolicy,
      networkAccess: this.config.networkAccess,
      slackMode: this.config.slackMode,
      slackAllowedUserIds: serializeUserIdList(this.config.slackAllowedUserIds),
      mattermostAllowedUserIds: serializeUserIdList(this.config.mattermostAllowedUserIds),
      mattermostMode: this.config.mattermostMode,
      mattermostActorMode: this.config.mattermostActorMode ?? "bot",
      host: this.config.host,
      port: this.config.port,
      adminHost: this.config.adminHost ?? this.config.host,
      adminPort: this.config.adminPort ?? this.config.port + 1,
      defaultWorkingDirectory: this.config.defaultWorkingDirectory,
      mattermostServerUrl: this.config.mattermostServerUrl,
      mattermostActionBaseUrl: this.config.mattermostActionBaseUrl,
      mattermostDmOnly: this.config.mattermostDmOnly ?? false,
      slackBotToken: this.config.slackBotToken,
      slackAppToken: this.config.slackAppToken,
      slackSigningSecret: this.config.slackSigningSecret,
      mattermostBotToken: this.config.mattermostBotToken,
      mattermostWebhookToken: this.config.mattermostWebhookToken,
      codexModel: this.config.codexModel,
      codexMaxTurns: this.config.codexMaxTurns,
      codexCommandTimeoutMs: this.config.codexCommandTimeoutMs,
      codexMaxRuntimeMs: this.config.codexMaxRuntimeMs,
      codexMaxOutputLength: this.config.codexMaxOutputLength,
      cursorCommand: this.config.cursorCommand ?? "cursor-agent --trust",
      cursorModel: this.config.cursorModel ?? "Auto",
      cursorCommandTimeoutMs: this.config.cursorCommandTimeoutMs ?? 600000,
      cursorMaxRuntimeMs: this.config.cursorMaxRuntimeMs ?? 3600000,
      geminiCommand: this.config.geminiCommand ?? "gemini --approval-mode yolo",
      geminiModel: this.config.geminiModel,
      geminiCommandTimeoutMs: this.config.geminiCommandTimeoutMs ?? 600000,
      geminiMaxRuntimeMs: this.config.geminiMaxRuntimeMs ?? 3600000,
      claudeModel: this.config.claudeModel,
      claudeMaxTurns: this.config.claudeMaxTurns,
      claudeCommandTimeoutMs: this.config.claudeCommandTimeoutMs,
      claudeMaxRuntimeMs: this.config.claudeMaxRuntimeMs,
      autoSplitThreadUserMessageCount: this.config.autoSplitThreadUserMessageCount,
    };
    // Read-modify-write so keys owned by chat commands (or future writers) are
    // preserved. `undefined` values (unset optional secrets) are stripped.
    updatePersistedSettings(this.config.settingsFile, payload);
  }

  private handleGetSettings(response: ServerResponse): void {
    writeJson(response, 200, {
      settings: this.readRuntimeSettings(),
    });
  }

  private async handleSessionAction(
    response: ServerResponse,
    sessionId: string,
    action: "cancel" | "approve" | "reject",
  ): Promise<void> {
    const result = await this.bridge.dispatchAdminAction(sessionId, action);
    if (!result.accepted) {
      writeJson(response, 404, {
        error: result.reason ?? "Action rejected",
      });
      return;
    }

    writeJson(response, 202, {
      ok: true,
      sessionId,
      action,
      status: "queued",
    });
  }

  private async handleSessionCommand(
    request: IncomingMessage,
    response: ServerResponse,
    sessionId: string,
  ): Promise<void> {
    let command = "";
    try {
      const body = await readRequestBody(request);
      const payload = body ? (JSON.parse(body) as { command?: string }) : {};
      command = typeof payload.command === "string" ? payload.command.trim() : "";
    } catch {
      writeJson(response, 400, { error: "Invalid JSON body" });
      return;
    }

    if (!command) {
      writeJson(response, 400, { error: "Missing command" });
      return;
    }

    const result = await this.bridge.dispatchAdminCommand(sessionId, command);
    if (!result.accepted) {
      writeJson(response, 404, {
        error: result.reason ?? "Command rejected",
      });
      return;
    }

    writeJson(response, 202, {
      ok: true,
      sessionId,
      command,
      status: "queued",
    });
  }

  private async handleUpdateSettings(
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> {
    let payload: Partial<RuntimeSettingsPayload> = {};
    try {
      const body = await readRequestBody(request);
      payload = body ? (JSON.parse(body) as Partial<RuntimeSettingsPayload>) : {};
    } catch {
      writeJson(response, 400, { error: "Invalid JSON body" });
      return;
    }

    if (
      payload.agentKind &&
      payload.agentKind !== "codex" &&
      payload.agentKind !== "claude-code" &&
      payload.agentKind !== "cursor" &&
      payload.agentKind !== "gemini"
    ) {
      writeJson(response, 400, { error: "Invalid agentKind" });
      return;
    }
    if (
      payload.defaultApprovalPolicy &&
      payload.defaultApprovalPolicy !== "manual" &&
      payload.defaultApprovalPolicy !== "on-failure" &&
      payload.defaultApprovalPolicy !== "never"
    ) {
      writeJson(response, 400, { error: "Invalid defaultApprovalPolicy" });
      return;
    }
    if (
      payload.networkAccess &&
      payload.networkAccess !== "enabled" &&
      payload.networkAccess !== "disabled"
    ) {
      writeJson(response, 400, { error: "Invalid networkAccess" });
      return;
    }
    if (
      payload.chatProvider &&
      payload.chatProvider !== "slack" &&
      payload.chatProvider !== "mattermost"
    ) {
      writeJson(response, 400, { error: "Invalid chatProvider" });
      return;
    }
    if (
      payload.slackMode &&
      payload.slackMode !== "http" &&
      payload.slackMode !== "socket"
    ) {
      writeJson(response, 400, { error: "Invalid slackMode" });
      return;
    }
    if (
      payload.mattermostMode &&
      payload.mattermostMode !== "http" &&
      payload.mattermostMode !== "websocket"
    ) {
      writeJson(response, 400, { error: "Invalid mattermostMode" });
      return;
    }
    if (
      payload.mattermostActorMode &&
      payload.mattermostActorMode !== "bot" &&
      payload.mattermostActorMode !== "self"
    ) {
      writeJson(response, 400, { error: "Invalid mattermostActorMode" });
      return;
    }
    if (payload.host !== undefined && !payload.host.trim()) {
      writeJson(response, 400, { error: "Invalid host" });
      return;
    }
    if (payload.adminHost !== undefined && !payload.adminHost.trim()) {
      writeJson(response, 400, { error: "Invalid adminHost" });
      return;
    }
    const numericKeys = [
      "port",
      "adminPort",
      "codexMaxTurns",
      "codexCommandTimeoutMs",
      "codexMaxRuntimeMs",
      "codexMaxOutputLength",
      "cursorCommandTimeoutMs",
      "cursorMaxRuntimeMs",
      "geminiCommandTimeoutMs",
      "geminiMaxRuntimeMs",
      "claudeCommandTimeoutMs",
      "claudeMaxRuntimeMs",
      "autoSplitThreadUserMessageCount",
    ];
    for (const key of numericKeys) {
      const value = payload[key as keyof RuntimeSettingsPayload];
      if (value !== undefined && (!Number.isInteger(value) || Number(value) < 1)) {
        writeJson(response, 400, { error: `Invalid ${key}` });
        return;
      }
    }
    // Keys that accept 0 (e.g. 0 = unlimited)
    const nonNegativeKeys = ["claudeMaxTurns", "agentRetryCount"];
    for (const key of nonNegativeKeys) {
      const value = payload[key as keyof RuntimeSettingsPayload];
      if (value !== undefined && (!Number.isInteger(value) || Number(value) < 0)) {
        writeJson(response, 400, { error: `Invalid ${key}` });
        return;
      }
    }
    const urlKeys = ["mattermostServerUrl", "mattermostActionBaseUrl"];
    for (const key of urlKeys) {
      const value = payload[key as keyof RuntimeSettingsPayload];
      if (typeof value === "string" && value.length > 0) {
        try {
          new URL(value);
        } catch {
          writeJson(response, 400, { error: `Invalid ${key}` });
          return;
        }
      }
    }
    // Validate the allowed roots first so defaultWorkingDirectory can be checked
    // against the new roots below. Each root must be an absolute path to an
    // existing directory.
    let nextAllowedDirectories: string[] | undefined;
    if (payload.allowedDirectories !== undefined) {
      if (typeof payload.allowedDirectories !== "string") {
        writeJson(response, 400, { error: "Invalid allowedDirectories" });
        return;
      }
      const entries = payload.allowedDirectories
        .split(/[\r\n]+/)
        .map((entry) => entry.trim())
        .filter((entry) => entry.length > 0);
      if (entries.length === 0) {
        writeJson(response, 400, {
          error: "Allowed directory roots must include at least one path.",
        });
        return;
      }

      const resolved: string[] = [];
      for (const entry of entries) {
        if (!path.isAbsolute(entry)) {
          writeJson(response, 400, {
            error: `Allowed directory root is not an absolute path: ${entry}`,
          });
          return;
        }
        const resolvedEntry = path.resolve(entry);
        let stat: fs.Stats | undefined;
        try {
          stat = fs.statSync(resolvedEntry);
        } catch {
          stat = undefined;
        }
        if (!stat || !stat.isDirectory()) {
          writeJson(response, 400, {
            error: `Allowed directory root does not exist or is not a directory: ${entry}`,
          });
          return;
        }
        resolved.push(resolvedEntry);
      }
      nextAllowedDirectories = resolved;
    }

    // The default working directory must stay inside the (possibly new) roots.
    const effectiveAllowedDirectories =
      nextAllowedDirectories ?? this.config.allowedDirectories;
    if (payload.defaultWorkingDirectory !== undefined) {
      try {
        payload.defaultWorkingDirectory = assertPathAllowed(
          payload.defaultWorkingDirectory,
          effectiveAllowedDirectories,
        );
      } catch (error) {
        writeJson(response, 400, {
          error: error instanceof Error ? error.message : "Invalid defaultWorkingDirectory",
        });
        return;
      }
    } else if (nextAllowedDirectories) {
      try {
        assertPathAllowed(this.config.defaultWorkingDirectory, nextAllowedDirectories);
      } catch {
        writeJson(response, 400, {
          error: `Current default working directory \`${this.config.defaultWorkingDirectory}\` would be outside the new allowed roots. Update the default working directory as well.`,
        });
        return;
      }
    }
    if (payload.cursorCommand !== undefined && !payload.cursorCommand.trim()) {
      writeJson(response, 400, { error: "Invalid cursorCommand" });
      return;
    }
    if (payload.cursorModel !== undefined && !payload.cursorModel.trim()) {
      writeJson(response, 400, { error: "Invalid cursorModel" });
      return;
    }
    if (payload.geminiCommand !== undefined && !payload.geminiCommand.trim()) {
      writeJson(response, 400, { error: "Invalid geminiCommand" });
      return;
    }
    const clearSecretKeys = [
      "clearSlackBotToken",
      "clearSlackAppToken",
      "clearSlackSigningSecret",
      "clearMattermostBotToken",
      "clearMattermostWebhookToken",
    ];
    for (const key of clearSecretKeys) {
      const value = payload[key as keyof RuntimeSettingsPayload];
      if (value !== undefined && typeof value !== "boolean") {
        writeJson(response, 400, { error: `Invalid ${key}` });
        return;
      }
    }

    if (payload.agentKind) {
      this.config.agentKind = payload.agentKind;
    }
    if (payload.defaultApprovalPolicy) {
      this.config.defaultApprovalPolicy = payload.defaultApprovalPolicy;
    }
    if (payload.networkAccess) {
      this.config.networkAccess = payload.networkAccess;
    }
    if (payload.autoSplitThreadUserMessageCount) {
      this.config.autoSplitThreadUserMessageCount = payload.autoSplitThreadUserMessageCount;
    }
      if (payload.chatProvider) {
        this.config.chatProvider = payload.chatProvider;
      }
      if (payload.slackAllowedUserIds !== undefined) {
        this.config.slackAllowedUserIds = parseUserIdList(payload.slackAllowedUserIds);
      }
      if (payload.mattermostAllowedUserIds !== undefined) {
        this.config.mattermostAllowedUserIds = parseUserIdList(payload.mattermostAllowedUserIds);
      }
      if (payload.slackMode) {
        this.config.slackMode = payload.slackMode;
      }
    if (payload.mattermostMode) {
      this.config.mattermostMode = payload.mattermostMode;
    }
    if (payload.mattermostActorMode) {
      this.config.mattermostActorMode = payload.mattermostActorMode;
      this.config.mattermostDmOnly = payload.mattermostActorMode === "self";
    }
    if (payload.host) {
      this.config.host = payload.host;
    }
    if (payload.port) {
      this.config.port = payload.port;
    }
    if (payload.adminHost) {
      this.config.adminHost = payload.adminHost;
    }
    if (payload.adminPort) {
      this.config.adminPort = payload.adminPort;
    }
    if (payload.defaultWorkingDirectory) {
      this.config.defaultWorkingDirectory = payload.defaultWorkingDirectory;
    }
    let allowedDirsChange:
      | { before: string[]; after: string[] }
      | undefined;
    if (nextAllowedDirectories) {
      const before = [...this.config.allowedDirectories];
      // Mutate the shared array in place so path checks that captured a
      // reference to config.allowedDirectories see the update immediately.
      this.config.allowedDirectories.splice(
        0,
        this.config.allowedDirectories.length,
        ...nextAllowedDirectories,
      );
      this.config.repoAliases = computeRepoAliases(
        this.config.repoAliasesRaw,
        this.config.allowedDirectories,
        this.config.discoverRepos ?? false,
      );
      allowedDirsChange = { before, after: [...this.config.allowedDirectories] };
    }
    if (payload.mattermostServerUrl !== undefined) {
      if (payload.mattermostServerUrl) {
        this.config.mattermostServerUrl = payload.mattermostServerUrl;
      } else {
        delete this.config.mattermostServerUrl;
      }
    }
    if (payload.mattermostActionBaseUrl !== undefined) {
      if (payload.mattermostActionBaseUrl) {
        this.config.mattermostActionBaseUrl = payload.mattermostActionBaseUrl;
      } else {
        delete this.config.mattermostActionBaseUrl;
      }
    }
    if (payload.mattermostDmOnly !== undefined) {
      this.config.mattermostDmOnly = payload.mattermostDmOnly;
      this.config.mattermostActorMode = payload.mattermostDmOnly ? "self" : "bot";
    }
    if (payload.clearSlackBotToken) {
      delete this.config.slackBotToken;
    } else if (payload.slackBotToken) {
      this.config.slackBotToken = payload.slackBotToken;
    }
    if (payload.clearSlackAppToken) {
      delete this.config.slackAppToken;
    } else if (payload.slackAppToken) {
      this.config.slackAppToken = payload.slackAppToken;
    }
    if (payload.clearSlackSigningSecret) {
      delete this.config.slackSigningSecret;
    } else if (payload.slackSigningSecret) {
      this.config.slackSigningSecret = payload.slackSigningSecret;
    }
    if (payload.clearMattermostBotToken) {
      delete this.config.mattermostBotToken;
    } else if (payload.mattermostBotToken) {
      this.config.mattermostBotToken = payload.mattermostBotToken;
    }
    if (payload.clearMattermostWebhookToken) {
      delete this.config.mattermostWebhookToken;
    } else if (payload.mattermostWebhookToken) {
      this.config.mattermostWebhookToken = payload.mattermostWebhookToken;
    }
    if (payload.codexModel) {
      this.config.codexModel = payload.codexModel;
    }
    if (payload.codexMaxTurns) {
      this.config.codexMaxTurns = payload.codexMaxTurns;
    }
    if (payload.codexCommandTimeoutMs) {
      this.config.codexCommandTimeoutMs = payload.codexCommandTimeoutMs;
    }
    if (payload.codexMaxRuntimeMs) {
      this.config.codexMaxRuntimeMs = payload.codexMaxRuntimeMs;
    }
    if (payload.codexMaxOutputLength) {
      this.config.codexMaxOutputLength = payload.codexMaxOutputLength;
    }
    if (payload.cursorCommand) {
      this.config.cursorCommand = payload.cursorCommand;
    }
    if (payload.cursorModel) {
      this.config.cursorModel = payload.cursorModel;
    }
    if (payload.cursorCommandTimeoutMs) {
      this.config.cursorCommandTimeoutMs = payload.cursorCommandTimeoutMs;
    }
    if (payload.cursorMaxRuntimeMs) {
      this.config.cursorMaxRuntimeMs = payload.cursorMaxRuntimeMs;
    }
    if (payload.geminiCommand) {
      this.config.geminiCommand = payload.geminiCommand;
    }
    if (payload.geminiModel !== undefined) {
      if (payload.geminiModel) {
        this.config.geminiModel = payload.geminiModel;
      } else {
        delete this.config.geminiModel;
      }
    }
    if (payload.geminiCommandTimeoutMs) {
      this.config.geminiCommandTimeoutMs = payload.geminiCommandTimeoutMs;
    }
    if (payload.geminiMaxRuntimeMs) {
      this.config.geminiMaxRuntimeMs = payload.geminiMaxRuntimeMs;
    }
    if (payload.claudeModel) {
      this.config.claudeModel = payload.claudeModel;
    }
    if (payload.claudeMaxTurns != null) {
      this.config.claudeMaxTurns = payload.claudeMaxTurns;
    }
    if (payload.claudeCommandTimeoutMs) {
      this.config.claudeCommandTimeoutMs = payload.claudeCommandTimeoutMs;
    }
    if (payload.claudeMaxRuntimeMs) {
      this.config.claudeMaxRuntimeMs = payload.claudeMaxRuntimeMs;
    }

    if (allowedDirsChange) {
      await this.auditLogger.log({
        type: "admin.settings.allowed-dirs.updated",
        before: allowedDirsChange.before,
        after: allowedDirsChange.after,
      });
    }

    const settings = this.readRuntimeSettings();
    await this.auditLogger.log({
      type: "admin.settings.updated",
      settings: JSON.parse(JSON.stringify(settings)) as JsonValue,
    });
    this.persistSettingsFile();
    writeJson(response, 200, {
      ok: true,
      settings,
    });
  }

  private async handleBridgeRestart(response: ServerResponse): Promise<void> {
    await this.bridge.requestAdminRestart();
    writeJson(response, 202, {
      ok: true,
      status: "queued",
    });
  }

  private async handleMetrics(response: ServerResponse): Promise<void> {
    const sessions = await this.stateStore.listSessions();
    const records = await this.stateStore.listExecutionRecords({ limit: 200 });
    const connectionStatus = this.bridge.getChatConnectionStatus?.();

    const running = records.filter((r) => r.status === "running").length;
    const failed = records.filter((r) => r.status === "failed").length;
    const completed = records.filter((r) => r.status === "completed").length;
    const waitingApproval = records.filter((r) => r.status === "waiting_for_approval").length;

    writeJson(response, 200, {
      uptime: process.uptime(),
      sessions: {
        total: sessions.length,
      },
      executions: {
        running,
        completed,
        failed,
        waitingApproval,
        recentTotal: records.length,
      },
      connection: connectionStatus
        ? {
            state: connectionStatus.state,
            connected: connectionStatus.connected,
            reconnectCount: connectionStatus.reconnectCount ?? 0,
            disconnectCount: connectionStatus.disconnectCount ?? 0,
          }
        : null,
      memory: {
        rss: process.memoryUsage.rss(),
      },
    });
  }
}
