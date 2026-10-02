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
  overrides?: Partial<AppConfig>,
): AppConfig {
  return {
    chatProvider: "mattermost",
    slackAllowedUserIds: [],
    mattermostAllowedUserIds: [state.memberUser.id],
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
    ...overrides,
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

class NativeRequestE2EAgent implements Agent {
  readonly kind = "codex" as const;
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: false,
    networkAccessControl: false,
  } as const;
  readonly requests: AgentExecutionRequest[] = [];
  approvalDecision?: "accept" | "decline";
  inputAnswers?: Record<string, string[]>;

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    this.requests.push(request);
    if (!request.onRequest) {
      throw new Error("Bridge did not supply the native request callback.");
    }

    const approval = await request.onRequest({
      id: "native-approval-e2e",
      kind: "approval",
      title: "Approve a workspace command",
      detail: "The agent needs permission to run its planned command.",
    });
    this.approvalDecision = approval.decision;
    if (approval.decision !== "accept") {
      return { sessionId: request.session.sessionId, summary: "Native approval was declined." };
    }

    const input = await request.onRequest({
      id: "native-input-e2e",
      kind: "input",
      title: "Provide run details",
      detail: "Answer both questions so the agent can continue.",
      questions: [
        { id: "workspace", question: "Which workspace?", options: ["client", "server"] },
        { id: "reason", question: "What is the goal?" },
      ],
    });
    this.inputAnswers = input.answers;
    return {
      sessionId: request.session.sessionId,
      summary: `Native requests completed: ${input.answers?.workspace?.[0] ?? ""} / ${input.answers?.reason?.[0] ?? ""}.`,
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

// Generic fake agent for the workdir/alias/resume/channel-default flow scenarios,
// which only need to observe what prompt and session state reached the agent.
class RecordingE2EAgent implements Agent {
  readonly kind: Agent["kind"];
  readonly capabilities = {
    manualApproval: true,
    writeApprovalRerun: true,
    networkAccessControl: false,
  } as const;
  readonly requests: AgentExecutionRequest[] = [];

  constructor(kind: Agent["kind"] = "codex") {
    this.kind = kind;
  }

  async execute(request: AgentExecutionRequest): Promise<AgentExecutionResult> {
    this.requests.push(request);
    return {
      sessionId: request.session.sessionId,
      summary: "done",
    };
  }

  async cancelExecution(): Promise<boolean> {
    return false;
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
  configOverrides?: Partial<AppConfig>;
}) {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-bridge-"));
  const config = createMattermostBridgeConfig(
    input.state,
    input.webhookToken,
    tempRoot,
    input.configOverrides,
  );
  const chatAdapter = new MattermostAdapter(config);
  const stateStore = new InMemoryStateStore();
  const bridge = new BridgeService({
    config,
    stateStore,
    sessionManager: new SessionManager(stateStore),
    jobQueue: new InlineJobQueue(),
    chatAdapter,
    agents: {
      // Register under whichever agent kind this scenario configures so
      // agentKind: "claude-code" scenarios route to the fake agent too.
      [input.agent.kind]: input.agent,
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

mattermostTest("Mattermost websocket E2E resolves native approval and multi-question input in one run", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const agent = new NativeRequestE2EAgent();
  const { bridge, chatAdapter } = await startMattermostBridge({
    state,
    webhookToken: "mm-e2e-native-request-hook",
    agent,
    configOverrides: {
      mattermostMode: "websocket",
      mattermostActorMode: "bot",
    },
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
          message: `@${state.bridgeUser.username} run the native request integration`,
        },
      },
    );

    await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && /Approve a workspace command/.test(post.message),
    );

    // These actual Mattermost replies exercise the websocket adapter's thread
    // reply route and its optional leading bot-mention normalization.
    await apiRequest<MattermostPost>(state.serverUrl, "/api/v4/posts", state.memberUser.token, {
      method: "POST",
      body: {
        channel_id: state.channel.id,
        root_id: created.id,
        message: `@${state.bridgeUser.username} approve native-approval-e2e`,
      },
    });

    await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && /Provide run details/.test(post.message),
    );

    await apiRequest<MattermostPost>(state.serverUrl, "/api/v4/posts", state.memberUser.token, {
      method: "POST",
      body: {
        channel_id: state.channel.id,
        root_id: created.id,
        message: `@${state.bridgeUser.username} answer native-input-e2e workspace client`,
      },
    });
    await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && /Answer received for `workspace`/.test(post.message),
    );

    await apiRequest<MattermostPost>(state.serverUrl, "/api/v4/posts", state.memberUser.token, {
      method: "POST",
      body: {
        channel_id: state.channel.id,
        root_id: created.id,
        message: `@${state.bridgeUser.username} answer native-input-e2e reason validate changes`,
      },
    });

    const completion = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && post.message === "Native requests completed: client / validate changes.",
    );
    assert.equal(completion.user_id, state.bridgeUser.id);
    assert.equal(agent.requests.length, 1, "approval and user input continue the same agent execution");
    assert.equal(agent.approvalDecision, "accept");
    assert.deepEqual(agent.inputAnswers, {
      workspace: ["client"],
      reason: ["validate changes"],
    });
  } finally {
    await bridge.stop();
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

async function seedClaudeProjectSessions(
  workingDirectory: string,
  entries: Array<{ id: string; summary: string; mtimeSeconds: number }>,
): Promise<string> {
  const { encodeClaudeProjectDir } = await import("../src/agents/claude/project-sessions.js");
  const projectsRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-e2e-projects-"));
  const projectFolder = path.join(projectsRoot, encodeClaudeProjectDir(workingDirectory));
  await fs.mkdir(projectFolder, { recursive: true });
  for (const entry of entries) {
    const file = path.join(projectFolder, `${entry.id}.jsonl`);
    await fs.writeFile(file, JSON.stringify({ type: "summary", summary: entry.summary }), "utf8");
    await fs.utimes(file, entry.mtimeSeconds, entry.mtimeSeconds);
  }

  return projectsRoot;
}

mattermostTest("Mattermost workdir picker E2E offers aliases and binds the selection", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const webhookToken = "mm-e2e-workdir-picker-hook";
  const repoRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-e2e-repo-"));
  const aliasDir = path.join(repoRoot, "alias-project");
  await fs.mkdir(aliasDir, { recursive: true });
  const agent = new RecordingE2EAgent("codex");
  const { chatAdapter } = await startMattermostBridge({
    state,
    webhookToken,
    agent,
    configOverrides: {
      allowedDirectories: [repoRoot],
      repoAliases: { project: aliasDir },
      defaultWorkingDirectory: repoRoot,
    },
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
          message: "bridge workdir picker root",
        },
      },
    );

    await dispatchMattermostWebhook({
      adapter: chatAdapter,
      webhookToken,
      channelId: state.channel.id,
      userId: state.memberUser.id,
      postId: created.id,
      text: "please build the feature",
    });

    const pickerPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) =>
        post.root_id === created.id &&
        /Choose a working directory for this thread/.test(post.message),
    );
    assert.match(pickerPost.message, /1\. `project`/);

    await dispatchMattermostWebhook({
      adapter: chatAdapter,
      webhookToken,
      channelId: state.channel.id,
      userId: state.memberUser.id,
      postId: created.id,
      text: "1",
    });

    const boundPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && post.message === "done",
    );
    assert.equal(boundPost.user_id, state.bridgeUser.id);
    assert.equal(agent.requests.length, 1);
    assert.equal(agent.requests[0]?.prompt, "please build the feature");
    assert.equal(
      path.resolve(agent.requests[0]?.session.workingDirectory ?? ""),
      path.resolve(aliasDir),
    );
  } finally {
    await chatAdapter.stop();
  }
});

