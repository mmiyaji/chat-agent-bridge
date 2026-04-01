import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createAppConfig } from "../src/config/env.js";

const tempRoot = path.resolve("./.test-tmp");

test("createAppConfig reads persisted settings when env values are absent", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings.json");
  fs.writeFileSync(
    settingsFile,
    JSON.stringify(
      {
        agentKind: "claude-code",
        defaultApprovalPolicy: "never",
        networkAccess: "disabled",
        chatProvider: "mattermost",
        mattermostMode: "websocket",
        mattermostServerUrl: "https://chat.example.test",
        slackBotToken: "persisted-token",
        codexModel: "gpt-5.5",
        codexMaxTurns: 11,
        autoSplitThreadUserMessageCount: 45,
      },
      null,
      2,
    ),
    "utf8",
  );

  const config = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
  });

  assert.equal(config.agentKind, "claude-code");
  assert.equal(config.defaultApprovalPolicy, "never");
  assert.equal(config.networkAccess, "disabled");
  assert.equal(config.chatProvider, "mattermost");
  assert.equal(config.mattermostMode, "websocket");
  assert.equal(config.mattermostServerUrl, "https://chat.example.test");
  assert.equal(config.slackBotToken, "persisted-token");
  assert.equal(config.codexModel, "gpt-5.5");
  assert.equal(config.codexMaxTurns, 11);
  assert.equal(config.autoSplitThreadUserMessageCount, 45);
});

test("createAppConfig lets env override persisted settings", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings-override.json");
  fs.writeFileSync(
    settingsFile,
    JSON.stringify(
      {
        agentKind: "claude-code",
        chatProvider: "mattermost",
        slackBotToken: "persisted-token",
        codexModel: "gpt-5.5",
        autoSplitThreadUserMessageCount: 45,
      },
      null,
      2,
    ),
    "utf8",
  );

  const config = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
    CAB_AGENT: "codex",
    CAB_CHAT_PROVIDER: "slack",
    SLACK_BOT_TOKEN: "env-token",
    CAB_CODEX_MODEL: "gpt-5.4",
    CAB_AUTO_SPLIT_THREAD_USER_MESSAGE_COUNT: "30",
  });

  assert.equal(config.agentKind, "codex");
  assert.equal(config.chatProvider, "slack");
  assert.equal(config.slackBotToken, "env-token");
  assert.equal(config.codexModel, "gpt-5.4");
  assert.equal(config.autoSplitThreadUserMessageCount, 30);
});

test("createAppConfig reads Mattermost actor mode and maps legacy dm-only to self", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings-mattermost.json");
  fs.rmSync(settingsFile, { force: true });

  const explicit = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
    MATTERMOST_ACTOR_MODE: "self",
  });
  assert.equal(explicit.mattermostActorMode, "self");

  const legacy = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
    MATTERMOST_DM_ONLY: "true",
  });
  assert.equal(legacy.mattermostActorMode, "self");
  assert.equal(legacy.mattermostDmOnly, true);
});

test("createAppConfig accepts cursor as the default agent", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings-cursor.json");
  fs.rmSync(settingsFile, { force: true });

  const config = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
    CAB_AGENT: "cursor",
    CAB_CURSOR_COMMAND: "cursor-agent",
  });

  assert.equal(config.agentKind, "cursor");
  assert.equal(config.cursorCommand, "cursor-agent --trust");
  assert.equal(config.cursorModel, "Auto");
});

test("createAppConfig accepts gemini as the default agent", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings-gemini.json");
  fs.rmSync(settingsFile, { force: true });

  const config = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
    CAB_AGENT: "gemini",
    CAB_GEMINI_COMMAND: "gemini --approval-mode yolo",
    CAB_GEMINI_MODEL: "gemini-2.5-pro",
  });

  assert.equal(config.agentKind, "gemini");
  assert.equal(config.geminiCommand, "gemini --approval-mode yolo");
  assert.equal(config.geminiModel, "gemini-2.5-pro");
});

test("createAppConfig normalizes the legacy cursor agent default command", () => {
  fs.mkdirSync(tempRoot, { recursive: true });
  const settingsFile = path.join(tempRoot, "env-settings-cursor-legacy.json");
  fs.writeFileSync(
    settingsFile,
    JSON.stringify(
      {
        cursorCommand: "cursor agent",
      },
      null,
      2,
    ),
    "utf8",
  );

  const config = createAppConfig({
    CAB_ALLOWED_DIRS: "C:/repo",
    CAB_SETTINGS_FILE: settingsFile,
  });

  assert.equal(config.cursorCommand, "cursor-agent --trust");
  assert.equal(config.cursorModel, "Auto");
});
