import crypto from "node:crypto";
import querystring from "node:querystring";
import http, { IncomingMessage, ServerResponse } from "node:http";
import https from "node:https";
import net from "node:net";
import tls from "node:tls";
import { AuditLogger, NoopAuditLogger } from "../../core/audit.js";
import { AppConfig, ChatThreadRef, IncomingChatMessage, SlackMode } from "../../core/types.js";
import {
  ChatAdapter,
  ChatConnectionStatus,
  ChatMessageHandler,
  ChatMessageOptions,
} from "../chat.js";

interface SlackEnvelope {
  type?: string;
  challenge?: string;
  event_id?: string;
  event?: {
    type?: string;
    subtype?: string;
    bot_id?: string;
    text?: string;
    user?: string;
    channel?: string;
    ts?: string;
    thread_ts?: string;
    channel_type?: string;
    hidden?: boolean;
    message?: {
      type?: string;
      user?: string;
      text?: string;
      ts?: string;
      thread_ts?: string;
    };
  };
  user?: {
    id?: string;
  };
  channel?: {
    id?: string;
  };
  message?: {
    thread_ts?: string;
    ts?: string;
  };
  actions?: Array<{
    action_id?: string;
    value?: string;
  }>;
}

interface SlackSocketEnvelope {
  envelope_id?: string;
  type?: string;
  payload?: SlackEnvelope;
  reason?: string;
}

interface SlackSocketOpenResponse {
  ok?: boolean;
  error?: string;
  url?: string;
}

interface SlackMessageResponse {
  ok?: boolean;
  error?: string;
  ts?: string;
}

interface SlackSocketConnection {
  close(): Promise<void>;
  setMessageHandler(handler: (message: string) => void): void;
  setCloseHandler(handler: () => void): void;
  sendText(text: string): Promise<void>;
}

interface SlackSocketConnector {
  open(url: string): Promise<SlackSocketConnection>;
}

export interface SlackAdapterOptions {
  mode: SlackMode;
  port: number;
  host: string;
  defaultWorkingDirectory: string;
  signingSecret?: string;
  botToken?: string;
  appToken?: string;
}

function normalizeSlackText(text: string): string {
  return text.replace(/^<@[A-Z0-9]+>\s*/i, "").trim();
}

function buildApprovalBlocks(): unknown[] {
  return [
    {
      type: "section",
      text: {
        type: "mrkdwn",
        text:
          "*Approval required*\nThis request needs write access. Use the buttons below to approve or reject it.",
      },
    },
    {
      type: "actions",
      elements: [
        {
          type: "button",
          action_id: "cab_approve",
          text: {
            type: "plain_text",
            text: "Approve and rerun",
            emoji: true,
          },
          style: "primary",
          value: "approve",
        },
        {
          type: "button",
          action_id: "cab_reject",
          text: {
            type: "plain_text",
            text: "Reject",
            emoji: true,
          },
          style: "danger",
          value: "reject",
        },
      ],
    },
  ];
}

function isAcceptedReplySubtype(subtype: string | undefined): boolean {
  return subtype === "message_replied" || subtype === "thread_broadcast";
}