mattermostTest("Mattermost inline directory token E2E binds without a picker", async () => {
  const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
  const webhookToken = "mm-e2e-inline-alias-hook";
  const repoRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-e2e-inline-"));
  const aliasDir = path.join(repoRoot, "alias-inline");
  await fs.mkdir(aliasDir, { recursive: true });
  const agent = new RecordingE2EAgent("codex");
  const { chatAdapter } = await startMattermostBridge({
    state,
    webhookToken,
    agent,
    configOverrides: {
      allowedDirectories: [repoRoot],
      repoAliases: { inline: aliasDir },
      defaultWorkingDirectory: repoRoot,
    },
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
          message: "bridge inline alias root",
        },
      },
    );

    await dispatchMattermostWebhook({
      adapter: chatAdapter,
      webhookToken,
      channelId: state.channel.id,
      userId: state.memberUser.id,
      postId: created.id,
      text: "inline fix the thing",
    });

    const boundPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && /is now bound to/.test(post.message),
    );
    assert.match(boundPost.message, new RegExp(escapeRegExp(aliasDir)));

    const completionPost = await waitForThreadPost(
      state,
      state.channel.id,
      state.memberUser.token,
      (post) => post.root_id === created.id && post.message === "done",
    );
    assert.equal(completionPost.user_id, state.bridgeUser.id);
    assert.equal(agent.requests.length, 1);
    assert.equal(agent.requests[0]?.prompt, "fix the thing");
    assert.equal(
      path.resolve(agent.requests[0]?.session.workingDirectory ?? ""),
      path.resolve(aliasDir),
    );

    // No picker should ever have been posted for this thread.
    const posts = await fetchRecentPosts(state, state.channel.id, state.memberUser.token);
    assert.equal(
      posts.some(
        (post) =>
          post.root_id === created.id &&
          /Choose a working directory/.test(post.message),
      ),
      false,
    );
  } finally {
    await chatAdapter.stop();
  }
});

