import http, { IncomingMessage, ServerResponse } from "node:http";
import fs from "node:fs";
import path from "node:path";
import { AuditLogger } from "../core/audit.js";
import { readPendingApproval } from "../core/approval.js";
import { BridgeService } from "../core/bridge-service.js";
import { assertPathAllowed } from "../core/paths.js";
import { AppConfig, ExecutionRecord, SessionBinding } from "../core/types.js";
import { StateStore } from "../state/state-store.js";

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
}

interface RuntimeSettingsPayload {
  agentKind: AppConfig["agentKind"];
  defaultApprovalPolicy: AppConfig["defaultApprovalPolicy"];
  networkAccess: AppConfig["networkAccess"];
  chatProvider: AppConfig["chatProvider"];
  slackMode: AppConfig["slackMode"];
  mattermostMode: AppConfig["mattermostMode"];
  host: AppConfig["host"];
  port: AppConfig["port"];
  adminHost: NonNullable<AppConfig["adminHost"]>;
  adminPort: NonNullable<AppConfig["adminPort"]>;
  defaultWorkingDirectory: AppConfig["defaultWorkingDirectory"];
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
  claudeModel: AppConfig["claudeModel"];
  claudeMaxTurns: AppConfig["claudeMaxTurns"];
  claudeCommandTimeoutMs: AppConfig["claudeCommandTimeoutMs"];
  claudeMaxRuntimeMs: AppConfig["claudeMaxRuntimeMs"];
  autoSplitThreadUserMessageCount: AppConfig["autoSplitThreadUserMessageCount"];
  settingsFile: string;
  settingsSavedAt?: string;
}

function writeJson(response: ServerResponse, statusCode: number, body: unknown): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "application/json; charset=utf-8");
  response.end(JSON.stringify(body));
}

function writeHtml(response: ServerResponse, statusCode: number, body: string): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "text/html; charset=utf-8");
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

