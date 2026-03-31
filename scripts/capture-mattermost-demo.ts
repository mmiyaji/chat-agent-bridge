import fs from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { spawnSync } from "node:child_process";
import { chromium } from "@playwright/test";
import { MattermostAdapter } from "../src/adapters/mattermost/mattermost-adapter.js";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import type { Agent } from "../src/agents/base.js";
import type { AgentExecutionRequest, AgentExecutionResult, AppConfig } from "../src/core/types.js";
import { InMemoryStateStore, InlineJobQueue, RecordingAuditLogger } from "../test/helpers/fakes.js";

interface BootstrapState {
  serverUrl: string;
  team: {
    id: string;
    name: string;
  };
  bridgeUser: {
    id: string;
    username: string;
    password: string;
    token: string;
  };
  memberUser: {
    id: string;
    username: string;
    password: string;
    token: string;
  };
}

interface MattermostChannel {
  id: string;
  name?: string;
}

interface MattermostPost {
  id: string;
  channel_id: string;
  root_id?: string;
  user_id: string;
  message: string;
}

interface MattermostPostList {
  order: string[];
  posts: Record<string, MattermostPost>;
}

class MattermostDemoAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    await request.onActivity?.({
      stream: "stdout",
      text: "Inspecting the thread request...",
    });
    await sleep(500);
    await request.onActivity?.({
      stream: "stdout",
      text: "Preparing a bridge-managed demo response...",
    });
    await sleep(600);
    await request.onActivity?.({
      stream: "stdout",
      text: "Composing the final response for Mattermost...",
    });
    await sleep(500);
    return {
      sessionId: request.session.sessionId,
      summary: "Implemented the README-ready Mattermost demo.",
    };
  }

  async cancelExecution(): Promise<boolean> {
    return false;
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function apiRequest<T>(
  serverUrl: string,
  requestPath: string,
  token: string,
  init?: {
    method?: string;
    body?: unknown;
  },
): Promise<T> {
  const response = await fetch(`${serverUrl}${requestPath}`, {
    method: init?.method ?? "GET",
    headers: {
      Authorization: `Bearer ${token}`,
      ...(init?.body ? { "Content-Type": "application/json" } : {}),
    },
    body: init?.body ? JSON.stringify(init.body) : undefined,
  });

  if (!response.ok) {
    throw new Error(`${init?.method ?? "GET"} ${requestPath} failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

async function waitFor<T>(
  callback: () => Promise<T | undefined>,
  timeoutMs = 30_000,
  intervalMs = 500,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await callback();
    if (result !== undefined) {
      return result;
    }

    await sleep(intervalMs);
  }

  throw new Error(`Timed out after ${timeoutMs}ms.`);
}

function createDemoConfig(
  state: BootstrapState,
  port: number,
  tempRoot: string,
  actionBaseUrl: string,
): AppConfig {
  return {
    chatProvider: "mattermost",
    agentKind: "codex",
    allowedDirectories: [process.cwd()],
    repoAliases: {},
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    mattermostMode: "websocket",
    mattermostActorMode: "bot",
    port,
    host: "0.0.0.0",
    defaultWorkingDirectory: process.cwd(),
    settingsFile: path.join(tempRoot, "settings.json"),
    stateFile: path.join(tempRoot, "state.json"),
    auditLogFile: path.join(tempRoot, "audit.jsonl"),
    codexSessionIndexFile: path.join(tempRoot, "codex-session-index.jsonl"),
    claudeSessionIndexFile: path.join(tempRoot, "claude-session-index.json"),
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 1_000,
    codexMaxRuntimeMs: 20_000,
    codexMaxOutputLength: 12_000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1_000,
    claudeMaxRuntimeMs: 20_000,
    autoSplitThreadUserMessageCount: 30,
    mattermostServerUrl: state.serverUrl,
    mattermostBotToken: state.bridgeUser.token,
    mattermostActionBaseUrl: actionBaseUrl,
    mattermostDmOnly: false,
  };
}

async function login(page: import("@playwright/test").Page, state: BootstrapState): Promise<void> {
  await page.goto(`${state.serverUrl}/landing#/login`);
  const browserChoice = page.locator("text=View in Browser");
  const loginId = page.locator('input[name="loginId"]');

  await Promise.race([
    browserChoice.waitFor({ state: "visible", timeout: 15_000 }).catch(() => undefined),
    loginId.waitFor({ state: "visible", timeout: 15_000 }).catch(() => undefined),
  ]);

  if (await browserChoice.isVisible().catch(() => false)) {
    await browserChoice.click();
  }

  await loginId.waitFor({ state: "visible", timeout: 30_000 });
  await loginId.fill(state.memberUser.username);
  await page.locator('input[name="password-input"]').fill(state.memberUser.password);
  await page.getByRole("button", { name: /Log in/i }).click();
  await page.waitForURL(/channels|messages/, { timeout: 30_000 });
}

async function maybeDismissOnboarding(page: import("@playwright/test").Page): Promise<void> {
  const dismiss = page.getByText(/No thanks/i).first();
  if ((await dismiss.count()) > 0) {
    await dismiss.click().catch(() => undefined);
  }
}

async function waitForPostByMessage(
  state: BootstrapState,
  channelId: string,
  token: string,
  message: string,
): Promise<MattermostPost> {
  return await waitFor(async () => {
    const posts = await apiRequest<MattermostPostList>(
      state.serverUrl,
      `/api/v4/channels/${channelId}/posts?page=0&per_page=50`,
      token,
    );

    return posts.order
      .map((postId) => posts.posts[postId])
      .find((post) => post?.message === message);
  }, 30_000, 500);
}

async function waitForThreadReplyByMessage(
  state: BootstrapState,
  channelId: string,
  rootId: string,
  token: string,
  messagePattern: RegExp,
): Promise<MattermostPost> {
  return await waitFor(async () => {
    const posts = await apiRequest<MattermostPostList>(
      state.serverUrl,
      `/api/v4/channels/${channelId}/posts?page=0&per_page=50`,
      token,
    );

    return posts.order
      .map((postId) => posts.posts[postId])
      .find((post) => post?.root_id === rootId && messagePattern.test(post.message));
  }, 30_000, 500);
}

async function convertVideoToGif(videoPath: string, gifPath: string, palettePath: string): Promise<void> {
  const paletteGen = spawnSync(
    "ffmpeg",
    [
      "-y",
      "-i",
      videoPath,
      "-vf",
      "fps=10,scale=1200:-1:flags=lanczos,palettegen",
      palettePath,
    ],
    {
      stdio: "ignore",
    },
  );
  if (paletteGen.status !== 0) {
    throw new Error("ffmpeg palette generation failed.");
  }

  const gif = spawnSync(
    "ffmpeg",
    [
      "-y",
      "-i",
      videoPath,
      "-i",
      palettePath,
      "-lavfi",
      "fps=10,scale=1200:-1:flags=lanczos[x];[x][1:v]paletteuse",
      gifPath,
    ],
    {
      stdio: "ignore",
    },
  );
  if (gif.status !== 0) {
    throw new Error("ffmpeg gif conversion failed.");
  }
}

function resolveDefaultActionBaseUrl(port: number): string {
  const inspect = spawnSync(
    "docker",
    [
      "inspect",
      "cab-mattermost-preview",
      "--format",
      "{{range.NetworkSettings.Networks}}{{.Gateway}}{{end}}",
    ],
    {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    },
  );
  const gateway = inspect.status === 0 ? inspect.stdout.trim() : "";
  if (gateway) {
    return `http://${gateway}:${port}`;
  }

  return `http://host.docker.internal:${port}`;
}

async function main(): Promise<void> {
  const stateFile = path.resolve(
    process.env.CAB_MATTERMOST_E2E_STATE_FILE ?? "./data/runtime/mattermost-e2e.json",
  );
  const outputDir = path.resolve(process.env.CAB_MATTERMOST_DEMO_OUTPUT_DIR ?? "./docs/assets");
  const port = Number.parseInt(process.env.CAB_MATTERMOST_DEMO_PORT ?? "37987", 10);
  const actionBaseUrl =
    process.env.CAB_MATTERMOST_DEMO_ACTION_BASE_URL ?? resolveDefaultActionBaseUrl(port);
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-demo-"));
  const videoTempDir = path.join(tempRoot, "video");
  await fs.mkdir(videoTempDir, { recursive: true });
  await fs.mkdir(outputDir, { recursive: true });

  const demoChannelName = `demo-${Date.now()}`;
  const demoChannel = await apiRequest<MattermostChannel>(
    state.serverUrl,
    "/api/v4/channels",
    state.memberUser.token,
    {
      method: "POST",
      body: {
        team_id: state.team.id,
        name: demoChannelName,
        display_name: demoChannelName,
        type: "O",
      },
    },
  );
  await apiRequest(
    state.serverUrl,
    `/api/v4/channels/${demoChannel.id}/members`,
    state.memberUser.token,
    {
      method: "POST",
      body: {
        user_id: state.bridgeUser.id,
      },
    },
  );

  const config = createDemoConfig(state, port, tempRoot, actionBaseUrl);
  const chatAdapter = new MattermostAdapter(config);
  const stateStore = new InMemoryStateStore();
  const bridge = new BridgeService({
    config,
    stateStore,
    sessionManager: new SessionManager(stateStore),
    jobQueue: new InlineJobQueue(),
    chatAdapter,
    agents: {
      codex: new MattermostDemoAgent(),
    },
    auditLogger: new RecordingAuditLogger(),
  });
  await bridge.start();

  const browser = await chromium.launch({
    headless: true,
  });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    recordVideo: {
      dir: videoTempDir,
      size: { width: 1440, height: 900 },
    },
  });
  const page = await context.newPage();
  let videoPath = "";

  try {
    await login(page, state);
    await maybeDismissOnboarding(page);
    await page.goto(`${state.serverUrl}/${state.team.name}/channels/${demoChannelName}`);
    await page.waitForLoadState("networkidle");

    const token = `README demo ${Date.now()}`;
    const prompt = `@${state.bridgeUser.username} ${token}`;
    const composer = page.locator('textarea[data-testid="post_textbox"]');
    await composer.fill(prompt);
    await sleep(400);
    await composer.press("Enter");

    const rootPost = await waitForPostByMessage(
      state,
      demoChannel.id,
      state.memberUser.token,
      prompt,
    );

    await waitForThreadReplyByMessage(
      state,
      demoChannel.id,
      rootPost.id,
      state.memberUser.token,
      /Implemented the README-ready Mattermost demo\./,
    );

    await page.goto(`${state.serverUrl}/${state.team.name}/channels/${demoChannelName}`);
    await page.waitForLoadState("networkidle");

    const rootCard = page.locator(".post").filter({ hasText: token }).first();
    await rootCard.waitFor({ state: "visible", timeout: 30_000 });

    const replyLink = rootCard.getByText(/reply/i).first();
    await replyLink.waitFor({ state: "visible", timeout: 30_000 });
    await sleep(400);
    await replyLink.click();

    await page
      .getByText("Implemented the README-ready Mattermost demo.")
      .waitFor({ state: "visible", timeout: 30_000 });
    await sleep(1_200);

    await page.screenshot({
      path: path.join(outputDir, "mattermost-demo-final.png"),
      fullPage: true,
    });
    videoPath = await page.video()!.path();
  } finally {
    await context.close();
    await browser.close();
    await chatAdapter.stop().catch(() => undefined);
  }

  const webmPath = path.join(outputDir, "mattermost-demo.webm");
  const gifPath = path.join(outputDir, "mattermost-demo.gif");
  const palettePath = path.join(tempRoot, "mattermost-demo-palette.png");
  await fs.copyFile(videoPath, webmPath);
  await convertVideoToGif(videoPath, gifPath, palettePath);

  console.log(`Saved Mattermost demo video: ${webmPath}`);
  console.log(`Saved Mattermost demo gif: ${gifPath}`);
  console.log(`Saved Mattermost demo screenshot: ${path.join(outputDir, "mattermost-demo-final.png")}`);
}

void main().catch((error) => {
  console.error("Failed to capture Mattermost demo.", error);
  process.exit(1);
});
