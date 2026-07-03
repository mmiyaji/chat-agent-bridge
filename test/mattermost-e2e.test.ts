import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { MattermostAdapter } from "../src/adapters/mattermost/mattermost-adapter.js";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import { AgentExecutionCancelledError, type Agent } from "../src/agents/base.js";
import type {
  AgentExecutionRequest,
  AgentExecutionResult,
  AppConfig,
  IncomingChatMessage,
} from "../src/core/types.js";
import {
  InMemoryStateStore,
  InlineJobQueue,
  RecordingAuditLogger,
} from "./helpers/fakes.js";

interface BootstrapState {
  serverUrl: string;
  channel: {
    id: string;
    name: string;
  };
  bridgeUser: {
    id: string;
    username: string;
    token: string;
  };
  memberUser: {
    id: string;
    username: string;
    token: string;
  };
}

interface MattermostPost {
  id: string;
  root_id?: string;
  channel_id: string;
  message: string;
  user_id: string;
}

interface MattermostPostList {
  order: string[];
  posts: Record<string, MattermostPost>;
}

interface MattermostChannel {
  id: string;
  type: string;
}

interface MattermostActionIntegration {
  url: string;
  context: {
    action: string;
    token: string;
  };
}

interface MattermostPostWithProps extends MattermostPost {
  props?: {
    attachments?: Array<{
      actions?: Array<{
        id?: string;
        name?: string;
        integration?: MattermostActionIntegration;
      }>;
    }>;
  };
}

const enabled = process.env.CAB_ENABLE_MATTERMOST_E2E === "1";
const stateFile = path.resolve(
  process.env.CAB_MATTERMOST_E2E_STATE_FILE ?? "./data/runtime/mattermost-e2e.json",
);

function createMattermostBridgeConfig(
  state: BootstrapState,
  webhookToken: string,
  tempRoot: string,
): AppConfig {
  return {
    chatProvider: "mattermost",
    agentKind: "codex",
    allowedDirectories: [process.cwd()],
    repoAliases: {},
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    mattermostMode: "http",
    port: 0,
    host: "127.0.0.1",
    defaultWorkingDirectory: process.cwd(),
    settingsFile: path.join(tempRoot, "settings.json"),
    stateFile: path.join(tempRoot, "state.json"),
    auditLogFile: path.join(tempRoot, "audit.jsonl"),
    codexSessionIndexFile: path.join(tempRoot, "codex-session-index.jsonl"),
    claudeSessionIndexFile: path.join(tempRoot, "claude-session-index.json"),
    claudeProjectsDir: path.join(tempRoot, "claude-projects"),
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 1_000,
    codexMaxRuntimeMs: 10_000,
    codexMaxOutputLength: 12_000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1_000,
    claudeMaxRuntimeMs: 10_000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
    mattermostServerUrl: state.serverUrl,
    mattermostBotToken: state.bridgeUser.token,
    mattermostWebhookToken: webhookToken,
  };
}

class ApprovalE2EAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;
  readonly requests: AgentExecutionRequest[] = [];

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    this.requests.push(request);
    if (this.requests.length === 1) {
      return {
        sessionId: request.session.sessionId,
        summary: "Blocked by read-only sandbox. I need write access to continue.",
      };
    }

    return {
      sessionId: request.session.sessionId,
      summary: "Applied the requested changes.",
    };
  }

  async cancelExecution(): Promise<boolean> {
    return false;
  }
}

class CancelE2EAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;
  readonly requests: AgentExecutionRequest[] = [];
  readonly cancelRequests: string[] = [];
  private pendingReject?: (error: Error) => void;

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    this.requests.push(request);
    return await new Promise<AgentExecutionResult>((_resolve, reject) => {
      this.pendingReject = reject;
    });
  }

  async cancelExecution(sessionId: string): Promise<boolean> {
    this.cancelRequests.push(sessionId);
    if (!this.pendingReject) {
      return false;
    }

    const reject = this.pendingReject;
    this.pendingReject = undefined;
    reject(new AgentExecutionCancelledError());
    return true;
  }
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
  intervalMs = 1_000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await callback();
    if (result !== undefined) {
      return result;
    }
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }

  throw new Error(`Timed out after ${timeoutMs}ms.`);
}

