import test from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import http from "node:http";
import { SlackAdapter, toIncomingChatMessage, verifySlackRequest } from "../src/adapters/slack/slack-adapter.js";
import { RecordingAuditLogger } from "./helpers/fakes.js";

test("toIncomingChatMessage converts a Slack app_mention event", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "app_mention",
        channel: "C1",
        user: "U1",
        text: "<@UAPP> hello",
        ts: "100.1",
      },
    },
    "C:/repo",
  );

  assert.deepEqual(message, {
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "100.1",
    },
    userId: "U1",
    text: "hello",
    requestedWorkingDirectory: "C:/repo",
  });
});

test("toIncomingChatMessage prefers thread_ts for thread replies", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "app_mention",
        channel: "C1",
        user: "U1",
        text: "<@bot> continue",
        ts: "101.2",
        thread_ts: "100.1",
      },
    },
    "C:/repo",
  );

  assert.equal(message?.thread.threadId, "100.1");
});

test("toIncomingChatMessage strips a leading app mention", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "app_mention",
        channel: "C1",
        user: "U1",
        text: "<@UAPP> continue from the last change",
        ts: "101.2",
      },
    },
    "C:/repo",
  );

  assert.equal(message?.text, "continue from the last change");
});

test("toIncomingChatMessage ignores root-level plain channel messages", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "message",
        channel: "C1",
        user: "U1",
        text: "plain channel post",
        ts: "101.2",
      },
    },
    "C:/repo",
  );

  assert.equal(message, undefined);
});

test("toIncomingChatMessage accepts plain thread replies without a mention", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "message",
        channel: "C1",
        user: "U1",
        text: "continue from here",
        ts: "101.3",
        thread_ts: "100.1",
      },
    },
    "C:/repo",
  );

  assert.equal(message?.thread.threadId, "100.1");
  assert.equal(message?.text, "continue from here");
});

test("toIncomingChatMessage accepts nested message_replied thread payloads", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "message",
        subtype: "message_replied",
        channel: "C1",
        hidden: true,
        ts: "100.1",
        message: {
          type: "message",
          user: "U1",
          text: "approve",
          ts: "101.3",
          thread_ts: "100.1",
        },
      },
    },
    "C:/repo",
  );

  assert.equal(message?.thread.threadId, "100.1");
  assert.equal(message?.text, "approve");
});

test("toIncomingChatMessage accepts thread_broadcast replies", () => {
  const message = toIncomingChatMessage(
    {
      type: "event_callback",
      event: {
        type: "message",
        subtype: "thread_broadcast",
        channel: "C1",
        user: "U1",
        text: "thread update",
        ts: "101.3",
        thread_ts: "100.1",
      },
    },
    "C:/repo",
  );

  assert.equal(message?.thread.threadId, "100.1");
  assert.equal(message?.text, "thread update");
});

test("toIncomingChatMessage ignores bot and unrelated subtype events", () => {
  assert.equal(
    toIncomingChatMessage(
      {
        type: "event_callback",
        event: {
          type: "message",
          bot_id: "B1",
          channel: "C1",
          user: "U1",
          text: "ignored",
          ts: "100.1",
        },
      },
      "C:/repo",
    ),
    undefined,
  );

  assert.equal(
    toIncomingChatMessage(
      {
        type: "event_callback",
        event: {
          type: "message",
          subtype: "message_changed",
          channel: "C1",
          user: "U1",
          text: "ignored",
          ts: "100.1",
        },
      },
      "C:/repo",
    ),
    undefined,
  );
});

test("verifySlackRequest accepts a valid Slack signature", () => {
  const rawBody = Buffer.from('{"type":"url_verification","challenge":"abc"}', "utf8");
  const timestamp = "1700000000";
  const baseString = `v0:${timestamp}:${rawBody.toString("utf8")}`;
  const signature =
    "v0=" + crypto.createHmac("sha256", "secret").update(baseString).digest("hex");

  assert.equal(
    verifySlackRequest({
      rawBody,
      signingSecret: "secret",
      signature,
      timestamp,
      now: 1700000000 * 1000,
    }),
    true,
  );
});

test("verifySlackRequest rejects an invalid Slack signature", () => {
  const rawBody = Buffer.from('{"type":"url_verification","challenge":"abc"}', "utf8");

  assert.equal(
    verifySlackRequest({
      rawBody,
      signingSecret: "secret",
      signature: "v0=deadbeef",
      timestamp: "1700000000",
      now: 1700000000 * 1000,
    }),
    false,
  );
});

