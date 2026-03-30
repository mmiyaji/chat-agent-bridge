import crypto from "node:crypto";
import http, { IncomingMessage, ServerResponse } from "node:http";
import https from "node:https";
import querystring from "node:querystring";
import { AuditLogger, NoopAuditLogger } from "../../core/audit.js";
import { AppConfig, ChatThreadRef, IncomingChatMessage } from "../../core/types.js";
import { ChatAction, ChatAdapter, ChatMessageHandler, ChatMessageOptions } from "../chat.js";

interface MattermostWebhookPayload {
  token?: string;
  team_id?: string;
  channel_id?: string;
  user_id?: string;
  post_id?: string;
  root_id?: string;
  text?: string;
  trigger_word?: string;
}

interface MattermostPost {
  id?: string;
  root_id?: string;
  channel_id?: string;
  message?: string;
  user_id?: string;
  props?: Record<string, unknown>;
}

interface MattermostPostResponse {
  id?: string;
  root_id?: string;
  channel_id?: string;
}

interface MattermostUser {
  id?: string;
  username?: string;
}

interface MattermostChannel {
  id?: string;
  type?: string;
}

interface MattermostWebSocketEvent {
  event?: string;
  status?: string;
  seq_reply?: number;
  data?: {
    post?: string;
  };
  broadcast?: {
    user_id?: string;
  };
}

interface MattermostActionRequest {
  user_id?: string;
  post_id?: string;
  channel_id?: string;
  team_id?: string;
  context?: {
    action?: string;
    token?: string;
  };
}

interface MattermostSocketLike {
  addEventListener(type: string, listener: (event: { data?: unknown }) => void): void;
  removeEventListener?(type: string, listener: (event: { data?: unknown }) => void): void;
  send(data: string): void;
  close(): void;
}

interface MattermostAdapterOptions {
  port: number;
  host: string;
  defaultWorkingDirectory: string;
  mode: "http" | "websocket";
  serverUrl?: string;
  botToken?: string;
  webhookToken?: string;
  actionBaseUrl?: string;
  dmOnly?: boolean;
}

function normalizeMattermostText(text: string, triggerWord?: string): string {
  const trimmed = text.trim();
  if (!triggerWord) {
    return trimmed;
  }

  const normalizedTrigger = triggerWord.trim();
  if (normalizedTrigger.length === 0) {
    return trimmed;
  }

  if (!trimmed.toLowerCase().startsWith(normalizedTrigger.toLowerCase())) {
    return trimmed;
  }

  return trimmed.slice(normalizedTrigger.length).trim();
}

function writeJson(response: ServerResponse, statusCode: number, body: unknown): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "application/json; charset=utf-8");
  response.end(JSON.stringify(body));
}

function writeText(response: ServerResponse, statusCode: number, body: string): void {
  response.statusCode = statusCode;
  response.setHeader("content-type", "text/plain; charset=utf-8");
  response.end(body);
}

async function readRawBody(request: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }

  return Buffer.concat(chunks);
}

async function fetchJson<T>(input: {
  url: string;
  method: string;
  headers: Record<string, string>;
  body?: string;
}): Promise<T> {
  return await new Promise<T>((resolve, reject) => {
    const url = new URL(input.url);
    const request = https.request(
      {
        protocol: url.protocol,
        hostname: url.hostname,
        port: url.port || undefined,
        path: `${url.pathname}${url.search}`,
        method: input.method,
        headers: input.headers,
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk) => {
          chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
        });
        response.on("end", () => {
          const body = Buffer.concat(chunks).toString("utf8");
          if ((response.statusCode ?? 500) >= 400) {
            reject(new Error(`Mattermost API HTTP error: ${response.statusCode ?? 500}`));
            return;
          }

          resolve(JSON.parse(body || "{}") as T);
        });
      },
    );

    request.on("error", reject);
    request.end(input.body ?? "");
  });
}

export class MattermostAdapter implements ChatAdapter {
  private readonly options: MattermostAdapterOptions;
  private readonly mattermostApiFetch: typeof fetchJson;
  private readonly socketFactory: (url: string) => MattermostSocketLike;
  private readonly auditLogger: AuditLogger;
  private handler?: ChatMessageHandler;
  private server: http.Server | undefined;
  private socket: MattermostSocketLike | undefined;
  private socketReconnectTimer: NodeJS.Timeout | undefined;
  private socketSeq = 1;
  private selfUserId: string | undefined;
  private selfUsername: string | undefined;
  private stopped = false;
  private readonly channelTypeCache = new Map<string, string>();
  private readonly actionToken = crypto.randomUUID();