mattermostTest(
  "Mattermost Claude per-folder resume picker E2E attaches the fixture session",
  async () => {
    const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
    const webhookToken = "mm-e2e-resume-picker-hook";
    const repoRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-e2e-resume-"));
    const projectDir = path.join(repoRoot, "resume-project");
    await fs.mkdir(projectDir, { recursive: true });
    const fixtureSessionId = "cccc3333-cccc-cccc-cccc-cccccccccccc";
    const claudeProjectsDir = await seedClaudeProjectSessions(projectDir, [
      { id: fixtureSessionId, summary: "fix login", mtimeSeconds: 2000 },
    ]);
    const agent = new RecordingE2EAgent("claude-code");
    const { chatAdapter } = await startMattermostBridge({
      state,
      webhookToken,
      agent,
      configOverrides: {
        agentKind: "claude-code",
        allowedDirectories: [repoRoot],
        repoAliases: { resume: projectDir },
        defaultWorkingDirectory: repoRoot,
        claudeProjectsDir,
      },
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
            message: "bridge resume picker root",
          },
        },
      );

      // Bind the directory first via the inline alias token (no picker, since
      // repoAliases has a single entry and this exercises the flow (b)
      // directory-confirm path before the Claude resume picker fires).
      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: created.id,
        text: "resume fix the login bug",
      });

      const resumePickerPost = await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) =>
          post.root_id === created.id &&
          /has previous Claude Code sessions/.test(post.message),
      );
      assert.match(resumePickerPost.message, new RegExp(fixtureSessionId));
      assert.match(resumePickerPost.message, /fix login/);
      assert.equal(agent.requests.length, 0);

      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: created.id,
        text: "1",
      });

      const completionPost = await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) => post.root_id === created.id && post.message === "done",
      );
      assert.equal(completionPost.user_id, state.bridgeUser.id);
      assert.equal(agent.requests.length, 1);
      assert.equal(agent.requests[0]?.prompt, "fix the login bug");
      assert.equal(
        agent.requests[0]?.session.agentState?.claudeSessionId,
        fixtureSessionId,
      );
      assert.equal(
        path.resolve(agent.requests[0]?.session.workingDirectory ?? ""),
        path.resolve(projectDir),
      );
    } finally {
      await chatAdapter.stop();
    }
  },
);

