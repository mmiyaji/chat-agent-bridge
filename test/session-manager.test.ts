import test from "node:test";
import assert from "node:assert/strict";
import { SessionManager } from "../src/core/session-manager.js";
import { InMemoryStateStore } from "./helpers/fakes.js";

test("SessionManager creates and reuses a binding for the same thread", async () => {
  const store = new InMemoryStateStore();
  const manager = new SessionManager(store);
  const first = await manager.getOrCreate({
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    allowedDirectories: ["C:/repo"],
  });

  const second = await manager.getOrCreate({
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    allowedDirectories: ["C:/repo"],
  });

  assert.equal(second.sessionId, first.sessionId);
  assert.equal(second.workingDirectory, first.workingDirectory);
  assert.ok(Date.parse(second.updatedAt) >= Date.parse(first.updatedAt));
});

test("SessionManager rejects a different working directory for the same thread", async () => {
  const store = new InMemoryStateStore();
  const manager = new SessionManager(store);

  await manager.getOrCreate({
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project-a",
    allowedDirectories: ["C:/repo"],
  });

  await assert.rejects(
    manager.getOrCreate({
      agent: "codex",
      thread: {
        provider: "slack",
        channelId: "C1",
        threadId: "T1",
      },
      workingDirectory: "C:/repo/project-b",
      allowedDirectories: ["C:/repo"],
    }),
    /already bound/,
  );
});

test("SessionManager normalizes equivalent paths", async () => {
  const store = new InMemoryStateStore();
  const manager = new SessionManager(store);

  const first = await manager.getOrCreate({
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project",
    allowedDirectories: ["C:/repo"],
  });

  const second = await manager.getOrCreate({
    agent: "codex",
    thread: {
      provider: "slack",
      channelId: "C1",
      threadId: "T1",
    },
    workingDirectory: "C:/repo/project/.",
    allowedDirectories: ["C:/repo"],
  });

  assert.equal(second.sessionId, first.sessionId);
});