  constructor(
    config: Pick<
      AppConfig,
      | "port"
      | "host"
      | "defaultWorkingDirectory"
      | "mattermostMode"
      | "mattermostServerUrl"
      | "mattermostBotToken"
      | "mattermostWebhookToken"
      | "mattermostActionBaseUrl"
      | "mattermostDmOnly"
    >,
    auditLogger?: AuditLogger,
    dependencies?: {
      mattermostApiFetch?: typeof fetchJson;
      socketFactory?: (url: string) => MattermostSocketLike;
    },
  ) {
    this.options = {
      port: config.port,
      host: config.host,
      defaultWorkingDirectory: config.defaultWorkingDirectory,
      mode: config.mattermostMode,
      ...(config.mattermostDmOnly
        ? {
            dmOnly: config.mattermostDmOnly,
          }
        : {}),
      ...(config.mattermostServerUrl
        ? {
            serverUrl: config.mattermostServerUrl,
          }
        : {}),
      ...(config.mattermostBotToken
        ? {
            botToken: config.mattermostBotToken,
          }
        : {}),
      ...(config.mattermostActionBaseUrl
        ? {
            actionBaseUrl: config.mattermostActionBaseUrl,
          }
        : {}),
      ...(config.mattermostWebhookToken
        ? {
            webhookToken: config.mattermostWebhookToken,
          }
        : {}),
    };
    this.mattermostApiFetch = dependencies?.mattermostApiFetch ?? fetchJson;
    this.socketFactory =
      dependencies?.socketFactory ??
      ((url) => {
        const WebSocketCtor = (globalThis as { WebSocket?: new (url: string) => MattermostSocketLike })
          .WebSocket;
        if (!WebSocketCtor) {
          throw new Error("Global WebSocket is not available in this Node runtime.");
        }

        return new WebSocketCtor(url);
      });
    this.auditLogger = auditLogger ?? new NoopAuditLogger();
  }

  async start(handler: ChatMessageHandler): Promise<void> {
    this.handler = handler;
    this.stopped = false;
    this.server = http.createServer(async (request, response) => {
      await this.handleHttpRequest(request, response);
    });

    await new Promise<void>((resolve, reject) => {
      this.server?.once("error", reject);
      this.server?.listen(this.options.port, this.options.host, () => resolve());
    });

    console.log(
      `Mattermost adapter listening on http://${this.options.host}:${this.getListeningPort() ?? this.options.port}/mattermost/events`,
    );

    if (this.options.mode === "websocket") {
      await this.startWebSocketMode();
      return;
    }
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.socketReconnectTimer) {
      clearTimeout(this.socketReconnectTimer);
      this.socketReconnectTimer = undefined;
    }

    if (this.socket) {
      this.socket.close();
      this.socket = undefined;
    }

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

