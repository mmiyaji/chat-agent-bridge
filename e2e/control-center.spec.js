import { expect, test } from "@playwright/test";

const settings = {
  chatProvider: "slack",
  agentKind: "codex",
  slackMode: "socket",
  mattermostMode: "websocket",
  mattermostActorMode: "bot",
  slackAllowedUserIds: "U-E2E",
  mattermostAllowedUserIds: "",
  defaultWorkingDirectory: "C:/repo",
  allowedDirectories: "C:/repo",
  defaultApprovalPolicy: "manual",
  networkAccess: "disabled",
  codexModel: "gpt-test",
  claudeModel: "sonnet",
  cursorModel: "Auto",
  geminiModel: "gemini-test",
  mattermostServerUrl: "https://chat.example.test",
  mattermostActionBaseUrl: "",
  hasSlackBotToken: true,
  hasSlackAppToken: true,
  hasSlackSigningSecret: false,
  hasMattermostBotToken: false,
  hasMattermostWebhookToken: false,
};

const sessions = [
  {
    sessionId: "session-e2e-1",
    agent: "codex",
    provider: "slack",
    channelId: "C-OPS",
    threadId: "T-1",
    workingDirectory: "C:/repo",
    updatedAt: "2026-09-29T02:00:00.000Z",
    latestExecution: { executionId: "exec-1", status: "completed", prompt: "Release notes", summary: "Release notes are ready." },
  },
  {
    sessionId: "session-e2e-2",
    agent: "codex",
    provider: "slack",
    channelId: "C-OPS",
    threadId: "T-2",
    workingDirectory: "C:/repo",
    updatedAt: "2026-09-29T02:01:00.000Z",
    pendingApproval: { prompt: "Update deployment config", requestedAt: "2026-09-29T02:01:00.000Z", reason: "Write approval required" },
  },
];

async function mockApi(page, { connections, saveResponse, actionError } = {}) {
  const calls = [];
  await page.route("**/admin/api/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    calls.push({ path, method, headers: request.headers(), body: request.postDataJSON?.() });
    let payload;
    if (path === "/admin/api/settings" && method === "GET") payload = { settings };
    else if (path === "/admin/api/settings" && method === "POST") payload = saveResponse || { settings: { ...settings, chatProvider: request.postDataJSON().chatProvider || settings.chatProvider } };
    else if (path === "/admin/api/connections" && method === "GET") payload = connections || {
      chat: { provider: "slack", mode: "socket", connected: false, state: "disconnected", statusText: "未接続" },
      agent: { kind: "codex", state: "disconnected", statusText: "未確認", model: "gpt-test", authenticated: true },
      busy: false,
    };
    else if (path === "/admin/api/connections/chat/connect" && method === "POST" && actionError) {
      await route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ error: actionError }) });
      return;
    }
    else if (path === "/admin/api/sessions") payload = { sessions };
    else if (path === "/admin/api/approvals") payload = { approvals: [{ sessionId: "session-e2e-2", provider: "slack", agent: "codex", prompt: "Update deployment config", reason: "Write approval required", requestedAt: "2026-09-29T02:01:00.000Z" }] };
    else if (path === "/admin/api/failures") payload = { failures: [] };
    else payload = { ok: true };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(payload) });
  });
  return calls;
}

test("control center shows real connection and conversation state across tabs", async ({ page }) => {
  await mockApi(page, { connections: {
    chat: { provider: "slack", mode: "socket", connected: true, state: "connected", statusText: "Socket Mode で接続しています" },
    agent: { kind: "codex", state: "connected", statusText: "CLI と認証を確認しました", model: "gpt-test", authenticated: true },
    busy: false,
  } });
  await page.goto("/admin");

  await expect(page.getByTestId("overview-chat-state")).toHaveText("接続中");
  await expect(page.getByTestId("overview-agent-state")).toHaveText("接続中");
  await expect(page.getByTestId("stat-sessions")).toHaveText("2");
  await expect(page.getByTestId("recent-sessions")).toContainText("Release notes");

  await page.getByRole("button", { name: "会話セッション" }).click();
  await expect(page.getByTestId("sessions-table")).toContainText("Update deployment config");
  await expect(page.getByTestId("approvals-list")).toContainText("承認待ち");
  await page.getByLabel("状態で絞り込み").selectOption("completed");
  await expect(page.locator("#sessions-table tbody tr:visible")).toHaveCount(1);
  await expect(page.locator("#sessions-table tbody tr:visible")).toContainText("Release notes");
});

test("settings save and connection actions use the admin request header", async ({ page }) => {
  const calls = await mockApi(page);
  await page.goto("/admin");
  await page.getByRole("button", { name: "接続と設定" }).click();
  await page.getByTestId("chat-provider").selectOption("mattermost");
  await page.getByTestId("mattermost-url").fill("https://mm.example.test");
  await page.getByTestId("save-chat").click();
  await expect(page.getByTestId("chat-message")).toContainText("設定を保存しました");
  await page.getByTestId("connect-chat").click();
  await expect.poll(() => calls.filter((call) => call.path === "/admin/api/connections/chat/connect").length).toBe(1);

  const save = calls.find((call) => call.path === "/admin/api/settings" && call.method === "POST");
  expect(save.headers["x-cab-request"]).toBe("admin");
  expect(save.body.mattermostServerUrl).toBe("https://mm.example.test");
  expect(save.body.chatProvider).toBe("mattermost");
  const connect = calls.find((call) => call.path === "/admin/api/connections/chat/connect");
  expect(connect.headers["x-cab-request"]).toBe("admin");
});

test("connection failures are shown as text and layout fits a phone viewport", async ({ page }) => {
  await mockApi(page, { actionError: "<img src=x onerror=alert(1)> 接続できません" });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/admin");
  await page.getByRole("button", { name: "接続と設定" }).click();
  await page.getByTestId("connect-chat").click();
  await expect(page.getByTestId("chat-message")).toContainText("接続できません");
  await expect(page.locator("#chat-message img")).toHaveCount(0);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
});
