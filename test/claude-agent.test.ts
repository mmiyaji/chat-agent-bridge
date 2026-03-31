import test from "node:test";
import assert from "node:assert/strict";
import {
  ClaudeCodeAgent,
  extractAssistantProgress,
  parseClaudeJsonLine,
} from "../src/agents/claude/claude-agent.js";
import { AppConfig } from "../src/core/types.js";

function createClaudeConfig(): AppConfig {
  return {
    chatProvider: "slack",
    agentKind: "claude-code",
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
    settingsFile: "./.test-tmp/claude-settings.json",
    stateFile: "C:/repo/data/state.json",
    auditLogFile: "C:/repo/data/audit.jsonl",
    codexSessionIndexFile: "C:/repo/data/session_index.jsonl",
    claudeSessionIndexFile: "C:/repo/data/claude_sessions.json",
    codexModel: "gpt-5.4",
    codexMaxTurns: 8,
    codexCommandTimeoutMs: 1000,
    codexMaxRuntimeMs: 10000,
    codexMaxOutputLength: 12000,
    claudeModel: "sonnet",
    claudeMaxTurns: 8,
    claudeCommandTimeoutMs: 1000,
    claudeMaxRuntimeMs: 10000,
    autoSplitThreadUserMessageCount: 30,
    agentRetryCount: 0,
  };
}

test("parseClaudeJsonLine reads a stream-json assistant payload", () => {
  const payload = parseClaudeJsonLine(
    JSON.stringify({
      type: "assistant",
      message: {
        content: [
          {
            type: "text",
            text: "Planning the next change.",
          },
        ],
      },
      session_id: "claude-session-1",
    }),
  );

  assert.equal(payload?.type, "assistant");
  assert.equal(payload?.session_id, "claude-session-1");
});

test("extractAssistantProgress condenses text and tool use blocks", () => {
  const progress = extractAssistantProgress({
    type: "assistant",
    message: {
      content: [
        {
          type: "text",
          text: "Inspecting the repository.",
        },
        {
          type: "tool_use",
          name: "Bash",
        },
      ],
    },
  });

  assert.equal(progress, "Inspecting the repository. | Using Bash");
});

test("extractAssistantProgress ignores non-assistant events", () => {
  const progress = extractAssistantProgress({
    type: "result",
    result: "done",
  });

  assert.equal(progress, undefined);
});

test("ClaudeCodeAgent maps approval policy to permission mode", () => {
  const agent = new ClaudeCodeAgent(createClaudeConfig());
  const reader = agent as unknown as {
    buildPermissionArgs: (request: { approvalPolicy: "manual" | "on-failure" | "never" }) => string[];
  };

  assert.deepEqual(reader.buildPermissionArgs({ approvalPolicy: "manual" }), [
    "--permission-mode",
    "plan",
  ]);
  assert.deepEqual(reader.buildPermissionArgs({ approvalPolicy: "on-failure" }), [
    "--permission-mode",
    "acceptEdits",
  ]);
  assert.deepEqual(reader.buildPermissionArgs({ approvalPolicy: "never" }), [
    "--dangerously-skip-permissions",
  ]);
});

test("ClaudeCodeAgent includes verbose with stream-json print mode", () => {
  const agent = new ClaudeCodeAgent(createClaudeConfig());
  const reader = agent as unknown as {
    buildExecArgs: (request: {
      session: { workingDirectory: string };
      prompt: string;
      approvalPolicy: "manual" | "on-failure" | "never";
    }) => string[];
  };

  const args = reader.buildExecArgs({
    session: { workingDirectory: "C:/repo" },
    prompt: "do work",
    approvalPolicy: "manual",
  });

  assert.equal(args[0], "-p");
  assert.equal(args[1], "--verbose");
  assert.match(args.join(" "), /--output-format stream-json/);
});
