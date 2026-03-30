import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import querystring from "node:querystring";
import { MattermostAdapter } from "../src/adapters/mattermost/mattermost-adapter.js";
import { RecordingAuditLogger } from "./helpers/fakes.js";

test("MattermostAdapter accepts outgoing webhook events over HTTP", async () => {
  const auditLogger = new RecordingAuditLogger();
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
      mattermostWebhookToken: "hook-token",
    },
    auditLogger,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        if (input.method === "GET") {
          return {
            id: "reply-post",
            root_id: "root-post",
            channel_id: "channel-1",
          } as T;
        }

        throw new Error(`unexpected request: ${input.method} ${input.url}`);
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

  const port = adapter.getListeningPort();
  assert.ok(port);

  const responseBody = await postForm({
    port,
    body: querystring.stringify({
      token: "hook-token",
      channel_id: "channel-1",
      user_id: "user-1",
      post_id: "reply-post",
      trigger_word: "@cab",
      text: "@cab continue here",
    }),
  });

  assert.equal(responseBody, '{"ok":true}');
  await waitForMicrotasks();
  assert.deepEqual(received, [{ text: "continue here", threadId: "root-post" }]);
  await adapter.stop();
});

test("MattermostAdapter starts a new thread via the posts API", async () => {
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        assert.equal(input.method, "POST");
        assert.match(input.url, /\/api\/v4\/posts$/);
        assert.equal(
          input.body,
          JSON.stringify({
            channel_id: "channel-1",
            message: "new thread",
            props: {
              chat_agent_bridge: "1",
            },
          }),
        );
        return {
          id: "root-post",
          channel_id: "channel-1",
        } as T;
      },
    },
  );

  const thread = await adapter.startNewThread("channel-1", "new thread");

  assert.deepEqual(thread, {
    provider: "mattermost",
    channelId: "channel-1",
    threadId: "root-post",
  });
});

test("MattermostAdapter includes interactive actions in post props when configured", async () => {
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
      mattermostActionBaseUrl: "http://bridge.lan:8787",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        const body = JSON.parse(input.body ?? "{}") as {
          props?: {
            attachments?: Array<{
              actions?: Array<{
                id?: string;
                integration?: {
                  url?: string;
                  context?: {
                    action?: string;
                    token?: string;
                  };
                };
              }>;
            }>;
          };
        };
        assert.equal(input.method, "POST");
        assert.match(input.url, /\/api\/v4\/posts$/);
        assert.equal(body.props?.attachments?.[0]?.actions?.[0]?.id, "cabapprove");
        assert.equal(
          body.props?.attachments?.[0]?.actions?.[0]?.integration?.url,
          "http://bridge.lan:8787/mattermost/actions",
        );
        assert.equal(
          body.props?.attachments?.[0]?.actions?.[0]?.integration?.context?.action,
          "approve",
        );
        assert.ok(body.props?.attachments?.[0]?.actions?.[0]?.integration?.context?.token);
        return {
          id: "reply-post",
          channel_id: "channel-1",
        } as T;
      },
    },
  );

  await adapter.postThreadMessage(
    {
      provider: "mattermost",
      channelId: "channel-1",
      threadId: "root-post",
    },
    "Approval required.",
    {
      actions: [
        {
          id: "cab_approve",
          label: "Approve and rerun",
          value: "approve",
          style: "primary",
        },
      ],
    },
  );
});

test("MattermostAdapter accepts slash commands with a synthetic thread id", async () => {
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostActorMode: "self",
      mattermostWebhookToken: "hook-token",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        if (/\/api\/v4\/users\/me$/.test(input.url)) {
          return {
            id: "user-1",
            username: "me",
          } as T;
        }

        if (/\/api\/v4\/channels\/private-1\/members$/.test(input.url)) {
          return [{ user_id: "user-1" }] as T;
        }

        assert.match(input.url, /\/api\/v4\/channels\/private-1$/);
        return {
          id: "private-1",
          type: "D",
        } as T;
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

  const port = adapter.getListeningPort();
  assert.ok(port);

  const responseBody = await postToPath({
    port,
    path: "/mattermost/commands",
    body: querystring.stringify({
      token: "hook-token",
      channel_id: "private-1",
      user_id: "user-1",
      text: "status",
    }),
  });

  assert.equal(responseBody, "");
  await waitForMicrotasks();
  assert.deepEqual(received, [{ text: "status", threadId: "command:private-1:user-1" }]);
  await adapter.stop();
});

