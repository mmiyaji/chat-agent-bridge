import test from "node:test";
import assert from "node:assert/strict";
import { createAppConfig } from "../src/config/env.js";
import { AdminServer } from "../src/admin/server.js";
import { ConnectionManager } from "../src/core/connection-manager.js";
import { BridgeService } from "../src/core/bridge-service.js";
import { SessionManager } from "../src/core/session-manager.js";
import { InMemoryStateStore, InlineJobQueue, RecordingAgent, RecordingChatAdapter, RecordingAuditLogger } from "./helpers/fakes.js";

test("control center API connects, reports, disconnects, and rejects cross-origin requests", async () => {
  const config = createAppConfig({ CAB_SETTINGS_FILE: ".tmp-no-admin-config.json" });
  config.adminHost = "127.0.0.1"; config.adminPort = 0;
  const store = new InMemoryStateStore();
  const audit = new RecordingAuditLogger();
  const manager = new ConnectionManager(config, { codex: new RecordingAgent("codex") }, () => new RecordingChatAdapter(), async () => ({ authenticated: true, model: "test" }));
  const bridge = new BridgeService({ config, stateStore: store, sessionManager: new SessionManager(store), jobQueue: new InlineJobQueue(), chatAdapter: manager, agents: manager.agents, auditLogger: audit });
  await bridge.start();
  const server = new AdminServer({ config, stateStore: store, auditLogger: audit, bridge, connections: manager });
  await server.start();
  const base = `http://127.0.0.1:${server.getListeningPort()}`;
  try {
    const modelsPath = base + '/admin/api/models?agent=codex';
    assert.equal((await fetch(modelsPath, { method: 'POST' })).status, 403);
    assert.equal((await fetch(modelsPath, { method: 'POST', headers: { 'x-cab-request': 'admin', origin: 'https://untrusted.example' } })).status, 403);
    assert.equal((await fetch(base + '/admin/api/models?agent=invalid', { method: 'POST', headers: { 'x-cab-request': 'admin' } })).status, 400);
    const path = base + "/admin/api/connections/agent/connect";
    assert.equal((await fetch(path, { method: "POST" })).status, 403);
    assert.equal((await fetch(path, { method: "POST", headers: { "x-cab-request": "admin", origin: "https://untrusted.example" } })).status, 403);
    const connected = await fetch(path, { method: "POST", headers: { "x-cab-request": "admin" } });
    assert.equal(connected.status, 200);
    assert.equal((await connected.json()).agent.authenticated, true);
    const snapshot = await (await fetch(base + "/admin/api/connections")).json();
    assert.equal(snapshot.agent.state, "connected");
    assert.deepEqual(snapshot.nativeRequests, []);
    const disconnected = await fetch(base + "/admin/api/connections/agent/disconnect", { method: "POST", headers: { "x-cab-request": "admin" } });
    assert.equal((await disconnected.json()).agent.state, "disconnected");
    assert.match(await (await fetch(base + "/admin")).text(), /コントロールセンター/);
  } finally { await bridge.stop(); await server.stop(); }
});