async function startMattermostBridge(input: {
  state: BootstrapState;
  webhookToken: string;
  agent: Agent;
}) {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-bridge-"));
  const config = createMattermostBridgeConfig(input.state, input.webhookToken, tempRoot);
  const chatAdapter = new MattermostAdapter(config);
  const stateStore = new InMemoryStateStore();
  const bridge = new BridgeService({
    config,
    stateStore,
    sessionManager: new SessionManager(stateStore),
    jobQueue: new InlineJobQueue(),
    chatAdapter,
    agents: {
      codex: input.agent,
    },
    auditLogger: new RecordingAuditLogger(),
  });

  await bridge.start();
  return { bridge, chatAdapter, stateStore, config };
}

async function fetchRecentPosts(
  state: BootstrapState,
  channelId: string,
  token: string,
): Promise<MattermostPostWithProps[]> {
  const posts = await apiRequest<{
    order: string[];
    posts: Record<string, MattermostPostWithProps>;
  }>(
    state.serverUrl,
    `/api/v4/channels/${channelId}/posts?page=0&per_page=50`,
    token,
  );

  return posts.order
    .map((postId) => posts.posts[postId])
    .filter((post): post is MattermostPostWithProps => Boolean(post));
}

async function waitForThreadPost(
  state: BootstrapState,
  channelId: string,
  token: string,
  predicate: (post: MattermostPostWithProps) => boolean,
): Promise<MattermostPostWithProps> {
  return await waitFor(async () => {
    const posts = await fetchRecentPosts(state, channelId, token);
    return posts.find(predicate);
  }, 30_000, 500);
}

async function postJsonToUrl(url: string, body: unknown): Promise<string> {
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(`POST ${url} failed with ${response.status}`);
  }

  return await response.text();
}

function buildMattermostActionUrl(adapter: MattermostAdapter): string {
  const port = adapter.getListeningPort();
  assert.ok(port);
  return `http://127.0.0.1:${port}/mattermost/actions`;
}

async function dispatchMattermostWebhook(input: {
  adapter: MattermostAdapter;
  webhookToken: string;
  channelId: string;
  userId: string;
  postId: string;
  text: string;
}): Promise<void> {
  const port = input.adapter.getListeningPort();
  assert.ok(port);
  await postToPath({
    port,
    path: "/mattermost/events",
    body: new URLSearchParams({
      token: input.webhookToken,
      channel_id: input.channelId,
      user_id: input.userId,
      post_id: input.postId,
      text: input.text,
    }).toString(),
  });
}

const mattermostTest = enabled ? test : test.skip;

mattermostTest("Mattermost websocket E2E receives a mention and posts a thread reply", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const adapter = new MattermostAdapter({
    port: 0,
    host: "127.0.0.1",
    defaultWorkingDirectory: process.cwd(),
    mattermostMode: "websocket",
    mattermostServerUrl: state.serverUrl,
    mattermostBotToken: state.bridgeUser.token,
  });

  const received: IncomingChatMessage[] = [];
  let replyPostId: string | undefined;

  await adapter.start(async (message) => {
    received.push(message);
    replyPostId = await adapter.postThreadMessage(
      message.thread,
      "Bridge Mattermost E2E OK",
    );
  });

  try {
    const created = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: state.channel.id,
          message: `@${state.bridgeUser.username} please handle this e2e check`,
        },
      },
    );

    const incoming = await waitFor(async () => received[0], 30_000, 500);
    assert.equal(incoming.thread.provider, "mattermost");
    assert.equal(incoming.thread.channelId, state.channel.id);
    assert.equal(incoming.thread.threadId, created.id);
    assert.match(incoming.text, /please handle this e2e check/);

    const reply = await waitFor(async () => {
      if (!replyPostId) {
        return undefined;
      }

      const posts = await apiRequest<MattermostPostList>(
        state.serverUrl,
        `/api/v4/channels/${state.channel.id}/posts?page=0&per_page=20`,
        state.memberUser.token,
      );
      const found = posts.posts[replyPostId];
      if (!found) {
        return undefined;
      }

      return found;
    }, 30_000, 1_000);

    assert.equal(reply.user_id, state.bridgeUser.id);
    assert.equal(reply.root_id, created.id);
    assert.equal(reply.message, "Bridge Mattermost E2E OK");
  } finally {
    await adapter.stop();
  }
});

