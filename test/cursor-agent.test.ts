import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { CursorAgent } from "../src/agents/cursor/cursor-agent.js";
import { AgentExecutionCancelledError } from "../src/agents/base.js";
import { AppConfig } from "../src/core/types.js";

function createCursorConfig(command: string): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "cursor",
    allowedDirectories: ["C:/repo"],
    repoAliases: {
      repo: "C:/repo",
    },
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    mattermostMode: "http",
    port: 8787,
    host: "127.0.0.1",
    defaultWorkingDirectory: "C:/repo",
    settingsFile: "./.test-tmp/cursor-settings.json",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 60_000,
    codexMaxRuntimeMs: 60_000,
    codexMaxOutputLength: 12000,
    cursorCommand: command,
    cursorCommandTimeoutMs: 60_000,
    cursorMaxRuntimeMs: 60_000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

test("CursorAgent returns stdout as the final summary", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-cursor-agent-"));
  const fakeCursorPath = path.join(tempRoot, "cursor.cmd");
  await fs.writeFile(
    fakeCursorPath,
    [
      "@echo off",
      "setlocal",
      "set /p PROMPT=",
      "echo Cursor handled: %PROMPT%",
    ].join("\r\n"),
    "utf8",
  );

  const agent = new CursorAgent(createCursorConfig(`"${fakeCursorPath}"`));
  try {
    const result = await agent.execute({
      session: {
        sessionId: "session-1",
        thread: {
          provider: "slack",
          channelId: "C1",
          threadId: "T1",
        },
        agent: "cursor",
        workingDirectory: tempRoot,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
      prompt: "summarize",
      approvalPolicy: "never",
      networkAccess: "enabled",
      allowedDirectories: [tempRoot],
    });

    assert.match(result.summary, /Cursor handled: summarize/i);
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
});

test("CursorAgent resolves cancellation even if the child process does not exit immediately", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-cursor-agent-cancel-"));
  const fakeCursorPath = path.join(tempRoot, "cursor.cmd");
  await fs.writeFile(
    fakeCursorPath,
    [
      "@echo off",
      "echo Cursor starting",
      "powershell -NoProfile -Command \"Start-Sleep -Seconds 30\"",
    ].join("\r\n"),
    "utf8",
  );

  const agent = new CursorAgent(createCursorConfig(`"${fakeCursorPath}"`));
  try {
    const executePromise = agent.execute({
      session: {
        sessionId: "session-1",
        thread: {
          provider: "slack",
          channelId: "C1",
          threadId: "T1",
        },
        agent: "cursor",
        workingDirectory: tempRoot,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
      prompt: "long running",
      approvalPolicy: "never",
      networkAccess: "enabled",
      allowedDirectories: [tempRoot],
    });

    await new Promise((resolve) => setTimeout(resolve, 200));
    const accepted = await agent.cancelExecution("session-1");
    assert.equal(accepted, true);

    await assert.rejects(executePromise, AgentExecutionCancelledError);
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
});
