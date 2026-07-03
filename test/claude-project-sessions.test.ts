import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import {
  encodeClaudeProjectDir,
  readClaudeProjectSessions,
} from "../src/agents/claude/project-sessions.js";

async function setMtime(filePath: string, epochSeconds: number): Promise<void> {
  await fs.utimes(filePath, epochSeconds, epochSeconds);
}

test("encodeClaudeProjectDir replaces non-alphanumeric characters with dashes", () => {
  const encoded = encodeClaudeProjectDir("C:/Users/mail/Documents/git/chat-agent-bridge");
  assert.match(encoded, /^[A-Za-z0-9-]+$/);
  assert.ok(encoded.includes("chat-agent-bridge"));
});

test("readClaudeProjectSessions returns [] when the root is missing", async () => {
  const missing = path.join(os.tmpdir(), "cab-projects-missing-does-not-exist");
  const sessions = await readClaudeProjectSessions(missing, "C:/some/where");
  assert.deepEqual(sessions, []);
});

test("readClaudeProjectSessions reads, orders, labels, and limits sessions", async () => {
  const projectsRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-projects-"));
  const workingDirectory = "C:/Users/tester/Repo";
  const encoded = encodeClaudeProjectDir(workingDirectory);
  const lowerFirst = encoded.charAt(0).toLowerCase() + encoded.slice(1);
  const projectFolder = path.join(projectsRoot, lowerFirst);
  await fs.mkdir(projectFolder, { recursive: true });

  const summaryFile = path.join(projectFolder, "11111111-1111-1111-1111-111111111111.jsonl");
  await fs.writeFile(
    summaryFile,
    [
      JSON.stringify({ type: "summary", summary: "Refactor the parser module" }),
      JSON.stringify({ type: "user", message: { content: "later line" } }),
    ].join("\n"),
    "utf8",
  );

  const userStringFile = path.join(projectFolder, "22222222-2222-2222-2222-222222222222.jsonl");
  await fs.writeFile(
    userStringFile,
    [
      JSON.stringify({ type: "queue-operation", content: "ignored" }),
      JSON.stringify({ type: "user", message: { content: "Fix the failing tests" } }),
    ].join("\n"),
    "utf8",
  );

  const userBlockFile = path.join(projectFolder, "33333333-3333-3333-3333-333333333333.jsonl");
  await fs.writeFile(
    userBlockFile,
    [
      JSON.stringify({
        type: "user",
        message: {
          content: [
            { type: "image", source: {} },
            { type: "text", text: "Add a new endpoint for exports" },
          ],
        },
      }),
    ].join("\n"),
    "utf8",
  );

  const zeroByteFile = path.join(projectFolder, "44444444-4444-4444-4444-444444444444.jsonl");
  await fs.writeFile(zeroByteFile, "", "utf8");

  const decoySubdir = path.join(projectFolder, "55555555-5555-5555-5555-555555555555");
  await fs.mkdir(decoySubdir, { recursive: true });
  await fs.writeFile(path.join(decoySubdir, "inner.jsonl"), "{}", "utf8");

  await setMtime(summaryFile, 3000);
  await setMtime(userStringFile, 2000);
  await setMtime(userBlockFile, 1000);

  const sessions = await readClaudeProjectSessions(projectsRoot, workingDirectory);
  assert.equal(sessions.length, 3);
  assert.deepEqual(
    sessions.map((entry) => entry.id),
    [
      "11111111-1111-1111-1111-111111111111",
      "22222222-2222-2222-2222-222222222222",
      "33333333-3333-3333-3333-333333333333",
    ],
  );
  assert.equal(sessions[0]?.label, "Refactor the parser module");
  assert.equal(sessions[1]?.label, "Fix the failing tests");
  assert.equal(sessions[2]?.label, "Add a new endpoint for exports");
  assert.equal(sessions[0]?.updatedAt, new Date(3000 * 1000).toISOString());

  const limited = await readClaudeProjectSessions(projectsRoot, workingDirectory, 2);
  assert.equal(limited.length, 2);
  assert.equal(limited[0]?.id, "11111111-1111-1111-1111-111111111111");
  assert.equal(limited[1]?.id, "22222222-2222-2222-2222-222222222222");

  await fs.rm(projectsRoot, { recursive: true, force: true });
});

test("readClaudeProjectSessions truncates long labels to 80 characters", async () => {
  const projectsRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-projects-label-"));
  const workingDirectory = "C:/Users/tester/LongLabel";
  const projectFolder = path.join(projectsRoot, encodeClaudeProjectDir(workingDirectory));
  await fs.mkdir(projectFolder, { recursive: true });

  const longText = "a".repeat(200);
  const file = path.join(projectFolder, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl");
  await fs.writeFile(file, JSON.stringify({ type: "summary", summary: longText }), "utf8");

  const sessions = await readClaudeProjectSessions(projectsRoot, workingDirectory);
  assert.equal(sessions.length, 1);
  assert.equal(sessions[0]?.label?.length, 80);
  assert.ok(sessions[0]?.label?.endsWith("..."));

  await fs.rm(projectsRoot, { recursive: true, force: true });
});