test("SlackAdapter accepts signed plain thread replies over HTTP", async () => {
  const auditLogger = new RecordingAuditLogger();
  const adapter = new SlackAdapter(
    {
      slackMode: "http",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      slackSigningSecret: "secret",
    },
    auditLogger,
  );
  const received: Array<{ text: string; threadId: string }> = [];

  await adapter.start(async (message) => {
    received.push({
      text: message.text,
      threadId: message.thread.threadId,
    });
  });

  const port = adapter.getListeningPort();
  assert.ok(port);

  const payload = {
    type: "event_callback",
    event_id: "EvHTTP1",
    event: {
      type: "message",
      channel: "C1",
      user: "U1",
      text: "continue from here",
      ts: "101.3",
      thread_ts: "100.1",
    },
  };

  const body = JSON.stringify(payload);
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const signature =
    "v0=" + crypto.createHmac("sha256", "secret").update(`v0:${timestamp}:${body}`).digest("hex");

  const responseBody = await postJson({
    port,
    body,
    headers: {
      "x-slack-request-timestamp": timestamp,
      "x-slack-signature": signature,
      "content-type": "application/json",
    },
  });

  assert.equal(responseBody, '{"ok":true}');
  await waitForMicrotasks();
  assert.deepEqual(received, [{ text: "continue from here", threadId: "100.1" }]);
  await adapter.stop();
});

test("SlackAdapter suppresses duplicate event ids over HTTP", async () => {
  const auditLogger = new RecordingAuditLogger();
  const adapter = new SlackAdapter(
    {
      slackMode: "http",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      slackSigningSecret: "secret",
    },
    auditLogger,
  );
  const received: string[] = [];

  await adapter.start(async (message) => {
    received.push(message.text);
  });

  const payload = {
    type: "event_callback",
    event_id: "EvHTTPDuplicate",
    event: {
      type: "message",
      channel: "C1",
      user: "U1",
      text: "continue from here",
      ts: "101.3",
      thread_ts: "100.1",
    },
  };

  const processIncomingPayload = (
    adapter as unknown as { processIncomingPayload(payload: unknown): Promise<void> }
  ).processIncomingPayload.bind(adapter);

  await processIncomingPayload(payload);
  await processIncomingPayload(payload);

  assert.deepEqual(received, ["continue from here"]);
  assert.equal(
    auditLogger.events.some(
      (event) => event.type === "slack.event.duplicate" && event.eventId === "EvHTTPDuplicate",
    ),
    true,
  );
  await adapter.stop();
});

test("SlackAdapter accepts Socket Mode events without HTTP ingress", async () => {
  const auditLogger = new RecordingAuditLogger();
  const sentFrames: string[] = [];
  let socketMessageHandler: ((message: string) => void) | undefined;
  let socketCloseHandler: (() => void) | undefined;

  const adapter = new SlackAdapter(
    {
      slackMode: "socket",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      slackAppToken: "xapp-test",
    },
    auditLogger,
    {
      openSocketUrl: async () => "wss://example.invalid/socket",
      socketConnector: {
        open: async () => ({
          close: async () => {
            socketCloseHandler?.();
          },
          setMessageHandler: (handler) => {
            socketMessageHandler = handler;
          },
          setCloseHandler: (handler) => {
            socketCloseHandler = handler;
          },
          sendText: async (text) => {
            sentFrames.push(text);
          },
        }),
      },
    },
  );

  const received: Array<{ text: string; threadId: string }> = [];
  await adapter.start(async (message) => {
    received.push({
      text: message.text,
      threadId: message.thread.threadId,
    });
  });

  assert.ok(socketMessageHandler);
  socketMessageHandler?.(
    JSON.stringify({
      envelope_id: "env-1",
      type: "events_api",
      payload: {
        type: "event_callback",
        event_id: "EvSocket1",
        event: {
          type: "message",
          channel: "C1",
          user: "U1",
          text: "continue from socket",
          ts: "101.3",
          thread_ts: "100.1",
        },
      },
    }),
  );

  await waitForMicrotasks();
  assert.deepEqual(sentFrames, ['{"envelope_id":"env-1"}']);
  assert.deepEqual(received, [{ text: "continue from socket", threadId: "100.1" }]);
  await adapter.stop();
});