mattermostTest("Mattermost websocket E2E accepts mentioned self-authored self-DM posts", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const directChannel = await apiRequest<MattermostChannel>(
    state.serverUrl,
    "/api/v4/channels/direct",
    state.memberUser.token,
    {
      method: "POST",
      body: [state.memberUser.id, state.memberUser.id],
    },
  );

  const adapter = new MattermostAdapter({
    port: 0,
    host: "127.0.0.1",
    defaultWorkingDirectory: process.cwd(),
    mattermostMode: "websocket",
    mattermostActorMode: "self",
    mattermostServerUrl: state.serverUrl,
    mattermostBotToken: state.memberUser.token,
  });

  const received: IncomingChatMessage[] = [];
  let replyPostId: string | undefined;

  await adapter.start(async (message) => {
    received.push(message);
    replyPostId = await adapter.postThreadMessage(
      message.thread,
      "Bridge Mattermost self-DM OK",
    );
  });

  try {
    const created = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: directChannel.id,
          message: `@${state.memberUser.username} note to self`,
        },
      },
    );

    const incoming = await waitFor(async () => received[0], 30_000, 500);
    assert.equal(incoming.thread.provider, "mattermost");
    assert.equal(incoming.thread.channelId, directChannel.id);
    assert.equal(incoming.thread.threadId, created.id);
    assert.equal(incoming.userId, state.memberUser.id);
    assert.equal(incoming.text, "note to self");

    const reply = await waitFor(async () => {
      if (!replyPostId) {
        return undefined;
      }

      const posts = await apiRequest<MattermostPostList>(
        state.serverUrl,
        `/api/v4/channels/${directChannel.id}/posts?page=0&per_page=20`,
        state.memberUser.token,
      );
      const found = posts.posts[replyPostId];
      if (!found) {
        return undefined;
      }

      return found;
    }, 30_000, 1_000);

    assert.equal(reply.user_id, state.memberUser.id);
    assert.equal(reply.root_id, created.id);
    assert.equal(reply.message, "Bridge Mattermost self-DM OK");
  } finally {
    await adapter.stop();
  }
});

mattermostTest("Mattermost websocket E2E ignores non-self channels in self actor mode", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const selfDirectChannel = await apiRequest<MattermostChannel>(
    state.serverUrl,
    "/api/v4/channels/direct",
    state.memberUser.token,
    {
      method: "POST",
      body: [state.memberUser.id, state.memberUser.id],
    },
  );
  const otherDirectChannel = await apiRequest<MattermostChannel>(
    state.serverUrl,
    "/api/v4/channels/direct",
    state.memberUser.token,
    {
      method: "POST",
      body: [state.memberUser.id, state.bridgeUser.id],
    },
  );

  const adapter = new MattermostAdapter({
    port: 0,
    host: "127.0.0.1",
    defaultWorkingDirectory: process.cwd(),
    mattermostMode: "websocket",
    mattermostActorMode: "self",
    mattermostServerUrl: state.serverUrl,
    mattermostBotToken: state.memberUser.token,
  });

  const received: IncomingChatMessage[] = [];
  let bridgeReplyPostId: string | undefined;

  await adapter.start(async (message) => {
    received.push(message);
    bridgeReplyPostId = await adapter.postThreadMessage(
      message.thread,
      "Bridge Mattermost unexpected self-mode reply",
    );
  });

  try {
    const publicRoot = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: state.channel.id,
          message: "public channel should be ignored",
        },
      },
    );

    const selfDmRoot = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: selfDirectChannel.id,
          message: "plain self DM should be ignored",
        },
      },
    );

    const otherDmRoot = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: otherDirectChannel.id,
          message: "other direct message should be ignored",
        },
      },
    );

    await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: state.channel.id,
          root_id: publicRoot.id,
          message: "public thread reply should also be ignored",
        },
      },
    );

    await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: selfDirectChannel.id,
          root_id: selfDmRoot.id,
          message: "plain self DM thread reply should also be ignored",
        },
      },
    );

    await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: otherDirectChannel.id,
          root_id: otherDmRoot.id,
          message: "other DM thread reply should also be ignored",
        },
      },
    );

    await new Promise((resolve) => setTimeout(resolve, 2_000));
    assert.equal(received.length, 0);
    assert.equal(bridgeReplyPostId, undefined);
  } finally {
    await adapter.stop();
  }
});

