import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { CodexAgent } from "../src/agents/codex/codex-agent.js";
import { AgentExecutionCancelledError } from "../src/agents/base.js";
import { AppConfig } from "../src/core/types.js";

function createCodexConfig(): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "codex",
    allowedDirectories: ["C:/repo"],
    repoAliases: {
      repo: "C:/repo",
    },
    defaultApprovalPolicy: "manual",
    networkAccess: "disabled",
    slackMode: "http",
    slackAllowedUserIds: [],
    mattermostMode: "http",
    mattermostAllowedUserIds: [],
    port: 8787,
    host: "127.0.0.1",
    defaultWorkingDirectory: "C:/repo",
    settingsFile: "./.test-tmp/codex-settings.json",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 60_000,
    codexMaxRuntimeMs: 60_000,
    codexMaxOutputLength: 12000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

test("CodexAgent resolves cancellation even if the child process does not exit immediately", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-codex-agent-"));
  const fakeCodexPath = path.join(tempRoot, "codex.cmd");
  await fs.writeFile(
    fakeCodexPath,
    [
      "@echo off",
      "echo {\"type\":\"thread.started\",\"thread_id\":\"fake-thread\"}",
      "powershell -NoProfile -Command \"Start-Sleep -Seconds 30\"",
    ].join("\r\n"),
    "utf8",
  );

  const originalPath = process.env.PATH;
  process.env.PATH = `${tempRoot}${path.delimiter}${originalPath ?? ""}`;

  try {
    const agent = new CodexAgent(createCodexConfig());
    const executePromise = agent.execute({
      session: {
        sessionId: "session-1",
        thread: {
          provider: "slack",
          channelId: "C1",
          threadId: "T1",
        },
        agent: "codex",
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
    process.env.PATH = originalPath;
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
});