test("MattermostAdapter filters non-self-DM slash commands when self mode is enabled", async () => {
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostActorMode: "self",
      mattermostWebhookToken: "hook-token",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: { url: string }) => {
        if (/\/api\/v4\/users\/me$/.test(input.url)) {
          return {
            id: "user-1",
            username: "me",
          } as T;
        }

        return {
          id: "channel-1",
          type: "O",
        } as T;
      },
    },
  );

  const received: string[] = [];
  await adapter.start(async (message) => {
    received.push(message.text);
  });

  const port = adapter.getListeningPort();
  assert.ok(port);

  await postToPath({
    port,
    path: "/mattermost/commands",
    body: querystring.stringify({
      token: "hook-token",
      channel_id: "channel-1",
      user_id: "user-1",
      text: "status",
    }),
  });

  await waitForMicrotasks();
  assert.deepEqual(received, []);
  await adapter.stop();
});

test("MattermostAdapter converts interactive actions into bridge commands", async () => {
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "http",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
      mattermostWebhookToken: "hook-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        assert.match(input.url, /\/api\/v4\/posts\/approval-post$/);
        return {
          id: "approval-post",
          root_id: "root-post",
          channel_id: "channel-1",
        } as T;
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

  const port = adapter.getListeningPort();
  assert.ok(port);

  const responseBody = await postJson({
    port,
    path: "/mattermost/actions",
    body: JSON.stringify({
      user_id: "user-1",
      post_id: "approval-post",
      channel_id: "channel-1",
      context: {
        action: "approve",
        token: "hook-token",
      },
    }),
  });

  assert.match(responseBody, /Approval received/);
  await waitForMicrotasks();
  assert.deepEqual(received, [{ text: "approve", threadId: "root-post" }]);
  await adapter.stop();
});

test("MattermostAdapter accepts websocket posted events", async () => {
  let messageHandler: ((event: { data?: unknown }) => void) | undefined;
  let openHandler: ((event: { data?: unknown }) => void) | undefined;
  let closeHandler: ((event: { data?: unknown }) => void) | undefined;
  const sentFrames: string[] = [];
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "websocket",
      mattermostActorMode: "self",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "bot-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        if (/\/api\/v4\/users\/me$/.test(input.url)) {
          return {
            id: "bot-user",
            username: "bridgebot",
          } as T;
        }

        if (/\/api\/v4\/channels\/channel-1\/members$/.test(input.url)) {
          return [{ user_id: "bot-user" }] as T;
        }

        assert.match(input.url, /\/api\/v4\/channels\/channel-1$/);
        return {
          id: "channel-1",
          type: "D",
        } as T;
      },
      socketFactory: () => ({
        addEventListener: (type, listener) => {
          if (type === "open") {
            openHandler = listener;
            queueMicrotask(() => {
              openHandler?.({});
            });
          } else if (type === "message") {
            messageHandler = listener;
          } else if (type === "close") {
            closeHandler = listener;
          }
        },
        send: (data) => {
          sentFrames.push(data);
        },
        close: () => {
          closeHandler?.({});
        },
      }),
    },
  );

  const received: Array<{ text: string; threadId: string }> = [];
  await adapter.start(async (message) => {
    received.push({
      text: message.text,
      threadId: message.thread.threadId,
    });
  });

  assert.match(sentFrames[0] ?? "", /authentication_challenge/);
  messageHandler?.({
    data: JSON.stringify({
      status: "OK",
      seq_reply: 1,
    }),
  });
  messageHandler?.({
    data: JSON.stringify({
      event: "posted",
      data: {
        post: JSON.stringify({
          id: "post-1",
          channel_id: "channel-1",
          root_id: "",
          user_id: "user-1",
          message: "@bridgebot continue here",
        }),
      },
    }),
  });

  await waitForMicrotasks();
  assert.deepEqual(received, [{ text: "continue here", threadId: "post-1" }]);
  await adapter.stop();
});