function createWebSocketAcceptValue(key: string): string {
  return crypto
    .createHash("sha1")
    .update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`)
    .digest("base64");
}

export function toIncomingChatMessage(
  payload: SlackEnvelope,
  defaultWorkingDirectory: string,
): IncomingChatMessage | undefined {
  if (payload.type !== "event_callback" || !payload.event) {
    return undefined;
  }

  const event = payload.event;
  const isNestedThreadReply =
    isAcceptedReplySubtype(event.subtype) && Boolean(event.message?.thread_ts);
  const isTopLevelThreadBroadcast =
    event.subtype === "thread_broadcast" && Boolean(event.thread_ts);

  if (event.bot_id) {
    return undefined;
  }

  if (event.subtype && !isNestedThreadReply && !isTopLevelThreadBroadcast) {
    return undefined;
  }

  if (event.type !== "message" && event.type !== "app_mention") {
    return undefined;
  }

  const messageObject = event.message;
  const effectiveUser = messageObject?.user ?? event.user;
  const effectiveText = messageObject?.text ?? event.text;
  const effectiveTs = messageObject?.ts ?? event.ts;
  const effectiveThreadTs = messageObject?.thread_ts ?? event.thread_ts;

  if (!event.channel || !effectiveUser || !effectiveText) {
    return undefined;
  }

  const threadId = effectiveThreadTs ?? effectiveTs;
  if (!threadId) {
    return undefined;
  }

  const isThreadReply = Boolean(effectiveThreadTs) && effectiveThreadTs !== effectiveTs;

  if (event.type === "message" && !isThreadReply) {
    return undefined;
  }

  return {
    thread: {
      provider: "slack",
      channelId: event.channel,
      threadId,
    },
    userId: effectiveUser,
    text: normalizeSlackText(effectiveText),
    requestedWorkingDirectory: defaultWorkingDirectory,
  };
}

async function readRawBody(request: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = [];

  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }

  return Buffer.concat(chunks);
}

function parseJsonBody(rawBody: Buffer): SlackEnvelope {
  if (rawBody.length === 0) {
    return {};
  }

  return JSON.parse(rawBody.toString("utf8")) as SlackEnvelope;
}

function parseSlackPayload(rawBody: Buffer, contentType: string | undefined): SlackEnvelope {
  if (contentType?.startsWith("application/x-www-form-urlencoded")) {
    const parsed = querystring.parse(rawBody.toString("utf8"));
    const payload = parsed.payload;
    if (typeof payload === "string") {
      return JSON.parse(payload) as SlackEnvelope;
    }
    return {};
  }

  return parseJsonBody(rawBody);
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

async function openSlackSocketUrl(appToken: string): Promise<string> {
  const response = await fetchJson<SlackSocketOpenResponse>({
    url: "https://slack.com/api/apps.connections.open",
    method: "POST",
    headers: {
      authorization: `Bearer ${appToken}`,
      "content-type": "application/json; charset=utf-8",
    },
    body: "{}",
  });

  if (!response.ok || !response.url) {
    throw new Error(`Slack Socket Mode open failed: ${response.error ?? "unknown_error"}`);
  }

  return response.url;
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
            reject(new Error(`Slack API HTTP error: ${response.statusCode ?? 500}`));
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

function openSocketStream(urlString: string): Promise<SlackSocketConnection> {
  return new Promise<SlackSocketConnection>((resolve, reject) => {
    const url = new URL(urlString);
    const secure = url.protocol === "wss:";
    const port = Number.parseInt(url.port || (secure ? "443" : "80"), 10);
    const socket: net.Socket = secure
      ? tls.connect({
          host: url.hostname,
          port,
          servername: url.hostname,
        })
      : net.connect({
          host: url.hostname,
          port,
        });

    let settled = false;
    let handshakeBuffer = Buffer.alloc(0);
    let frameBuffer = Buffer.alloc(0);
    let closed = false;
    let messageHandler: ((message: string) => void) | undefined;
    let closeHandler: (() => void) | undefined;
    const requestKey = crypto.randomBytes(16).toString("base64");

    const fail = (error: Error): void => {
      if (!settled) {
        settled = true;
        reject(error);
        socket.destroy();
        return;
      }

      if (!closed) {
        closed = true;
        closeHandler?.();
      }
    };

    const sendFrame = async (opcode: number, payload: Buffer): Promise<void> => {
      const mask = crypto.randomBytes(4);
      const header: number[] = [0x80 | opcode];
      if (payload.length < 126) {
        header.push(0x80 | payload.length);
      } else if (payload.length < 65536) {
        header.push(0x80 | 126, (payload.length >> 8) & 0xff, payload.length & 0xff);
      } else {
        const high = Math.floor(payload.length / 2 ** 32);
        const low = payload.length >>> 0;
        header.push(
          0x80 | 127,
          (high >> 24) & 0xff,
          (high >> 16) & 0xff,
          (high >> 8) & 0xff,
          high & 0xff,
          (low >> 24) & 0xff,
          (low >> 16) & 0xff,
          (low >> 8) & 0xff,
          low & 0xff,
        );
      }

      const maskedPayload = Buffer.from(payload);
      for (let index = 0; index < maskedPayload.length; index += 1) {
        const currentByte = maskedPayload[index];
        if (currentByte === undefined) {
          continue;
        }

        maskedPayload[index] = currentByte ^ (mask[index % 4] ?? 0);
      }

      await new Promise<void>((resolveWrite, rejectWrite) => {
        socket.write(Buffer.concat([Buffer.from(header), mask, maskedPayload]), (error) => {
          if (error) {
            rejectWrite(error);
            return;
          }

          resolveWrite();
        });
      });
    };

    const parseFrames = (): void => {
      while (frameBuffer.length >= 2) {
        const firstByte = frameBuffer[0] ?? 0;
        const secondByte = frameBuffer[1] ?? 0;
        const opcode = firstByte & 0x0f;
        const masked = (secondByte & 0x80) !== 0;
        let payloadLength = secondByte & 0x7f;
        let offset = 2;

        if (payloadLength === 126) {
          if (frameBuffer.length < 4) {
            return;
          }

          payloadLength = frameBuffer.readUInt16BE(2);
          offset = 4;
        } else if (payloadLength === 127) {
          if (frameBuffer.length < 10) {
            return;
          }

          const high = frameBuffer.readUInt32BE(2);
          const low = frameBuffer.readUInt32BE(6);
          payloadLength = high * 2 ** 32 + low;
          offset = 10;
        }

        const maskLength = masked ? 4 : 0;
        const frameLength = offset + maskLength + payloadLength;
        if (frameBuffer.length < frameLength) {
          return;
        }

        const mask = masked ? frameBuffer.subarray(offset, offset + 4) : undefined;
        const payloadStart = offset + maskLength;
        const payload = Buffer.from(frameBuffer.subarray(payloadStart, payloadStart + payloadLength));
        frameBuffer = frameBuffer.subarray(frameLength);

        if (mask) {
          for (let index = 0; index < payload.length; index += 1) {
            const currentByte = payload[index];
            if (currentByte === undefined) {
              continue;
            }

            payload[index] = currentByte ^ (mask[index % 4] ?? 0);
          }
        }

        if (opcode === 0x1) {
          messageHandler?.(payload.toString("utf8"));
          continue;
        }

        if (opcode === 0x8) {
          void sendFrame(0x8, Buffer.alloc(0)).catch(() => undefined);
          socket.end();
          return;
        }

        if (opcode === 0x9) {
          void sendFrame(0xA, payload).catch(() => undefined);
        }
      }
    };

    socket.once("error", (error) => {
      fail(error instanceof Error ? error : new Error(String(error)));
    });

    socket.on("close", () => {
      if (!settled) {
        reject(new Error("Slack Socket Mode connection closed during handshake."));
        settled = true;
        return;
      }

      if (!closed) {
        closed = true;
        closeHandler?.();
      }
    });

    socket.on("data", (chunk) => {
      const data = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      if (!settled) {
        handshakeBuffer = Buffer.concat([handshakeBuffer, data]);
        const terminator = handshakeBuffer.indexOf("\r\n\r\n");
        if (terminator < 0) {
          return;
        }

        const headerText = handshakeBuffer.subarray(0, terminator).toString("utf8");
        const headerLines = headerText.split("\r\n");
        const statusLine = headerLines.shift() ?? "";
        const headers = new Map<string, string>();
        for (const line of headerLines) {
          const separator = line.indexOf(":");
          if (separator < 0) {
            continue;
          }

          const name = line.slice(0, separator).trim().toLowerCase();
          const value = line.slice(separator + 1).trim();
          headers.set(name, value);
        }

        if (!statusLine.includes("101")) {
          fail(new Error(`Slack Socket Mode handshake failed: ${statusLine}`));
          return;
        }

        const accept = headers.get("sec-websocket-accept");
        if (accept !== createWebSocketAcceptValue(requestKey)) {
          fail(new Error("Slack Socket Mode handshake failed: invalid accept key."));
          return;
        }

        settled = true;
        frameBuffer = handshakeBuffer.subarray(terminator + 4);
        resolve({
          close: async () => {
            if (!closed) {
              closed = true;
              await sendFrame(0x8, Buffer.alloc(0)).catch(() => undefined);
              socket.end();
            }
          },
          setMessageHandler: (handler) => {
            messageHandler = handler;
          },
          setCloseHandler: (handler) => {
            closeHandler = handler;
          },
          sendText: async (text) => {
            await sendFrame(0x1, Buffer.from(text, "utf8"));
          },
        });
        parseFrames();
        return;
      }

      frameBuffer = Buffer.concat([frameBuffer, data]);
      parseFrames();
    });

    socket.once(secure ? "secureConnect" : "connect", () => {
      const requestPath = `${url.pathname}${url.search}`;
      socket.write(
        [
          `GET ${requestPath || "/"} HTTP/1.1`,
          `Host: ${url.hostname}`,
          "Upgrade: websocket",
          "Connection: Upgrade",
          `Sec-WebSocket-Key: ${requestKey}`,
          "Sec-WebSocket-Version: 13",
          "\r\n",
        ].join("\r\n"),
      );
    });
  });
}

export function verifySlackRequest(input: {
  rawBody: Buffer;
  signingSecret: string;
  signature: string | undefined;
  timestamp: string | undefined;
  now?: number;
}): boolean {
  if (!input.signature || !input.timestamp) {
    return false;
  }

  const parsedTimestamp = Number.parseInt(input.timestamp, 10);
  if (!Number.isFinite(parsedTimestamp)) {
    return false;
  }

  const nowSeconds = Math.floor((input.now ?? Date.now()) / 1000);
  if (Math.abs(nowSeconds - parsedTimestamp) > 60 * 5) {
    return false;
  }

  const baseString = `v0:${input.timestamp}:${input.rawBody.toString("utf8")}`;
  const expected =
    "v0=" +
    crypto.createHmac("sha256", input.signingSecret).update(baseString).digest("hex");

  const expectedBuffer = Buffer.from(expected, "utf8");
  const actualBuffer = Buffer.from(input.signature, "utf8");

  if (expectedBuffer.length !== actualBuffer.length) {
    return false;
  }

  return crypto.timingSafeEqual(expectedBuffer, actualBuffer);
}

export class SlackAdapter implements ChatAdapter {
  private readonly options: SlackAdapterOptions;
  private readonly socketConnector: SlackSocketConnector;
  private readonly openSocketUrl: (appToken: string) => Promise<string>;
  private readonly slackApiFetch: <T>(input: {
    url: string;
    method: string;
    headers: Record<string, string>;
    body?: string;
  }) => Promise<T>;
  private handler?: ChatMessageHandler;
  private server: http.Server | undefined;
  private socketConnection: SlackSocketConnection | undefined;
  private socketReconnectTimer: NodeJS.Timeout | undefined;
  private readonly seenEventIds = new Set<string>();
  private readonly auditLogger: AuditLogger;
  private stopped = false;
  private connectionStatus: ChatConnectionStatus;

  constructor(
    config: Pick<
      AppConfig,
      | "port"
      | "host"
      | "defaultWorkingDirectory"
      | "slackMode"
      | "slackSigningSecret"
      | "slackBotToken"
      | "slackAppToken"
    >,
    auditLogger?: AuditLogger,
    dependencies?: {
      slackApiFetch?: typeof fetchJson;
      openSocketUrl?: (appToken: string) => Promise<string>;
      socketConnector?: SlackSocketConnector;
    },
  ) {
    const options: SlackAdapterOptions = {
      mode: config.slackMode,
      port: config.port,
      host: config.host,
      defaultWorkingDirectory: config.defaultWorkingDirectory,
    };

    if (config.slackSigningSecret) {
      options.signingSecret = config.slackSigningSecret;
    }

    if (config.slackBotToken) {
      options.botToken = config.slackBotToken;
    }

    if (config.slackAppToken) {
      options.appToken = config.slackAppToken;
    }

    this.options = options;
    this.auditLogger = auditLogger ?? new NoopAuditLogger();
    this.slackApiFetch = dependencies?.slackApiFetch ?? fetchJson;
    this.openSocketUrl = dependencies?.openSocketUrl ?? openSlackSocketUrl;
    this.socketConnector = dependencies?.socketConnector ?? {
      open: openSocketStream,
    };
    this.connectionStatus = {
      provider: "slack",
      mode: options.mode,
      state: options.mode === "socket" ? "disconnected" : "disconnected",
      connected: false,
      statusText: options.mode === "socket" ? "Socket Mode is not connected." : "HTTP listener is stopped.",
    };
  }

  async start(handler: ChatMessageHandler): Promise<void> {
    this.handler = handler;
    this.stopped = false;

    if (this.options.mode === "socket") {
      this.updateConnectionStatus({
        state: "connecting",
        connected: false,
        statusText: "Connecting to Slack Socket Mode...",
      });
      await this.startSocketModeGracefully();
      return;
    }

    this.server = http.createServer(async (request, response) => {
      await this.handleHttpRequest(request, response);
    });

    await new Promise<void>((resolve, reject) => {
      this.server?.once("error", reject);
      this.server?.listen(this.options.port, this.options.host, () => resolve());
    });

    console.log(
      `Slack adapter listening on http://${this.options.host}:${this.options.port}/slack/events`,
    );
    this.updateConnectionStatus({
      state: "listening",
      connected: true,
      statusText: `Listening for Slack HTTP events on ${this.options.host}:${this.getListeningPort() ?? this.options.port}.`,
    });
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.socketReconnectTimer) {
      clearTimeout(this.socketReconnectTimer);
      this.socketReconnectTimer = undefined;
    }

    if (this.socketConnection) {
      const connection = this.socketConnection;
      this.socketConnection = undefined;
      await connection.close().catch(() => undefined);
    }

    this.updateConnectionStatus({
      state: "disconnected",
      connected: false,
      statusText:
        this.options.mode === "socket"
          ? "Slack Socket Mode is stopped."
          : "Slack HTTP listener is stopped.",
    });

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
    server.closeIdleConnections?.();
    server.closeAllConnections?.();
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

  getConnectionStatus(): ChatConnectionStatus {
    return { ...this.connectionStatus };
  }

  async postThreadMessage(
    thread: ChatThreadRef,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<string | undefined> {
    if (this.options.botToken) {
      const blocks =
        options?.blocks ??
        (text.startsWith("Approval required.") ? buildApprovalBlocks() : undefined);

      const payload = await this.slackApiFetch<SlackMessageResponse>({
        url: "https://slack.com/api/chat.postMessage",
        method: "POST",
        headers: {
          authorization: `Bearer ${this.options.botToken}`,
          "content-type": "application/json; charset=utf-8",
        },
        body: JSON.stringify({
          channel: thread.channelId,
          thread_ts: thread.threadId,
          text,
          blocks,
        }),
      });

      if (!payload.ok) {
        throw new Error(`Slack API error: ${payload.error ?? "unknown_error"}`);
      }

      await this.auditLogger.log({
        type: "slack.message.posted",
        channelId: thread.channelId,
        threadId: thread.threadId,
        text,
        hasBlocks: Boolean(blocks),
      });

      return payload.ts;
    }

    const prefix = `[slack:${thread.channelId}:${thread.threadId}]`;
    console.log(prefix, text, options?.blocks ? JSON.stringify(options.blocks) : "");
    await this.auditLogger.log({
      type: "slack.message.posted",
      channelId: thread.channelId,
      threadId: thread.threadId,
      text,
      hasBlocks: Boolean(options?.blocks),
      delivery: "stdout",
    });
    return undefined;
  }

  async startNewThread(
    channelId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<ChatThreadRef | undefined> {
    if (this.options.botToken) {
      const payload = await this.slackApiFetch<SlackMessageResponse>({
        url: "https://slack.com/api/chat.postMessage",
        method: "POST",
        headers: {
          authorization: `Bearer ${this.options.botToken}`,
          "content-type": "application/json; charset=utf-8",
        },
        body: JSON.stringify({
          channel: channelId,
          text,
          blocks: options?.blocks,
        }),
      });

      if (!payload.ok || !payload.ts) {
        throw new Error(`Slack API error: ${payload.error ?? "unknown_error"}`);
      }

      await this.auditLogger.log({
        type: "slack.thread.started",
        channelId,
        threadId: payload.ts,
        text,
        hasBlocks: Boolean(options?.blocks),
      });

      return {
        provider: "slack",
        channelId,
        threadId: payload.ts,
      };
    }

    const threadId = `synthetic-thread-${crypto.randomUUID()}`;
    console.log(`[slack:${channelId}:${threadId}] [new-thread]`, text, options?.blocks ? JSON.stringify(options.blocks) : "");
    await this.auditLogger.log({
      type: "slack.thread.started",
      channelId,
      threadId,
      text,
      hasBlocks: Boolean(options?.blocks),
      delivery: "stdout",
    });
    return {
      provider: "slack",
      channelId,
      threadId,
    };
  }

  async updateThreadMessage(
    thread: ChatThreadRef,
    messageId: string,
    text: string,
    options?: ChatMessageOptions,
  ): Promise<void> {
    if (this.options.botToken) {
      const blocks =
        options?.blocks ??
        (text.startsWith("Approval required.") ? buildApprovalBlocks() : undefined);

      const payload = await this.slackApiFetch<SlackMessageResponse>({
        url: "https://slack.com/api/chat.update",
        method: "POST",
        headers: {
          authorization: `Bearer ${this.options.botToken}`,
          "content-type": "application/json; charset=utf-8",
        },
        body: JSON.stringify({
          channel: thread.channelId,
          ts: messageId,
          text,
          blocks,
        }),
      });

      if (!payload.ok) {
        throw new Error(`Slack API error: ${payload.error ?? "unknown_error"}`);
      }

      await this.auditLogger.log({
        type: "slack.message.updated",
        channelId: thread.channelId,
        threadId: thread.threadId,
        messageId,
        text,
        hasBlocks: Boolean(blocks),
      });
      return;
    }

    const prefix = `[slack:${thread.channelId}:${thread.threadId}:${messageId}]`;
    console.log(`${prefix} [update]`, text, options?.blocks ? JSON.stringify(options.blocks) : "");
    await this.auditLogger.log({
      type: "slack.message.updated",
      channelId: thread.channelId,
      threadId: thread.threadId,
      messageId,
      text,
      hasBlocks: Boolean(options?.blocks),
      delivery: "stdout",
    });
  }

  async simulateIncomingMessage(message: Parameters<ChatMessageHandler>[0]): Promise<void> {
    if (!this.handler) {
      throw new Error("Slack adapter has not been started.");
    }

    await this.handler(message);
  }

  private async startSocketMode(): Promise<void> {
    if (!this.options.appToken) {
      throw new Error("SLACK_APP_TOKEN is required when CAB_SLACK_MODE=socket.");
    }

    const socketUrl = await this.openSocketUrl(this.options.appToken);
    const connection = await this.socketConnector.open(socketUrl);
    this.socketConnection = connection;
    await this.auditLogger.log({
      type: "slack.socket.connected",
      mode: "socket",
    });
    connection.setCloseHandler(() => {
      void this.auditLogger.log({
        type: "slack.socket.closed",
      });
      this.socketConnection = undefined;
      this.updateConnectionStatus({
        state: "disconnected",
        connected: false,
        statusText: "Slack Socket Mode disconnected. Waiting to reconnect...",
      });
      if (this.stopped) {
        return;
      }

      this.scheduleSocketReconnect();
    });
    connection.setMessageHandler((message) => {
      void this.handleSocketMessage(message).catch((error: unknown) => {
        console.error("Slack Socket Mode handler failed.", error);
      });
    });

    console.log("Slack adapter connected via Socket Mode.");
    this.updateConnectionStatus({
      state: "connected",
      connected: true,
      statusText: "Connected to Slack via Socket Mode.",
      clearError: true,
    });
  }

  private async startSocketModeGracefully(): Promise<void> {
    try {
      await this.startSocketMode();
    } catch (error) {
      console.error("Slack Socket Mode initial connect failed.", error);
      this.updateConnectionStatus({
        state: "disconnected",
        connected: false,
        statusText: "Slack Socket Mode initial connect failed. Retrying...",
        lastError: error instanceof Error ? error.message : String(error),
      });
      await this.auditLogger.log({
        type: "slack.socket.connect-failed",
        phase: "initial",
        error: error instanceof Error ? error.message : String(error),
      });
      this.scheduleSocketReconnect();
    }
  }

  private scheduleSocketReconnect(): void {
    if (this.socketReconnectTimer || this.stopped) {
      return;
    }

    this.updateConnectionStatus({
      state: "connecting",
      connected: false,
      statusText: "Reconnecting to Slack Socket Mode...",
    });
    void this.auditLogger.log({
      type: "slack.socket.reconnect-scheduled",
      delayMs: 1000,
    });

    this.socketReconnectTimer = setTimeout(() => {
      this.socketReconnectTimer = undefined;
      void this.startSocketMode().catch((error: unknown) => {
        console.error("Slack Socket Mode reconnect failed.", error);
        this.updateConnectionStatus({
          state: "disconnected",
          connected: false,
          statusText: "Slack Socket Mode reconnect failed. Retrying...",
          lastError: error instanceof Error ? error.message : String(error),
        });
        void this.auditLogger.log({
          type: "slack.socket.connect-failed",
          phase: "reconnect",
          error: error instanceof Error ? error.message : String(error),
        });
        this.scheduleSocketReconnect();
      });
    }, 1000);
  }

  private async handleSocketMessage(rawMessage: string): Promise<void> {
    const envelope = JSON.parse(rawMessage) as SlackSocketEnvelope;
    if (envelope.envelope_id && this.socketConnection) {
      await this.socketConnection.sendText(JSON.stringify({ envelope_id: envelope.envelope_id }));
    }

    if (envelope.type === "disconnect") {
      await this.auditLogger.log({
        type: "slack.socket.disconnect",
        reason: envelope.reason ?? "",
      });
      this.updateConnectionStatus({
        state: "disconnected",
        connected: false,
        statusText: `Slack Socket Mode disconnected (${envelope.reason ?? "unknown"}). Reconnecting...`,
      });
      this.scheduleSocketReconnect();
      await this.socketConnection?.close();
      return;
    }

    if (!envelope.payload) {
      return;
    }

    await this.processIncomingPayload(envelope.payload);
  }

  private async handleHttpRequest(
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> {
    if (request.method === "GET" && request.url === "/healthz") {
      writeJson(response, 200, { ok: true });
      return;
    }

    if (request.method !== "POST" || request.url !== "/slack/events") {
      writeJson(response, 404, { error: "Not found" });
      return;
    }

    try {
      const rawBody = await readRawBody(request);

      if (this.options.signingSecret) {
        const verified = verifySlackRequest({
          rawBody,
          signingSecret: this.options.signingSecret,
          signature: request.headers["x-slack-signature"]?.toString(),
          timestamp: request.headers["x-slack-request-timestamp"]?.toString(),
        });

        if (!verified) {
          await this.auditLogger.log({
            type: "slack.request.rejected",
            reason: "invalid-signature",
          });
          writeJson(response, 401, { error: "Invalid Slack signature" });
          return;
        }
      }

      const payload = parseSlackPayload(rawBody, request.headers["content-type"]?.toString());

      if (payload.type === "url_verification") {
        await this.auditLogger.log({
          type: "slack.url-verification",
        });
        writeText(response, 200, payload.challenge ?? "");
        return;
      }

      writeJson(response, 200, { ok: true });
      await this.processIncomingPayload(payload);
    } catch (error) {
      const text = error instanceof Error ? error.message : "Invalid request.";
      await this.auditLogger.log({
        type: "slack.request.failed",
        error: text,
      });
      writeJson(response, 400, { error: text });
    }
  }

  private async processIncomingPayload(payload: SlackEnvelope): Promise<void> {
    if (payload.type === "block_actions") {
      await this.handleBlockActions(payload);
      return;
    }

    if (payload.event_id) {
      if (this.seenEventIds.has(payload.event_id)) {
        await this.auditLogger.log({
          type: "slack.event.duplicate",
          eventId: payload.event_id,
        });
        return;
      }

      this.seenEventIds.add(payload.event_id);
    }

    const message = toIncomingChatMessage(payload, this.options.defaultWorkingDirectory);
    await this.auditLogger.log({
      type: "slack.event.received",
      eventId: payload.event_id ?? "",
      eventType: payload.event?.type ?? "",
      eventSubtype: payload.event?.subtype ?? "",
      channelId: message?.thread.channelId ?? payload.event?.channel ?? "",
      threadId: message?.thread.threadId ?? payload.event?.thread_ts ?? payload.event?.ts ?? "",
      userId: message?.userId ?? payload.event?.user ?? "",
      accepted: Boolean(message),
      text: message?.text ?? "",
    });

    if (message && this.handler) {
      queueMicrotask(() => {
        void this.handler?.(message).catch((error: unknown) => {
          console.error("Slack handler failed.", error);
        });
      });
    }
  }

  private async handleBlockActions(payload: SlackEnvelope): Promise<void> {
    const actionId = payload.actions?.[0]?.action_id;
    const channelId = payload.channel?.id;
    const threadId = payload.message?.thread_ts ?? payload.message?.ts;
    const userId = payload.user?.id;
    await this.auditLogger.log({
      type: "slack.block-action.received",
      actionId: actionId ?? "",
      channelId: channelId ?? "",
      threadId: threadId ?? "",
      userId: userId ?? "",
    });

    if (
      (actionId === "cab_approve" || actionId === "cab_reject" || actionId === "cab_cancel") &&
      channelId &&
      threadId &&
      userId &&
      this.handler
    ) {
      queueMicrotask(() => {
        void this.handler?.({
          thread: {
            provider: "slack",
            channelId,
            threadId,
          },
          userId,
          text:
            actionId === "cab_approve"
              ? "approve"
              : actionId === "cab_reject"
                ? "reject"
                : "cancel",
          requestedWorkingDirectory: this.options.defaultWorkingDirectory,
        }).catch((error: unknown) => {
          console.error("Slack interaction handler failed.", error);
        });
      });
    }
  }

  private updateConnectionStatus(input: {
    state: ChatConnectionStatus["state"];
    connected: boolean;
    statusText: string;
    lastError?: string;
    clearError?: boolean;
  }): void {
    const nextStatus: ChatConnectionStatus = {
      ...this.connectionStatus,
      state: input.state,
      connected: input.connected,
      statusText: input.statusText,
      lastChangedAt: new Date().toISOString(),
    };

    if (input.clearError) {
      delete nextStatus.lastError;
    } else if (input.lastError !== undefined) {
      nextStatus.lastError = input.lastError;
    }

    this.connectionStatus = nextStatus;
  }
}