test("SlackAdapter keeps the bridge alive when initial Socket Mode connect fails", async () => {
  const auditLogger = new RecordingAuditLogger();
  let openAttempts = 0;
  const adapter = new SlackAdapter(
    {
      slackMode: "socket",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      slackAppToken: "xapp-test",
    },
    auditLogger,
    {
      openSocketUrl: async () => "wss://example.invalid/socket",
      socketConnector: {
        open: async () => {
          openAttempts += 1;
          throw new Error("connect EACCES 1.2.3.4:443");
        },
      },
    },
  );

  await adapter.start(async () => undefined);
  await waitForMicrotasks();

  assert.equal(openAttempts, 1);
  assert.equal(
    auditLogger.events.some(
      (event) =>
        event.type === "slack.socket.connect-failed" &&
        event.phase === "initial" &&
        String(event.error).includes("EACCES"),
    ),
    true,
  );

  await adapter.stop();
});

test("SlackAdapter reconnects after a Socket Mode disconnect warning", async () => {
  const auditLogger = new RecordingAuditLogger();
  const received: string[] = [];
  const messageHandlers: Array<(message: string) => void> = [];
  const closeHandlers: Array<() => void> = [];
  let openAttempts = 0;

  const adapter = new SlackAdapter(
    {
      slackMode: "socket",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      slackAppToken: "xapp-test",
    },
    auditLogger,
    {
      openSocketUrl: async () => "wss://example.invalid/socket",
      socketConnector: {
        open: async () => {
          const connectionIndex = openAttempts;
          openAttempts += 1;
          return {
            close: async () => {
              const closeHandler = closeHandlers[connectionIndex];
              closeHandler?.();
            },
            setMessageHandler: (handler) => {
              messageHandlers[connectionIndex] = handler;
            },
            setCloseHandler: (handler) => {
              closeHandlers[connectionIndex] = handler;
            },
            sendText: async () => undefined,
          };
        },
      },
    },
  );

  await adapter.start(async (message) => {
    received.push(message.text);
  });

  assert.equal(openAttempts, 1);
  messageHandlers[0]?.(
    JSON.stringify({
      type: "disconnect",
      reason: "warning",
    }),
  );

  await new Promise((resolve) => setTimeout(resolve, 1100));
  assert.equal(openAttempts, 2);
  messageHandlers[1]?.(
    JSON.stringify({
      envelope_id: "env-reconnected",
      type: "events_api",
      payload: {
        type: "event_callback",
        event_id: "EvSocketReconnect",
        event: {
          type: "message",
          channel: "C1",
          user: "U1",
          text: "status",
          ts: "101.4",
          thread_ts: "100.1",
        },
      },
    }),
  );

  await waitForMicrotasks();
  assert.deepEqual(received, ["status"]);
  const status = adapter.getConnectionStatus();
  assert.equal(status.connected, true);
  assert.equal(status.reconnectCount, 1);
  assert.equal(status.disconnectCount, 1);
  assert.match(status.lastRecoveredAt ?? "", /T/);
  assert.equal(
    auditLogger.events.some(
      (event) => event.type === "slack.socket.disconnect" && event.reason === "warning",
    ),
    true,
  );
  assert.equal(
    auditLogger.events.some(
      (event) => event.type === "slack.socket.recovered" && event.outageDurationMs !== undefined,
    ),
    true,
  );

  await adapter.stop();
});

test("SlackAdapter converts cancel block actions into cancel messages", async () => {
  const auditLogger = new RecordingAuditLogger();
  const adapter = new SlackAdapter(
    {
      slackMode: "http",
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
    },
    auditLogger,
  );
  const received: string[] = [];

  await adapter.start(async (message) => {
    received.push(message.text);
  });

  const processIncomingPayload = (
    adapter as unknown as { processIncomingPayload(payload: unknown): Promise<void> }
  ).processIncomingPayload.bind(adapter);

  await processIncomingPayload({
    type: "block_actions",
    user: {
      id: "U1",
    },
    channel: {
      id: "C1",
    },
    message: {
      thread_ts: "100.1",
      ts: "101.3",
    },
    actions: [
      {
        action_id: "cab_cancel",
        value: "cancel",
      },
    ],
  });

  await waitForMicrotasks();
  assert.deepEqual(received, ["cancel"]);
  await adapter.stop();
});

async function postJson(input: {
  port: number;
  body: string;
  headers: Record<string, string>;
}): Promise<string> {
  return await new Promise<string>((resolve, reject) => {
    const request = http.request(
      {
        agent: false,
        host: "127.0.0.1",
        port: input.port,
        path: "/slack/events",
        method: "POST",
        headers: {
          ...input.headers,
          connection: "close",
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

async function waitForMicrotasks(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 10));
}