test("MattermostAdapter accepts plain self-DM posts in self mode", async () => {
  let messageHandler: ((event: { data?: unknown }) => void) | undefined;
  let openHandler: ((event: { data?: unknown }) => void) | undefined;
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "websocket",
      mattermostActorMode: "self",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "user-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        if (/\/api\/v4\/users\/me$/.test(input.url)) {
          return {
            id: "user-1",
            username: "me",
          } as T;
        }

        if (/\/api\/v4\/channels\/dm-1\/members$/.test(input.url)) {
          return [{ user_id: "user-1" }] as T;
        }

        return {
          id: "dm-1",
          type: "D",
        } as T;
      },
      socketFactory: () => ({
        addEventListener: (type, listener) => {
          if (type === "open") {
            openHandler = listener;
            queueMicrotask(() => {
              openHandler?.({});
            });
          } else if (type === "message") {
            messageHandler = listener;
          }
        },
        send: () => undefined,
        close: () => undefined,
      }),
    },
  );

  const received: string[] = [];
  await adapter.start(async (message) => {
    received.push(message.text);
  });

  messageHandler?.({
    data: JSON.stringify({
      status: "OK",
      seq_reply: 1,
    }),
  });
  messageHandler?.({
    data: JSON.stringify({
      event: "posted",
      data: {
        post: JSON.stringify({
          id: "post-1",
          channel_id: "dm-1",
          root_id: "",
          user_id: "user-1",
          message: "quick memo",
        }),
      },
    }),
  });
  messageHandler?.({
    data: JSON.stringify({
      event: "posted",
      data: {
        post: JSON.stringify({
          id: "post-plain",
          channel_id: "dm-1",
          root_id: "",
          user_id: "user-1",
          message: "plain memo",
        }),
      },
    }),
  });
  messageHandler?.({
    data: JSON.stringify({
      event: "posted",
      data: {
        post: JSON.stringify({
          id: "post-2",
          channel_id: "dm-1",
          root_id: "",
          user_id: "user-1",
          message: "bridge echo",
          props: {
            chat_agent_bridge: "1",
          },
        }),
      },
    }),
  });

  await waitForMicrotasks();
  assert.deepEqual(received, ["quick memo"]);
  await adapter.stop();
});

test("MattermostAdapter ignores one-to-one DMs with other users in self mode", async () => {
  let messageHandler: ((event: { data?: unknown }) => void) | undefined;
  let openHandler: ((event: { data?: unknown }) => void) | undefined;
  const adapter = new MattermostAdapter(
    {
      port: 0,
      host: "127.0.0.1",
      defaultWorkingDirectory: "C:/repo",
      mattermostMode: "websocket",
      mattermostActorMode: "self",
      mattermostServerUrl: "https://mattermost.example.local",
      mattermostBotToken: "user-token",
    },
    undefined,
    {
      mattermostApiFetch: async <T>(input: {
        url: string;
        method: string;
        headers: Record<string, string>;
        body?: string;
      }) => {
        if (/\/api\/v4\/users\/me$/.test(input.url)) {
          return {
            id: "user-1",
            username: "me",
          } as T;
        }

        if (/\/api\/v4\/channels\/dm-2\/members$/.test(input.url)) {
          return [{ user_id: "user-1" }, { user_id: "user-2" }] as T;
        }

        return {
          id: "dm-2",
          type: "D",
        } as T;
      },
      socketFactory: () => ({
        addEventListener: (type, listener) => {
          if (type === "open") {
            openHandler = listener;
            queueMicrotask(() => {
              openHandler?.({});
            });
          } else if (type === "message") {
            messageHandler = listener;
          }
        },
        send: () => undefined,
        close: () => undefined,
      }),
    },
  );

  const received: string[] = [];
  await adapter.start(async (message) => {
    received.push(message.text);
  });

  messageHandler?.({
    data: JSON.stringify({
      status: "OK",
      seq_reply: 1,
    }),
  });
  messageHandler?.({
    data: JSON.stringify({
      event: "posted",
      data: {
        post: JSON.stringify({
          id: "post-1",
          channel_id: "dm-2",
          root_id: "",
          user_id: "user-1",
          message: "should stay private",
        }),
      },
    }),
  });

  await waitForMicrotasks();
  assert.deepEqual(received, []);
  await adapter.stop();
});

async function postForm(input: { port: number; body: string }): Promise<string> {
  return await postToPath({
    ...input,
    path: "/mattermost/events",
  });
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

async function postJson(input: { port: number; path: string; body: string }): Promise<string> {
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
          "content-type": "application/json",
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