mattermostTest(
  "Mattermost channel-default E2E skips the picker for new threads until cleared",
  async () => {
    const state = JSON.parse(await fs.readFile(stateFile, "utf8")) as BootstrapState;
    const webhookToken = "mm-e2e-channel-default-hook";
    const repoRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-mm-e2e-channel-default-"));
    const defaultDir = path.join(repoRoot, "default-project");
    const otherDir = path.join(repoRoot, "other-project");
    await fs.mkdir(defaultDir, { recursive: true });
    await fs.mkdir(otherDir, { recursive: true });
    const agent = new RecordingE2EAgent("codex");
    const { chatAdapter } = await startMattermostBridge({
      state,
      webhookToken,
      agent,
      configOverrides: {
        allowedDirectories: [repoRoot],
        repoAliases: { deflt: defaultDir, other: otherDir },
        defaultWorkingDirectory: repoRoot,
      },
    });

    try {
      // Set the channel default from a setup thread.
      const setupRoot = await apiRequest<MattermostPost>(
        state.serverUrl,
        "/api/v4/posts",
        state.memberUser.token,
        {
          method: "POST",
          body: {
            channel_id: state.channel.id,
            message: "bridge channel-default setup root",
          },
        },
      );
      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: setupRoot.id,
        text: "channel-default deflt",
      });
      await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) =>
          post.root_id === setupRoot.id &&
          /Channel default working directory set to/.test(post.message),
      );

      // A brand-new thread in the same channel should skip the picker and run
      // immediately in the channel default directory.
      const newThreadRoot = await apiRequest<MattermostPost>(
        state.serverUrl,
        "/api/v4/posts",
        state.memberUser.token,
        {
          method: "POST",
          body: {
            channel_id: state.channel.id,
            message: "bridge channel-default new thread root",
          },
        },
      );
      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: newThreadRoot.id,
        text: "build the feature",
      });

      const completionPost = await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) => post.root_id === newThreadRoot.id && post.message === "done",
      );
      assert.equal(completionPost.user_id, state.bridgeUser.id);
      assert.equal(agent.requests.length, 1);
      assert.equal(
        path.resolve(agent.requests[0]?.session.workingDirectory ?? ""),
        path.resolve(defaultDir),
      );

      const postsAfterDefault = await fetchRecentPosts(state, state.channel.id, state.memberUser.token);
      assert.equal(
        postsAfterDefault.some(
          (post) =>
            post.root_id === newThreadRoot.id &&
            /Choose a working directory/.test(post.message),
        ),
        false,
      );

      // Clear the channel default; a subsequent new thread should see the
      // picker again.
      const clearRoot = await apiRequest<MattermostPost>(
        state.serverUrl,
        "/api/v4/posts",
        state.memberUser.token,
        {
          method: "POST",
          body: {
            channel_id: state.channel.id,
            message: "bridge channel-default clear root",
          },
        },
      );
      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: clearRoot.id,
        text: "channel-default clear",
      });
      await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) =>
          post.root_id === clearRoot.id &&
          /Channel default working directory cleared/.test(post.message),
      );

      const afterClearRoot = await apiRequest<MattermostPost>(
        state.serverUrl,
        "/api/v4/posts",
        state.memberUser.token,
        {
          method: "POST",
          body: {
            channel_id: state.channel.id,
            message: "bridge channel-default after-clear root",
          },
        },
      );
      await dispatchMattermostWebhook({
        adapter: chatAdapter,
        webhookToken,
        channelId: state.channel.id,
        userId: state.memberUser.id,
        postId: afterClearRoot.id,
        text: "do more work",
      });

      const pickerAgain = await waitForThreadPost(
        state,
        state.channel.id,
        state.memberUser.token,
        (post) =>
          post.root_id === afterClearRoot.id &&
          /Choose a working directory for this thread/.test(post.message),
      );
      assert.ok(pickerAgain);
      // Still only the one completed request from before the clear.
      assert.equal(agent.requests.length, 1);
    } finally {
      await chatAdapter.stop();
    }
  },
);

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

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