mattermostTest("Mattermost approval button E2E reruns the pending request", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const webhookToken = "mm-e2e-approval-hook";
  const agent = new ApprovalE2EAgent();
  const { chatAdapter } = await startMattermostBridge({
    state,
    webhookToken,
    agent,
  });

  try {
    const created = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: state.channel.id,
          message: "bridge approval root",
        },
      },
    );

    await dispatchMattermostWebhook({
      adapter: chatAdapter,
      webhookToken,
      channelId: state.channel.id,
      userId: state.memberUser.id,
      postId: created.id,
      text: "please edit files",
    });

    const approvalPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) =>
        post.root_id === created.id &&
        /Approval required/.test(post.message) &&
        Boolean(
          post.props?.attachments?.[0]?.actions?.some(
            (action) => action.name === "Approve and rerun",
          ),
        ),
    );
    const approveAction = approvalPost.props?.attachments?.[0]?.actions?.find(
      (action) => action.name === "Approve and rerun",
    );
    assert.ok(approveAction);

    const actionResponse = await postJsonToUrl(buildMattermostActionUrl(chatAdapter), {
      user_id: state.memberUser.id,
      post_id: approvalPost.id,
      channel_id: state.channel.id,
      context: {
        action: "approve",
        token: webhookToken,
      },
    });
    assert.match(actionResponse, /Approval received/);

    const completionPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && post.message === "Applied the requested changes.",
    );

    assert.equal(completionPost.user_id, state.bridgeUser.id);
    assert.equal(agent.requests.length, 2);
    assert.equal(agent.requests[1]?.approvalPolicy, "never");
  } finally {
    await chatAdapter.stop();
  }
});

mattermostTest("Mattermost cancel button E2E cancels the running request", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const webhookToken = "mm-e2e-cancel-hook";
  const agent = new CancelE2EAgent();
  const { chatAdapter } = await startMattermostBridge({
    state,
    webhookToken,
    agent,
  });

  try {
    const created = await apiRequest<MattermostPost>(
      state.serverUrl,
      "/api/v4/posts",
      state.memberUser.token,
      {
        method: "POST",
        body: {
          channel_id: state.channel.id,
          message: "bridge cancel root",
        },
      },
    );

    await dispatchMattermostWebhook({
      adapter: chatAdapter,
      webhookToken,
      channelId: state.channel.id,
      userId: state.memberUser.id,
      postId: created.id,
      text: "please run a long task",
    });

    const progressPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) =>
        post.root_id === created.id &&
        /State: Running/.test(post.message) &&
        Boolean(
          post.props?.attachments?.[0]?.actions?.some(
            (action) => action.name === "Cancel",
          ),
        ),
    );
    const cancelAction = progressPost.props?.attachments?.[0]?.actions?.find(
      (action) => action.name === "Cancel",
    );
    assert.ok(cancelAction);

    const actionResponse = await postJsonToUrl(buildMattermostActionUrl(chatAdapter), {
      user_id: state.memberUser.id,
      post_id: progressPost.id,
      channel_id: state.channel.id,
      context: {
        action: "cancel",
        token: webhookToken,
      },
    });
    assert.match(actionResponse, /Cancellation requested/);

    const cancelledPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) =>
        post.root_id === created.id &&
        /Execution cancelled for session/.test(post.message),
    );

    assert.equal(cancelledPost.user_id, state.bridgeUser.id);
    assert.equal(agent.cancelRequests.length, 1);
  } finally {
    await chatAdapter.stop();
  }
});

async function postToPath(input: { port: number; path: string; body: string }): Promise<string> {
  return await new Promise<string>((resolve, reject) => {
    const request = http.request(
      {
        agent: false,
        host: "127.0.0.1",
        port: input.port,
        path: input.path,
        method: "POST",
        headers: {
          connection: "close",
          "content-type": "application/x-www-form-urlencoded",
          "content-length": Buffer.byteLength(input.body).toString(),
        },
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk) => {
          chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
        });
        response.on("end", () => {
          resolve(Buffer.concat(chunks).toString("utf8"));
        });
      },
    );

    request.on("error", reject);
    request.end(input.body);
  });
}
