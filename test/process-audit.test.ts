import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { writeProcessAudit } from "../src/core/process-audit.js";

test("writeProcessAudit appends a JSONL record", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-process-audit-"));
  const auditFile = path.join(tempRoot, "audit.jsonl");

  writeProcessAudit(auditFile, "bridge.process.test", {
    pid: 123,
    signal: "SIGTERM",
  });

  const contents = await fs.readFile(auditFile, "utf8");
  const line = contents.trim();
  assert.ok(line.length > 0);

  const parsed = JSON.parse(line) as {
    timestamp: string;
    type: string;
    pid: number;
    signal: string;
  };

  assert.equal(parsed.type, "bridge.process.test");
  assert.equal(parsed.pid, 123);
  assert.equal(parsed.signal, "SIGTERM");
  assert.match(parsed.timestamp, /^\d{4}-\d{2}-\d{2}T/);
});
