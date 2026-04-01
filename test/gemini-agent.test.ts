import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { GeminiAgent } from "../src/agents/gemini/gemini-agent.js";
import { AgentExecutionCancelledError } from "../src/agents/base.js";
import { AppConfig } from "../src/core/types.js";

function createGeminiConfig(command: string): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "gemini",
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
    settingsFile: "./.test-tmp/gemini-settings.json",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 60_000,
    codexMaxRuntimeMs: 60_000,
    codexMaxOutputLength: 12000,
    geminiCommand: command,
    geminiModel: "gemini-2.5-pro",
    geminiCommandTimeoutMs: 60_000,
    geminiMaxRuntimeMs: 60_000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

test("GeminiAgent returns stdout as the final summary", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-gemini-agent-"));
  const fakeGeminiPath = path.join(tempRoot, "gemini.cmd");
  await fs.writeFile(
    fakeGeminiPath,
    [
      "@echo off",
      "setlocal",
      "set /p PROMPT=",
      "echo Gemini handled: %PROMPT%",
    ].join("\r\n"),
    "utf8",
  );

  const agent = new GeminiAgent(createGeminiConfig(`\"${fakeGeminiPath}\"`));
  try {
    const result = await agent.execute({
      session: {
        sessionId: "session-1",
        thread: {
          provider: "slack",
          channelId: "C1",
          threadId: "T1",
        },
        agent: "gemini",
        workingDirectory: tempRoot,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
      prompt: "summarize",
      approvalPolicy: "never",
      networkAccess: "enabled",
      allowedDirectories: [tempRoot],
    });

    assert.match(result.summary, /Gemini handled: summarize/i);
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
});

test("GeminiAgent resolves cancellation even if the child process does not exit immediately", async () => {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "cab-gemini-agent-cancel-"));
  const fakeGeminiPath = path.join(tempRoot, "gemini.cmd");
  await fs.writeFile(
    fakeGeminiPath,
    [
      "@echo off",
      "echo Gemini starting",
      "powershell -NoProfile -Command \"Start-Sleep -Seconds 30\"",
    ].join("\r\n"),
    "utf8",
  );

  const agent = new GeminiAgent(createGeminiConfig(`\"${fakeGeminiPath}\"`));
  try {
    const executePromise = agent.execute({
      session: {
        sessionId: "session-1",
        thread: {
          provider: "slack",
          channelId: "C1",
          threadId: "T1",
        },
        agent: "gemini",
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
