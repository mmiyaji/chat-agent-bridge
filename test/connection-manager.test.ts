import test from "node:test";
import assert from "node:assert/strict";
import { ConnectionManager } from "../src/core/connection-manager.js";
import { createAppConfig } from "../src/config/env.js";
import type { ChatAdapter, ChatMessageHandler } from "../src/adapters/chat.js";
import type { Agent } from "../src/agents/base.js";

function setup() {
  const config = createAppConfig({ CAB_SETTINGS_FILE: "./.test-missing-settings.json", CAB_CHAT_PROVIDER: "mattermost", MATTERMOST_SERVER_URL: "http://127.0.0.1:8065", MATTERMOST_BOT_TOKEN: "test", MATTERMOST_MODE: "websocket" });
  let stopCount = 0;
  let fail = false;
  let handler: ChatMessageHandler | undefined;
  const source: Agent = { kind: "codex", capabilities: { manualApproval: true, writeApprovalRerun: false, networkAccessControl: true }, async execute(r) { return { sessionId: r.session.sessionId, summary: "done" }; }, async cancelExecution() { return true; } };
  const factory = (): ChatAdapter => ({
    async start(h) { handler = h; if (fail) throw new Error("invalid token"); },
    async stop() { stopCount++; },
    getConnectionStatus() { return { provider: "mattermost", mode: "websocket", connected: true, state: "connected", statusText: "Connected" }; },
    async startNewThread() { return undefined; }, async postThreadMessage() { return "post"; }, async updateThreadMessage() {},
  });
  const manager = new ConnectionManager(config, { codex: source }, factory, async () => ({ authenticated: true, model: "test-model" }));
  return { manager, config, handler: () => handler!, stopCount: () => stopCount, fail: () => { fail = true; } };
}

test("connection failures keep runtime usable and clean the failed transport", async () => {
  const s = setup();
  await s.manager.start(async () => {});
  s.fail();
  await assert.rejects(s.manager.connectChat(), /invalid token/);
  assert.equal(s.stopCount(), 1);
  assert.equal(s.manager.snapshot().chat.state, "disconnected");
  assert.equal(s.manager.busy, false);
  await s.manager.connectAgent();
  assert.equal(s.manager.snapshot().agent.authenticated, true);
  await s.manager.disconnectAgent();
  assert.equal(s.manager.snapshot().agent.state, "disconnected");
});

test("switching is blocked while an incoming message runs, then reconnect disposes old transport", async () => {
  const s = setup();
  let release!: () => void;
  await s.manager.start(async () => new Promise<void>(r => { release = r; }));
  await s.manager.connectChat();
  const task = s.handler()({ thread: { provider: "mattermost", channelId: "c", threadId: "t" }, userId: "u", text: "hello", requestedWorkingDirectory: process.cwd() });
  await assert.rejects(s.manager.disconnectChat(), /実行中/);
  release(); await task;
  await s.manager.connectChat();
  assert.equal(s.stopCount(), 1);
  await s.manager.disconnectChat();
  assert.equal(s.stopCount(), 2);
  assert.throws(() => s.manager.postThreadMessage({ provider: "slack", channelId: "c", threadId: "t" }, "test"), /disconnected/);
});