function summarizeSession(
  session: SessionBinding,
  latestExecution: ExecutionRecord | undefined,
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
    connectionMode: readConnectionMode(session),
    approvalPolicyOverride: readApprovalPolicyOverride(session) ?? "default",
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

function renderDashboard(input: DashboardData, view: "dashboard" | "settings"): string {
  const isSettingsView = view === "settings";
  const sessionCount = input.sessions.length;
  const approvalCount = input.approvals.length;
  const failureCount = input.failures.length;
  const renderCancelAction = (session: Record<string, any>): string => {
    return session.latestExecution?.status === "running"
      ? renderButtonMarkup({
          label: "Cancel",
          action: "cancel",
          sessionId: session.sessionId,
        })
      : `<span class="muted-note">Idle</span>`;
  };
  const sessionsMarkup = renderTableMarkup({
    testId: "sessions-table",
    headers: ["Session", "Agent", "Workspace", "Thread", "State", "Action"],
    rows: input.sessions.map((session) => [
      `<div class="session-cell"><strong>${escapeHtml(session.sessionId)}</strong><span>${escapeHtml(session.connectionMode)}</span></div>`,
      `<span class="pill">${escapeHtml(session.agent)}</span>`,
      `<code>${escapeHtml(session.workingDirectory)}</code>`,
      `<code>${escapeHtml(session.provider)}:${escapeHtml(session.threadId)}</code>`,
      `<div class="session-cell"><span class="status-${escapeHtml(session.latestExecution?.status ?? "idle")}">${escapeHtml(session.latestExecution?.status ?? "idle")}</span><span>${escapeHtml(session.latestExecution?.updatedAt ?? session.updatedAt)}</span></div>`,
      renderCancelAction(session),
    ]),
  });
  const approvalsMarkup = renderTableMarkup({
    testId: "approvals-table",
    headers: ["Session", "Prompt", "Requested", "Actions"],
    rows: input.approvals.map((entry) => [
      `<code>${escapeHtml(entry.sessionId)}</code>`,
      escapeHtml(entry.prompt),
      escapeHtml(entry.requestedAt),
      [
        renderButtonMarkup({
          label: "Approve",
          action: "approve",
          sessionId: entry.sessionId,
        }),
        renderButtonMarkup({
          label: "Reject",
          action: "reject",
          sessionId: entry.sessionId,
        }),
      ].join(" "),
    ]),
  });
  const failuresMarkup = renderTableMarkup({
    testId: "failures-table",
    headers: ["Session", "Status", "Summary"],
    rows: input.failures.map((entry) => [
      `<code>${escapeHtml(entry.sessionId)}</code>`,
      `<span class="status-${escapeHtml(entry.status)}">${escapeHtml(entry.status)}</span>`,
      escapeHtml(entry.error || entry.summary || ""),
    ]),
  });
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Chat Agent Bridge Admin</title>
    <style>
      :root {
        color-scheme: light;
        --bg: #f4f1ea;
        --panel: #fffdf8;
        --ink: #1e1d1a;
        --muted: #6a665d;
        --line: #d8cfbe;
        --accent: #0f766e;
        --warn: #b45309;
        --fail: #b91c1c;
      }
      body {
        margin: 0;
        font: 14px/1.5 "Segoe UI", sans-serif;
        color: var(--ink);
        background:
          radial-gradient(circle at top left, #efe5d5, transparent 32%),
          linear-gradient(180deg, #fbf8f2 0%, var(--bg) 100%);
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
        grid-template-columns: minmax(0, 1.35fr) minmax(320px, 0.72fr);
        gap: 18px;
        margin-top: 24px;
        align-items: start;
      }
      .hero {
        display: flex;
        justify-content: space-between;
        gap: 20px;
        align-items: end;
      }
      .nav {
        display: inline-flex;
        gap: 8px;
        padding: 4px;
        border: 1px solid var(--line);
        border-radius: 999px;
        background: rgba(255, 253, 248, 0.72);
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
        background: #fff8eb;
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
        background: rgba(255, 253, 248, 0.72);
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
        box-shadow: 0 14px 30px rgba(30, 29, 26, 0.06);
        overflow: hidden;
      }
      section[data-testid="sessions-panel"] {
        grid-column: 1;
        grid-row: 1 / span 2;
      }
      section[data-testid="approvals-panel"] {
        grid-column: 2;
        grid-row: 1;
      }
      section[data-testid="failures-panel"] {
        grid-column: 2;
        grid-row: 2;
      }
      section[data-testid="detail-panel"] {
        grid-column: 1 / span 2;
        grid-row: 3;
      }
      section[data-testid="settings-panel"] {
        grid-column: 1 / span 2;
        grid-row: 4;
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
      .status-completed { color: #166534; }
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
        background: white;
        color: var(--ink);
      }
      .table-wrap tr.is-selected {
        background: #f7f1e3;
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
        background: #fff8eb;
      }
      .mini-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
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
      .detail-card {
        border: 1px solid var(--line);
        border-radius: 14px;
        padding: 12px 14px;
        background: rgba(255, 253, 248, 0.72);
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
      .history-list {
        display: grid;
        gap: 10px;
      }
      .history-item {
        border: 1px solid var(--line);
        border-radius: 14px;
        padding: 12px 14px;
        background: white;
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
        border: 1px solid var(--line);
        background: #fff8eb;
      }
      .flash[data-kind="error"] {
        border-color: #efb3b3;
        background: #fff1f1;
        color: var(--fail);
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
      button {
        border: 1px solid var(--line);
        background: white;
        color: var(--ink);
        border-radius: 999px;
        padding: 8px 14px;
        cursor: pointer;
        margin: 0 6px 6px 0;
        transition: transform 120ms ease, border-color 120ms ease, background 120ms ease;
      }
      button:hover {
        transform: translateY(-1px);
        border-color: #bda97e;
        background: #fffaf0;
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
        section[data-testid="sessions-panel"],
        section[data-testid="approvals-panel"],
        section[data-testid="failures-panel"],
        section[data-testid="detail-panel"] {
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
        section[data-testid="sessions-panel"] table {
          min-width: 760px;
        }
      }
    </style>
  </head>
  <body>
    <main>
      <div class="hero">
        <div>
          <h1>Bridge Admin</h1>
          <p>${isSettingsView ? "Bridge configuration and connection settings." : "Sessions, approvals, and failures from the local bridge state."}</p>
        </div>
        <div class="toolbar-group">
          <nav class="nav" aria-label="Admin navigation">
            <a href="/admin" class="${isSettingsView ? "" : "is-active"}" data-testid="nav-dashboard">Dashboard</a>
            <a href="/admin/settings" class="${isSettingsView ? "is-active" : ""}" data-testid="nav-settings">Settings</a>
          </nav>
          <div class="stats">
            <div class="stat" data-testid="sessions-stat"><strong>${sessionCount}</strong><span>Sessions</span></div>
            <div class="stat" data-testid="approvals-stat"><strong>${approvalCount}</strong><span>Pending</span></div>
            <div class="stat" data-testid="failures-stat"><strong>${failureCount}</strong><span>Recent failures</span></div>
          </div>
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
        <section data-testid="approvals-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Pending Approvals</h2>
              <p>Approval queue stays visible beside the active sessions list.</p>
            </div>
          </div>
          <div id="approvals">${approvalsMarkup}</div>
        </section>
        <section data-testid="failures-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Recent Failures</h2>
              <p>Recent failure summaries stay stacked under approvals for faster triage.</p>
            </div>
          </div>
          <div id="failures">${failuresMarkup}</div>
        </section>
        <section data-testid="detail-panel"${isSettingsView ? ' style="display:none"' : ""}>
          <div class="section-head">
            <div>
              <h2>Session Detail</h2>
              <p>Inspect the selected session, current approval state, and recent execution history.</p>
            </div>
          </div>
          <div id="detail" data-testid="session-detail">
            <div class="detail-empty">Select a session from the sessions table to inspect details and available actions.</div>
          </div>
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
    </main>
    <script>
      let dashboardState = {
        sessions: [],
        approvals: [],
        failures: [],
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
          '<div class="session-cell"><strong>' + escapeHtml(session.sessionId) + "</strong><span>" + escapeHtml(session.connectionMode) + "</span></div>",
          '<span class="pill">' + escapeHtml(session.agent) + "</span>",
          "<code>" + escapeHtml(session.workingDirectory) + "</code>",
          "<code>" + escapeHtml(session.provider) + ":" + escapeHtml(session.threadId) + "</code>",
          '<div class="session-cell"><span class="status-' + escapeHtml(session.latestExecution?.status ?? "idle") + '">' +
            escapeHtml(session.latestExecution?.status ?? "idle") + '</span><span>' + escapeHtml(session.latestExecution?.updatedAt ?? session.updatedAt) + "</span></div>",
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
          ["Session", "Agent", "Workspace", "Thread", "State", "Action"],
          renderSessionTableRows(filtered),
        );
        const rows = document.querySelectorAll('[data-testid="sessions-table"] tbody tr');
        rows.forEach((row, index) => {
          if (filtered[index]?.sessionId === dashboardState.selectedSessionId) {
            row.classList.add("is-selected");
          }
        });
        document.querySelector('[data-testid="sessions-stat"] strong').textContent = String(filtered.length);
        persistFilters();
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
          return;
        }
        const approvalActions = session.pendingApproval
          ? renderButtonMarkup("Approve", session.sessionId, "approve") + renderButtonMarkup("Reject", session.sessionId, "reject")
          : "";
        const cancelAction = canCancelSession(session)
          ? renderButtonMarkup("Cancel session", session.sessionId, "cancel")
          : "";
        const sessionCommands = [
          '<div class="field"><label for="detail-agent">Agent</label><select id="detail-agent" data-session-id="' + escapeHtml(session.sessionId) + '"><option value="codex"' + (session.agent === "codex" ? " selected" : "") + '>codex</option><option value="claude-code"' + (session.agent === "claude-code" ? " selected" : "") + '>claude</option></select></div>',
          '<div class="field"><label for="detail-mode">Approval</label><select id="detail-mode" data-session-id="' + escapeHtml(session.sessionId) + '"><option value="manual"' + (session.approvalPolicyOverride === "manual" ? " selected" : "") + '>manual</option><option value="never"' + (session.approvalPolicyOverride === "never" ? " selected" : "") + '>allow</option></select></div>',
        ].join("");
        const pendingMarkup = session.pendingApproval
          ? '<div class="detail-card"><strong>Pending approval</strong><span>' + escapeHtml(session.pendingApproval.requestedAt) + '</span><p>' + escapeHtml(session.pendingApproval.prompt) + "</p></div>"
          : "";
        const historyMarkup = history.length
          ? '<div class="history-list">' + history.map((entry) =>
              '<article class="history-item"><header><strong>' + escapeHtml(entry.executionId) + '</strong><span class="status-' + escapeHtml(entry.status) + '">' + escapeHtml(entry.status) + '</span></header><code>' +
              escapeHtml(entry.updatedAt) + "</code><p><strong>Prompt:</strong> " + escapeHtml(entry.prompt || "") + "</p><p><strong>Result:</strong> " + escapeHtml(entry.summary || entry.error || "") + "</p></article>"
            ).join("") + "</div>"
          : '<div class="detail-empty">No execution history recorded for this session.</div>';
        document.getElementById("detail").innerHTML =
          '<div class="detail-actions">' +
            cancelAction +
            approvalActions +
            '<button data-command="new-thread" data-session-id="' + escapeHtml(session.sessionId) + '">New thread</button>' +
            '<button data-command="fork codex" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Codex</button>' +
            '<button data-command="fork claude" data-session-id="' + escapeHtml(session.sessionId) + '">Fork Claude</button>' +
            '<button data-command="detach" data-session-id="' + escapeHtml(session.sessionId) + '">Detach</button>' +
          "</div>" +
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
        document.getElementById("settings").innerHTML =
          '<div class="settings-grid">' +
            '<div class="field"><label for="settings-agent">Default agent</label><select id="settings-agent" data-testid="settings-agent"><option value="codex"' + (settings.agentKind === "codex" ? " selected" : "") + '>codex</option><option value="claude-code"' + (settings.agentKind === "claude-code" ? " selected" : "") + '>claude</option></select></div>' +
            '<div class="field"><label for="settings-approval">Default approval</label><select id="settings-approval" data-testid="settings-approval"><option value="manual"' + (settings.defaultApprovalPolicy === "manual" ? " selected" : "") + '>manual</option><option value="on-failure"' + (settings.defaultApprovalPolicy === "on-failure" ? " selected" : "") + '>on-failure</option><option value="never"' + (settings.defaultApprovalPolicy === "never" ? " selected" : "") + '>allow</option></select></div>' +
            '<div class="field"><label for="settings-network">Network access</label><select id="settings-network" data-testid="settings-network"><option value="enabled"' + (settings.networkAccess === "enabled" ? " selected" : "") + '>enabled</option><option value="disabled"' + (settings.networkAccess === "disabled" ? " selected" : "") + '>disabled</option></select></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-auto-split-count">Auto-split after user messages</label><input id="settings-auto-split-count" data-testid="settings-auto-split-count" type="number" min="1" value="' + escapeHtml(settings.autoSplitThreadUserMessageCount) + '" /></div>' +
          "</div>" +
          '<div class="section-head"><div><h2>Codex</h2><p>Applied to future Codex runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-codex-model">Model</label><input id="settings-codex-model" data-testid="settings-codex-model" type="text" value="' + escapeHtml(settings.codexModel) + '" /></div>' +
            '<div class="field"><label for="settings-codex-turns">Max turns</label><input id="settings-codex-turns" data-testid="settings-codex-turns" type="number" min="1" value="' + escapeHtml(settings.codexMaxTurns) + '" /></div>' +
            '<div class="field"><label for="settings-codex-timeout">Command timeout ms</label><input id="settings-codex-timeout" data-testid="settings-codex-timeout" type="number" min="1" value="' + escapeHtml(settings.codexCommandTimeoutMs) + '" /></div>' +
            '<div class="field"><label for="settings-codex-runtime">Max runtime ms</label><input id="settings-codex-runtime" data-testid="settings-codex-runtime" type="number" min="1" value="' + escapeHtml(settings.codexMaxRuntimeMs) + '" /></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-codex-output">Max output length</label><input id="settings-codex-output" data-testid="settings-codex-output" type="number" min="1" value="' + escapeHtml(settings.codexMaxOutputLength) + '" /></div>' +
          "</div>" +
          '<div class="section-head"><div><h2>Claude Code</h2><p>Applied to future Claude runs.</p></div></div>' +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-claude-model">Model</label><input id="settings-claude-model" data-testid="settings-claude-model" type="text" value="' + escapeHtml(settings.claudeModel) + '" /></div>' +
            '<div class="field"><label for="settings-claude-turns">Max turns</label><input id="settings-claude-turns" data-testid="settings-claude-turns" type="number" min="1" value="' + escapeHtml(settings.claudeMaxTurns) + '" /></div>' +
            '<div class="field"><label for="settings-claude-timeout">Command timeout ms</label><input id="settings-claude-timeout" data-testid="settings-claude-timeout" type="number" min="1" value="' + escapeHtml(settings.claudeCommandTimeoutMs) + '" /></div>' +
            '<div class="field"><label for="settings-claude-runtime">Max runtime ms</label><input id="settings-claude-runtime" data-testid="settings-claude-runtime" type="number" min="1" value="' + escapeHtml(settings.claudeMaxRuntimeMs) + '" /></div>' +
          "</div>" +
          '<div class="section-head"><div><h2>Restart-required</h2><p>Saved immediately, applied after bridge restart.</p></div></div>' +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-provider">Chat provider</label><select id="settings-provider" data-testid="settings-provider"><option value="slack"' + (settings.chatProvider === "slack" ? " selected" : "") + '>slack</option><option value="mattermost"' + (settings.chatProvider === "mattermost" ? " selected" : "") + '>mattermost</option></select></div>' +
            '<div class="field"><label for="settings-slack-mode">Slack mode</label><select id="settings-slack-mode" data-testid="settings-slack-mode"><option value="http"' + (settings.slackMode === "http" ? " selected" : "") + '>http</option><option value="socket"' + (settings.slackMode === "socket" ? " selected" : "") + '>socket</option></select></div>' +
            '<div class="field"><label for="settings-mattermost-mode">Mattermost mode</label><select id="settings-mattermost-mode" data-testid="settings-mattermost-mode"><option value="http"' + (settings.mattermostMode === "http" ? " selected" : "") + '>http</option><option value="websocket"' + (settings.mattermostMode === "websocket" ? " selected" : "") + '>websocket</option></select></div>' +
            '<div class="field"><label for="settings-workdir">Default workdir</label><input id="settings-workdir" data-testid="settings-workdir" type="text" value="' + escapeHtml(settings.defaultWorkingDirectory) + '" /></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-host">Bridge host</label><input id="settings-host" data-testid="settings-host" type="text" value="' + escapeHtml(settings.host) + '" /></div>' +
            '<div class="field"><label for="settings-port">Bridge port</label><input id="settings-port" data-testid="settings-port" type="number" min="1" value="' + escapeHtml(settings.port) + '" /></div>' +
            '<div class="field"><label for="settings-admin-host">Admin host</label><input id="settings-admin-host" data-testid="settings-admin-host" type="text" value="' + escapeHtml(settings.adminHost) + '" /></div>' +
            '<div class="field"><label for="settings-admin-port">Admin port</label><input id="settings-admin-port" data-testid="settings-admin-port" type="number" min="1" value="' + escapeHtml(settings.adminPort) + '" /></div>' +
          "</div>" +
          '<div class="section-head"><div><h2>Connections</h2><p>Secrets stay masked. Enter a new value only when rotating it.</p></div></div>' +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-mm-server">Mattermost server URL</label><input id="settings-mm-server" data-testid="settings-mm-server" type="text" value="' + escapeHtml(settings.mattermostServerUrl || "") + '" /></div>' +
            '<div class="field"><label for="settings-mm-action-base">Mattermost action base URL</label><input id="settings-mm-action-base" data-testid="settings-mm-action-base" type="text" value="' + escapeHtml(settings.mattermostActionBaseUrl || "") + '" /></div>' +
            '<div class="field"><label class="toggle" for="settings-mm-dm-only"><input id="settings-mm-dm-only" data-testid="settings-mm-dm-only" type="checkbox"' + (settings.mattermostDmOnly ? " checked" : "") + ' />Mattermost DM only</label></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-slack-bot-token">Slack bot token</label><input id="settings-slack-bot-token" data-testid="settings-slack-bot-token" type="password" placeholder="' + (settings.hasSlackBotToken ? "stored" : "not set") + '" /></div>' +
            '<div class="field"><label for="settings-slack-app-token">Slack app token</label><input id="settings-slack-app-token" data-testid="settings-slack-app-token" type="password" placeholder="' + (settings.hasSlackAppToken ? "stored" : "not set") + '" /></div>' +
            '<div class="field"><label for="settings-slack-signing-secret">Slack signing secret</label><input id="settings-slack-signing-secret" data-testid="settings-slack-signing-secret" type="password" placeholder="' + (settings.hasSlackSigningSecret ? "stored" : "not set") + '" /></div>' +
            '<div class="field"><label for="settings-mm-bot-token">Mattermost bot token</label><input id="settings-mm-bot-token" data-testid="settings-mm-bot-token" type="password" placeholder="' + (settings.hasMattermostBotToken ? "stored" : "not set") + '" /></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label for="settings-mm-webhook-token">Mattermost webhook token</label><input id="settings-mm-webhook-token" data-testid="settings-mm-webhook-token" type="password" placeholder="' + (settings.hasMattermostWebhookToken ? "stored" : "not set") + '" /></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label class="toggle" for="clear-slack-bot-token"><input id="clear-slack-bot-token" data-testid="clear-slack-bot-token" type="checkbox" />Clear stored Slack bot token</label></div>' +
            '<div class="field"><label class="toggle" for="clear-slack-app-token"><input id="clear-slack-app-token" data-testid="clear-slack-app-token" type="checkbox" />Clear stored Slack app token</label></div>' +
            '<div class="field"><label class="toggle" for="clear-slack-signing-secret"><input id="clear-slack-signing-secret" data-testid="clear-slack-signing-secret" type="checkbox" />Clear stored Slack signing secret</label></div>' +
          "</div>" +
          '<div class="settings-subgrid">' +
            '<div class="field"><label class="toggle" for="clear-mm-bot-token"><input id="clear-mm-bot-token" data-testid="clear-mm-bot-token" type="checkbox" />Clear stored Mattermost bot token</label></div>' +
            '<div class="field"><label class="toggle" for="clear-mm-webhook-token"><input id="clear-mm-webhook-token" data-testid="clear-mm-webhook-token" type="checkbox" />Clear stored Mattermost webhook token</label></div>' +
          "</div>" +
          '<div class="detail-grid">' +
            '<div class="detail-card"><strong>Chat provider</strong><span>' + escapeHtml(settings.chatProvider) + "</span></div>" +
            '<div class="detail-card"><strong>Slack mode</strong><span>' + escapeHtml(settings.slackMode) + "</span></div>" +
            '<div class="detail-card"><strong>Mattermost mode</strong><span>' + escapeHtml(settings.mattermostMode) + "</span></div>" +
            '<div class="detail-card"><strong>Default workdir</strong><code>' + escapeHtml(settings.defaultWorkingDirectory) + "</code></div>" +
            '<div class="detail-card"><strong>Settings file</strong><code>' + escapeHtml(settings.settingsFile) + "</code></div>" +
            '<div class="detail-card"><strong>Last saved</strong><span>' + escapeHtml(settings.settingsSavedAt || "not saved yet") + "</span></div>" +
          "</div>" +
          '<div class="detail-actions"><button id="save-settings" data-testid="save-settings">Save runtime settings</button><button id="restart-bridge" data-testid="restart-bridge">Restart bridge</button></div>';
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
        dashboardState.approvals = approvalsPayload.approvals;
        dashboardState.failures = failuresPayload.failures;
        syncFilterOptions();
        if (!dashboardState.filtersRestored) {
          restoreFilters();
          syncFilterOptions();
          dashboardState.filtersRestored = true;
        }
        renderSessions();

        document.getElementById("approvals").innerHTML = renderTableMarkup(
          "approvals-table",
          ["Session", "Prompt", "Requested", "Actions"],
          approvalsPayload.approvals.map((entry) => [
            "<code>" + escapeHtml(entry.sessionId) + "</code>",
            escapeHtml(entry.prompt),
            escapeHtml(entry.requestedAt),
            [
              renderButtonMarkup("Approve", entry.sessionId, "approve"),
              renderButtonMarkup("Reject", entry.sessionId, "reject"),
            ].join(" "),
          ]),
        );

        document.getElementById("failures").innerHTML = renderTableMarkup(
          "failures-table",
          ["Session", "Status", "Summary"],
          failuresPayload.failures.map((entry) => [
            "<code>" + escapeHtml(entry.sessionId) + "</code>",
            '<span class="status-' + escapeHtml(entry.status) + '">' + escapeHtml(entry.status) + "</span>",
            escapeHtml(entry.error || entry.summary || ""),
          ]),
        );
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
        const payload = await fetchJson("/admin/api/settings");
        dashboardState.settings = payload.settings;
        renderSettings();
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
          claudeModel: document.getElementById("settings-claude-model").value.trim(),
          claudeMaxTurns: Number.parseInt(document.getElementById("settings-claude-turns").value, 10),
          claudeCommandTimeoutMs: Number.parseInt(document.getElementById("settings-claude-timeout").value, 10),
          claudeMaxRuntimeMs: Number.parseInt(document.getElementById("settings-claude-runtime").value, 10),
          chatProvider: document.getElementById("settings-provider").value,
          slackMode: document.getElementById("settings-slack-mode").value,
          mattermostMode: document.getElementById("settings-mattermost-mode").value,
          defaultWorkingDirectory: document.getElementById("settings-workdir").value.trim(),
          host: document.getElementById("settings-host").value.trim(),
          port: Number.parseInt(document.getElementById("settings-port").value, 10),
          adminHost: document.getElementById("settings-admin-host").value.trim(),
          adminPort: Number.parseInt(document.getElementById("settings-admin-port").value, 10),
          mattermostServerUrl: document.getElementById("settings-mm-server").value.trim(),
          mattermostActionBaseUrl: document.getElementById("settings-mm-action-base").value.trim(),
          mattermostDmOnly: document.getElementById("settings-mm-dm-only").checked,
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

export class AdminServer {
  private readonly config: AdminServerDeps["config"];
  private readonly stateStore: StateStore;
  private readonly auditLogger: AuditLogger;
  private readonly bridge: BridgeService;
  private server: http.Server | undefined;

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
    await new Promise<void>((resolve, reject) => {
      server.close((error) => {
        if (error) {
          reject(error);
          return;
        }

        resolve();
      });
    });
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

    if (request.method === "GET" && url.pathname === "/admin") {
      writeHtml(response, 200, renderDashboard(await this.readDashboardData(), "dashboard"));
      return;
    }

    if (request.method === "GET" && url.pathname === "/admin/settings") {
      writeHtml(response, 200, renderDashboard(await this.readDashboardData(), "settings"));
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
    const sessions = await this.stateStore.listSessions();
    const latestRecords = await this.stateStore.listExecutionRecords({
      limit: Math.max(sessions.length * 5, 50),
    });
    const latestBySession = new Map<string, ExecutionRecord>();
    for (const record of latestRecords) {
      if (!latestBySession.has(record.sessionId)) {
        latestBySession.set(record.sessionId, record);
      }
    }

    const summarizedSessions = sessions
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .map((session) => summarizeSession(session, latestBySession.get(session.sessionId)));
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
      .slice(0, Math.max(limit, 1));

    return {
      sessions: summarizedSessions,
      approvals,
      failures,
    };
  }

  private async handleListSessions(response: ServerResponse): Promise<void> {
    const payload = (await this.readDashboardData()).sessions;
    await this.auditLogger.log({
      type: "admin.api.sessions",
      count: payload.length,
    });
    writeJson(response, 200, {
      sessions: payload,
    });
  }

  private async handleGetSession(response: ServerResponse, sessionId: string): Promise<void> {
    const sessions = await this.stateStore.listSessions();
    const session = sessions.find((entry) => entry.sessionId === sessionId);
    if (!session) {
      writeJson(response, 404, { error: "Session not found" });
      return;
    }

    const records = await this.stateStore.listExecutionRecords({
      sessionId,
      limit: 20,
    });
    writeJson(response, 200, {
      session: summarizeSession(session, records[0]),
      executionHistory: records,
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

  private readRuntimeSettings(): RuntimeSettingsPayload {
    const settingsSavedAt = fs.existsSync(this.config.settingsFile)
      ? fs.statSync(this.config.settingsFile).mtime.toISOString()
      : undefined;
    return {
      agentKind: this.config.agentKind,
      defaultApprovalPolicy: this.config.defaultApprovalPolicy,
      networkAccess: this.config.networkAccess,
      chatProvider: this.config.chatProvider,
      slackMode: this.config.slackMode,
      mattermostMode: this.config.mattermostMode,
      defaultWorkingDirectory: this.config.defaultWorkingDirectory,
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
      claudeModel: this.config.claudeModel,
      claudeMaxTurns: this.config.claudeMaxTurns,
      claudeCommandTimeoutMs: this.config.claudeCommandTimeoutMs,
      claudeMaxRuntimeMs: this.config.claudeMaxRuntimeMs,
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
      chatProvider: this.config.chatProvider,
      agentKind: this.config.agentKind,
      defaultApprovalPolicy: this.config.defaultApprovalPolicy,
      networkAccess: this.config.networkAccess,
      slackMode: this.config.slackMode,
      mattermostMode: this.config.mattermostMode,
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
      claudeModel: this.config.claudeModel,
      claudeMaxTurns: this.config.claudeMaxTurns,
      claudeCommandTimeoutMs: this.config.claudeCommandTimeoutMs,
      claudeMaxRuntimeMs: this.config.claudeMaxRuntimeMs,
      autoSplitThreadUserMessageCount: this.config.autoSplitThreadUserMessageCount,
    };
    fs.mkdirSync(path.dirname(this.config.settingsFile), { recursive: true });
    fs.writeFileSync(
      this.config.settingsFile,
      JSON.stringify(payload, null, 2),
      "utf8",
    );
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

    if (payload.agentKind && payload.agentKind !== "codex" && payload.agentKind !== "claude-code") {
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
      "claudeMaxTurns",
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
    if (payload.defaultWorkingDirectory !== undefined) {
      try {
        payload.defaultWorkingDirectory = assertPathAllowed(
          payload.defaultWorkingDirectory,
          this.config.allowedDirectories,
        );
      } catch (error) {
        writeJson(response, 400, {
          error: error instanceof Error ? error.message : "Invalid defaultWorkingDirectory",
        });
        return;
      }
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
    if (payload.slackMode) {
      this.config.slackMode = payload.slackMode;
    }
    if (payload.mattermostMode) {
      this.config.mattermostMode = payload.mattermostMode;
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
    if (payload.claudeModel) {
      this.config.claudeModel = payload.claudeModel;
    }
    if (payload.claudeMaxTurns) {
      this.config.claudeMaxTurns = payload.claudeMaxTurns;
    }
    if (payload.claudeCommandTimeoutMs) {
      this.config.claudeCommandTimeoutMs = payload.claudeCommandTimeoutMs;
    }
    if (payload.claudeMaxRuntimeMs) {
      this.config.claudeMaxRuntimeMs = payload.claudeMaxRuntimeMs;
    }

    await this.auditLogger.log({
      type: "admin.settings.updated",
      settings: {
        ...this.readRuntimeSettings(),
      },
    });
    this.persistSettingsFile();
    writeJson(response, 200, {
      ok: true,
      settings: this.readRuntimeSettings(),
    });
  }

  private async handleBridgeRestart(response: ServerResponse): Promise<void> {
    await this.bridge.requestAdminRestart();
    writeJson(response, 202, {
      ok: true,
      status: "queued",
    });
  }
}
