import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { FileStateStore } from "../src/state/file-state-store.js";
import { ExecutionRecord, SessionBinding } from "../src/core/types.js";

async function createStore(): Promise<{
  dir: string;
  store: FileStateStore;
  stateFile: string;
}> {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), "cab-state-"));
  const stateFile = path.join(dir, "state.json");
  const store = new FileStateStore(stateFile);
  await store.initialize();
  return { dir, store, stateFile };
}

function createSession(): SessionBinding {
  return {
    sessionId: "session-1",
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo",
    createdAt: "2026-03-30T00:00:00.000Z",
    updatedAt: "2026-03-30T00:00:00.000Z",
  };
}

function createExecutionRecord(
  executionId: string,
  createdAt: string,
  status: ExecutionRecord["status"],
): ExecutionRecord {
  return {
    executionId,
    sessionId: "session-1",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    agent: "codex",
    prompt: `prompt-${executionId}`,
    workingDirectory: "C:/repo",
    approvalPolicy: "manual",
    networkAccess: "disabled",
    status,
    summary: `${status}-${executionId}`,
    createdAt,
    updatedAt: createdAt,
  };
}

test("FileStateStore persists execution history across reloads", async () => {
  const { store, stateFile } = await createStore();
  await store.upsertSession(createSession());
  await store.upsertExecutionRecord(
    createExecutionRecord("exec-1", "2026-03-30T01:00:00.000Z", "completed"),
  );

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();
  const sessions = await reloaded.listSessions();
  const records = await reloaded.listExecutionRecords({ sessionId: "session-1", limit: 10 });

  assert.equal(sessions.length, 1);
  assert.equal(records.length, 1);
  assert.equal(records[0].executionId, "exec-1");
  assert.equal(records[0].status, "completed");
});

test("FileStateStore sorts execution history newest first and honors limit", async () => {
  const { store } = await createStore();
  await store.upsertExecutionRecord(
    createExecutionRecord("exec-1", "2026-03-30T01:00:00.000Z", "completed"),
  );
  await store.upsertExecutionRecord(
    createExecutionRecord("exec-2", "2026-03-30T02:00:00.000Z", "failed"),
  );

  const records = await store.listExecutionRecords({ limit: 1 });

  assert.equal(records.length, 1);
  assert.equal(records[0].executionId, "exec-2");
});

test("FileStateStore reads older snapshots without execution history", async () => {
  const { stateFile } = await createStore();
  await fs.writeFile(stateFile, JSON.stringify({ sessions: [createSession()] }, null, 2), "utf8");

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();
  const sessions = await reloaded.listSessions();
  const records = await reloaded.listExecutionRecords();

  assert.equal(sessions.length, 1);
  assert.deepEqual(records, []);
});

test("FileStateStore persists channel default working directories across reloads", async () => {
  const { store, stateFile } = await createStore();
  await store.setChannelDefaultWorkingDirectory("slack", "C1", "C:/repo/alpha");
  await store.setChannelDefaultWorkingDirectory("mattermost", "C2", "C:/repo/beta");

  assert.equal(await store.getChannelDefaultWorkingDirectory("slack", "C1"), "C:/repo/alpha");
  assert.deepEqual(await store.listChannelDefaultWorkingDirectories(), {
    "slack:C1": "C:/repo/alpha",
    "mattermost:C2": "C:/repo/beta",
  });

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();
  assert.equal(await reloaded.getChannelDefaultWorkingDirectory("slack", "C1"), "C:/repo/alpha");

  await reloaded.clearChannelDefaultWorkingDirectory("slack", "C1");
  assert.equal(await reloaded.getChannelDefaultWorkingDirectory("slack", "C1"), undefined);
  assert.deepEqual(await reloaded.listChannelDefaultWorkingDirectories(), {
    "mattermost:C2": "C:/repo/beta",
  });
});

test("FileStateStore reads older snapshots without channel defaults", async () => {
  const { stateFile } = await createStore();
  await fs.writeFile(
    stateFile,
    JSON.stringify({ sessions: [createSession()], executionHistory: [] }, null, 2),
    "utf8",
  );

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();

  assert.deepEqual(await reloaded.listChannelDefaultWorkingDirectories(), {});
  assert.equal(await reloaded.getChannelDefaultWorkingDirectory("slack", "C1"), undefined);
  // Setting still works and does not disturb existing sessions.
  await reloaded.setChannelDefaultWorkingDirectory("slack", "C1", "C:/repo");
  assert.equal(await reloaded.getChannelDefaultWorkingDirectory("slack", "C1"), "C:/repo");
  assert.equal((await reloaded.listSessions()).length, 1);
});

test("FileStateStore recovers from truncated json without crashing", async () => {
  const { stateFile } = await createStore();
  await fs.writeFile(stateFile, '{"sessions":[', "utf8");

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();
  const sessions = await reloaded.listSessions();
  const records = await reloaded.listExecutionRecords();
  const dirEntries = await fs.readdir(path.dirname(stateFile));

  assert.deepEqual(sessions, []);
  assert.deepEqual(records, []);
  assert.ok(
    dirEntries.some((entry) => entry.startsWith("state.json.corrupt.") && entry.endsWith(".json")),
  );
});

test("FileStateStore serializes concurrent session, execution, and channel updates", async () => {
  const { store, stateFile } = await createStore();
  const sessions = Array.from({ length: 8 }, (_, index) => ({
    ...createSession(),
    sessionId: `session-${index}`,
    thread: { provider: "slack" as const, channelId: `C${index}`, threadId: `T${index}` },
  }));
  const records = Array.from({ length: 8 }, (_, index) =>
    createExecutionRecord(`exec-${index}`, `2026-03-30T01:${String(index).padStart(2, "0")}:00.000Z`, "completed"),
  );

  await Promise.all([
    ...sessions.map((session) => store.upsertSession(session)),
    ...records.map((record) => store.upsertExecutionRecord(record)),
    ...Array.from({ length: 8 }, (_, index) =>
      store.setChannelDefaultWorkingDirectory("slack", `C${index}`, `C:/repo/${index}`),
    ),
  ]);

  assert.deepEqual(
    (await store.listSessions()).map((session) => session.sessionId).sort(),
    sessions.map((session) => session.sessionId).sort(),
  );
  assert.deepEqual(
    (await store.listExecutionRecords()).map((record) => record.executionId).sort(),
    records.map((record) => record.executionId).sort(),
  );
  assert.equal(Object.keys(await store.listChannelDefaultWorkingDirectories()).length, 8);

  const reloaded = new FileStateStore(stateFile);
  await reloaded.initialize();
  assert.equal((await reloaded.listSessions()).length, 8);
  assert.equal((await reloaded.listExecutionRecords()).length, 8);
  assert.equal(Object.keys(await reloaded.listChannelDefaultWorkingDirectories()).length, 8);
});

test("FileStateStore rejects a mutation when atomic persistence fails", async () => {
  const { store } = await createStore();
  const originalRename = fs.rename;
  fs.rename = async () => {
    throw Object.assign(new Error("disk unavailable"), { code: "EIO" });
  };

  try {
    await assert.rejects(store.upsertSession(createSession()), /disk unavailable/);
  } finally {
    fs.rename = originalRename;
  }

  await store.upsertSession({ ...createSession(), sessionId: "session-after-recovery" });
  assert.deepEqual(
    (await store.listSessions()).map((session) => session.sessionId),
    ["session-after-recovery"],
  );
});