  async startNewThread(
    channelId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<ChatThreadRef | undefined> {
    const post = await this.createPost({
      channelId,
      text,
      ...(options
        ? {
            options,
          }
        : {}),
    });
    if (!post.id) {
      return undefined;
    }

    return {
      provider: "mattermost",
      channelId,
      threadId: post.id,
    };
  }

  async postThreadMessage(
    thread: ChatThreadRef,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<string | undefined> {
    const post = await this.createPost({
      channelId: thread.channelId,
      text,
      rootId: thread.threadId,
      ...(options
        ? {
            options,
          }
        : {}),
    });
    return post.id;
  }

  async updateThreadMessage(
    _thread: ChatThreadRef,
    messageId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<void> {
    if (!this.options.serverUrl || !this.options.botToken) {
      throw new Error("Mattermost update requires MATTERMOST_SERVER_URL and MATTERMOST_BOT_TOKEN.");
    }

    await this.mattermostApiFetch<MattermostPostResponse>({
      url: new URL(`/api/v4/posts/${messageId}/patch`, this.options.serverUrl).toString(),
      method: "PUT",
      headers: {
        authorization: `Bearer ${this.options.botToken}`,
        "content-type": "application/json; charset=utf-8",
      },
      body: JSON.stringify({
        message: text,
        props: this.buildPostProps(text, options),
      }),
    });
  }

  private async handleHttpRequest(
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> {
    if (request.method === "GET" && request.url === "/healthz") {
      writeJson(response, 200, { ok: true });
      return;
    }

    if (request.method !== "POST") {
      writeJson(response, 404, { error: "Not found" });
      return;
    }

    try {
      const rawBody = await readRawBody(request);

      if (request.url === "/mattermost/actions") {
        await this.handleActionRequest(rawBody, response);
        return;
      }

      const payload = querystring.parse(rawBody.toString("utf8")) as Record<string, string>;
      if (this.options.webhookToken && payload.token !== this.options.webhookToken) {
        await this.auditLogger.log({
          type: "mattermost.request.rejected",
          reason: "invalid-token",
        });
        writeJson(response, 401, { error: "Invalid Mattermost token" });
        return;
      }

      if (request.url === "/mattermost/events") {
        writeJson(response, 200, { ok: true });
        await this.processIncomingPayload(payload, "webhook");
        return;
      }

      if (request.url === "/mattermost/commands") {
        writeText(response, 200, "");
        await this.processIncomingPayload(payload, "command");
        return;
      }

      writeJson(response, 404, { error: "Not found" });
    } catch (error) {
      const text = error instanceof Error ? error.message : "Invalid request.";
      await this.auditLogger.log({
        type: "mattermost.request.failed",
        error: text,
      });
      writeJson(response, 400, { error: text });
    }
  }

  private async handleActionRequest(rawBody: Buffer, response: ServerResponse): Promise<void> {
    const payload = JSON.parse(rawBody.toString("utf8")) as MattermostActionRequest;
    const action = payload.context?.action;
    const token = payload.context?.token;
    if (
      this.options.webhookToken &&
      (token !== this.options.webhookToken && token !== this.actionToken)
    ) {
      await this.auditLogger.log({
        type: "mattermost.action.rejected",
        reason: "invalid-token",
        action: action ?? "",
      });
      writeJson(response, 401, {
        error: {
          message: "Invalid Mattermost action token.",
        },
      });
      return;
    }

    if (!action || !payload.channel_id || !payload.user_id || !payload.post_id) {
      writeJson(response, 400, {
        error: {
          message: "Invalid Mattermost action payload.",
        },
      });
      return;
    }

    const command =
      action === "approve" || action === "reject" || action === "cancel" ? action : undefined;
    if (!command) {
      writeJson(response, 400, {
        error: {
          message: "Unsupported Mattermost action.",
        },
      });
      return;
    }

    const post = await this.readPost(payload.post_id);
    const threadId = post?.root_id || post?.id || payload.post_id;
    await this.auditLogger.log({
      type: "mattermost.action.received",
      action: command,
      channelId: payload.channel_id,
      threadId,
      userId: payload.user_id,
    });

    if (this.handler) {
      queueMicrotask(() => {
        void this.handler?.({
          thread: {
            provider: "mattermost",
            channelId: payload.channel_id ?? "",
            threadId,
          },
          userId: payload.user_id ?? "",
          text: command,
          requestedWorkingDirectory: this.options.defaultWorkingDirectory,
        }).catch((error: unknown) => {
          console.error("Mattermost action handler failed.", error);
        });
      });
    }

    writeJson(response, 200, {
      ephemeral_text:
        command === "cancel"
          ? "Cancellation requested."
          : command === "approve"
            ? "Approval received. Re-running."
            : "Pending approval cleared.",
    });
  }

  private async processIncomingPayload(
    payload: Record<string, string>,
    mode: "webhook" | "command",
  ): Promise<void> {
    if (!(await this.isAllowedChannel(payload.channel_id))) {
      await this.auditLogger.log({
        type: "mattermost.event.filtered",
        mode,
        channelId: payload.channel_id ?? "",
        reason: "channel-type",
      });
      return;
    }

    const message = await this.toIncomingChatMessage(payload, mode);
    await this.auditLogger.log({
      type: "mattermost.event.received",
      mode,
      channelId: message?.thread.channelId ?? payload.channel_id ?? "",
      threadId: message?.thread.threadId ?? payload.post_id ?? "",
      userId: message?.userId ?? payload.user_id ?? "",
      accepted: Boolean(message),
      text: message?.text ?? payload.text ?? "",
    });

    if (message && this.handler) {
      queueMicrotask(() => {
        void this.handler?.(message).catch((error: unknown) => {
          console.error("Mattermost handler failed.", error);
        });
      });
    }
  }

  private async toIncomingChatMessage(
    payload: Record<string, string>,
    mode: "webhook" | "command",
  ): Promise<IncomingChatMessage | undefined> {
    if (!payload.channel_id || !payload.user_id || !payload.text) {
      return undefined;
    }

    const post = payload.post_id ? await this.readPost(payload.post_id) : undefined;
    if (post?.props?.chat_agent_bridge === "1") {
      return undefined;
    }

    const threadId =
      payload.root_id ||
      post?.root_id ||
      post?.id ||
      payload.post_id ||
      (mode === "command" ? `command:${payload.channel_id}:${payload.user_id}` : undefined);
    if (!threadId) {
      return undefined;
    }

    return {
      thread: {
        provider: "mattermost",
        channelId: payload.channel_id,
        threadId,
      },
      userId: payload.user_id,
      text: normalizeMattermostText(payload.text, payload.trigger_word),
      requestedWorkingDirectory: this.options.defaultWorkingDirectory,
    };
  }

  private async startWebSocketMode(): Promise<void> {
    if (!this.options.serverUrl || !this.options.botToken) {
      throw new Error(
        "Mattermost websocket mode requires MATTERMOST_SERVER_URL and MATTERMOST_BOT_TOKEN.",
      );
    }

    const currentUser = await this.readCurrentUser();
    this.selfUserId = currentUser?.id;
    this.selfUsername = currentUser?.username;
    const websocketUrl = new URL("/api/v4/websocket", this.options.serverUrl);
    websocketUrl.protocol = websocketUrl.protocol === "https:" ? "wss:" : "ws:";
    const socket = this.socketFactory(websocketUrl.toString());
    this.socket = socket;
    let authenticated = false;

    socket.addEventListener("open", () => {
      socket.send(
        JSON.stringify({
          seq: this.socketSeq,
          action: "authentication_challenge",
          data: {
            token: this.options.botToken,
          },
        }),
      );
      this.socketSeq += 1;
    });

    socket.addEventListener("message", (event) => {
      const raw = typeof event.data === "string" ? event.data : String(event.data ?? "");
      void this.handleSocketMessage(raw, {
        onAuthenticated: () => {
          authenticated = true;
        },
      }).catch((error: unknown) => {
        console.error("Mattermost websocket handler failed.", error);
      });
    });

    socket.addEventListener("close", () => {
      this.socket = undefined;
      if (this.stopped) {
        return;
      }

      if (!authenticated) {
        console.error("Mattermost websocket closed before authentication completed.");
      }
      this.scheduleSocketReconnect();
    });

    console.log("Mattermost adapter connected via WebSocket.");
  }

  private scheduleSocketReconnect(): void {
    if (this.socketReconnectTimer || this.stopped) {
      return;
    }

    this.socketReconnectTimer = setTimeout(() => {
      this.socketReconnectTimer = undefined;
      void this.startWebSocketMode().catch((error: unknown) => {
        console.error("Mattermost websocket reconnect failed.", error);
        this.scheduleSocketReconnect();
      });
    }, 1000);
  }

  private async handleSocketMessage(
    rawMessage: string,
    input: {
      onAuthenticated: () => void;
    },
  ): Promise<void> {
    const event = JSON.parse(rawMessage) as MattermostWebSocketEvent;
    if (event.status === "OK" && event.seq_reply === 1) {
      input.onAuthenticated();
      await this.auditLogger.log({
        type: "mattermost.websocket.authenticated",
      });
      return;
    }

    if (event.event !== "posted" || !event.data?.post) {
      return;
    }

    const post = JSON.parse(event.data.post) as MattermostPost;
    if (!post.id || !post.channel_id || !post.message || !post.user_id) {
      return;
    }

    if (post.props?.chat_agent_bridge === "1") {
      return;
    }

    if (!(await this.isAllowedChannel(post.channel_id))) {
      return;
    }

    if (!this.options.dmOnly && this.selfUserId && post.user_id === this.selfUserId) {
      return;
    }

    const mention = this.selfUsername ? `@${this.selfUsername}` : undefined;
    const isThreadReply = Boolean(post.root_id) && post.root_id !== post.id;
    if (
      !isThreadReply &&
      (!mention || !post.message.trim().toLowerCase().startsWith(mention.toLowerCase()))
    ) {
      return;
    }

    const incomingMessage: IncomingChatMessage = {
      thread: {
        provider: "mattermost",
        channelId: post.channel_id,
        threadId: post.root_id || post.id,
      },
      userId: post.user_id,
      text: normalizeMattermostText(post.message, mention),
      requestedWorkingDirectory: this.options.defaultWorkingDirectory,
    };

    await this.auditLogger.log({
      type: "mattermost.websocket.event.received",
      channelId: incomingMessage.thread.channelId,
      threadId: incomingMessage.thread.threadId,
      userId: incomingMessage.userId,
      accepted: true,
      text: incomingMessage.text,
    });

    if (this.handler) {
      queueMicrotask(() => {
        void this.handler?.(incomingMessage).catch((error: unknown) => {
          console.error("Mattermost websocket handler failed.", error);
        });
      });
    }
  }

  private async readPost(postId: string): Promise<MattermostPost | undefined> {
    if (!this.options.serverUrl || !this.options.botToken) {
      return undefined;
    }

    return await this.mattermostApiFetch<MattermostPost>({
      url: new URL(`/api/v4/posts/${postId}`, this.options.serverUrl).toString(),
      method: "GET",
      headers: {
        authorization: `Bearer ${this.options.botToken}`,
      },
    });
  }

  private async readCurrentUser(): Promise<MattermostUser | undefined> {
    if (!this.options.serverUrl || !this.options.botToken) {
      return undefined;
    }

    return await this.mattermostApiFetch<MattermostUser>({
      url: new URL("/api/v4/users/me", this.options.serverUrl).toString(),
      method: "GET",
      headers: {
        authorization: `Bearer ${this.options.botToken}`,
      },
    });
  }

  private async readChannel(channelId: string): Promise<MattermostChannel | undefined> {
    if (!this.options.serverUrl || !this.options.botToken) {
      return undefined;
    }

    return await this.mattermostApiFetch<MattermostChannel>({
      url: new URL(`/api/v4/channels/${channelId}`, this.options.serverUrl).toString(),
      method: "GET",
      headers: {
        authorization: `Bearer ${this.options.botToken}`,
      },
    });
  }

  private async isAllowedChannel(channelId: string | undefined): Promise<boolean> {
    if (!channelId) {
      return false;
    }

    if (!this.options.dmOnly) {
      return true;
    }

    const cached = this.channelTypeCache.get(channelId);
    if (cached) {
      return cached === "D";
    }

    const channel = await this.readChannel(channelId);
    if (!channel?.type) {
      return false;
    }

    this.channelTypeCache.set(channelId, channel.type);
    return channel.type === "D";
  }

  private async createPost(input: {
    channelId: string;
    text: string;
    rootId?: string;
    options?: ChatMessageOptions;
  }): Promise<MattermostPostResponse> {
    if (!this.options.serverUrl || !this.options.botToken) {
      throw new Error("Mattermost posting requires MATTERMOST_SERVER_URL and MATTERMOST_BOT_TOKEN.");
    }

    return await this.mattermostApiFetch<MattermostPostResponse>({
      url: new URL("/api/v4/posts", this.options.serverUrl).toString(),
      method: "POST",
      headers: {
        authorization: `Bearer ${this.options.botToken}`,
        "content-type": "application/json; charset=utf-8",
      },
      body: JSON.stringify({
        channel_id: input.channelId,
        message: input.text,
        props: this.buildPostProps(input.text, input.options),
        ...(input.rootId
          ? {
              root_id: input.rootId,
            }
          : {}),
      }),
    });
  }

  private buildPostProps(text: string, options?: ChatMessageOptions): Record<string, unknown> {
    const props: Record<string, unknown> = {
      chat_agent_bridge: "1",
    };
    const attachments = this.buildAttachments(text, options?.actions);
    if (attachments.length > 0) {
      props.attachments = attachments;
    }
    return props;
  }

  private buildAttachments(
    text: string,
    actions?: ChatAction[],
  ): Array<Record<string, unknown>> {
    const actionUrl = this.resolveActionUrl();
    if (!actionUrl || !actions || actions.length === 0) {
      return [];
    }

    return [
      {
        text,
        actions: actions.map((action) => ({
          id: action.id.replace(/[^a-zA-Z0-9]/g, ""),
          type: "button",
          name: action.label,
          style: this.mapActionStyle(action.style),
          integration: {
            url: actionUrl,
            context: {
              action: action.value ?? action.id,
              token: this.options.webhookToken ?? this.actionToken,
            },
          },
        })),
      },
    ];
  }

  private resolveActionUrl(): string | undefined {
    if (this.options.actionBaseUrl) {
      return new URL("/mattermost/actions", this.options.actionBaseUrl).toString();
    }

    if (!this.options.host || this.options.host === "0.0.0.0" || this.options.host === "::") {
      return undefined;
    }

    const port = this.getListeningPort() ?? this.options.port;
    return `http://${this.options.host}:${port}/mattermost/actions`;
  }

  private mapActionStyle(style: ChatAction["style"]): string {
    switch (style) {
      case "primary":
        return "primary";
      case "danger":
        return "danger";
      default:
        return "default";
    }
  }
}
