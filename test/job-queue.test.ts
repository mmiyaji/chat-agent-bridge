import test from "node:test";
import assert from "node:assert/strict";
import { InMemoryJobQueue } from "../src/core/job-queue.js";

test("InMemoryJobQueue serializes jobs per session", async () => {
  const queue = new InMemoryJobQueue();
  const order: string[] = [];

  const slow = queue.enqueue("S1", async () => {
    await new Promise((resolve) => setTimeout(resolve, 30));
    order.push("first");
  });

  const fast = queue.enqueue("S1", async () => {
    order.push("second");
  });

  await Promise.all([slow, fast]);
  assert.deepEqual(order, ["first", "second"]);
});

test("InMemoryJobQueue allows concurrent jobs for different sessions", async () => {
  const queue = new InMemoryJobQueue();
  let running = 0;
  let maxRunning = 0;

  const run = (sessionId: string) =>
    queue.enqueue(sessionId, async () => {
      running += 1;
      maxRunning = Math.max(maxRunning, running);
      await new Promise((resolve) => setTimeout(resolve, 20));
      running -= 1;
    });

  await Promise.all([run("S1"), run("S2")]);
  assert.equal(maxRunning, 2);
});

test("InMemoryJobQueue continues after a rejected job", async () => {
  const queue = new InMemoryJobQueue();
  const order: string[] = [];

  await assert.rejects(
    queue.enqueue("S1", async () => {
      order.push("failed");
      throw new Error("boom");
    }),
  );

  await queue.enqueue("S1", async () => {
    order.push("after");
  });

  assert.deepEqual(order, ["failed", "after"]);
});
