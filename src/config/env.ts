import fs from "node:fs";
import path from "node:path";
import {
  AgentKind,
  ApprovalPolicy,
  AppConfig,
  ChatIngress,
  MattermostActorMode,
  MattermostMode,
  NetworkAccess,
  SlackMode,
} from "../core/types.js";

function readRequired(name: string, env: NodeJS.ProcessEnv): string {
  const value = env[name];
  if (!value) {
    throw new Error(`Missing required environment variable: ${name}`);
  }

  return value;
}

function parseApprovalPolicy(value: string | undefined): ApprovalPolicy {
  switch (value) {
    case undefined:
    case "":
    case "manual":
      return "manual";
    case "on-failure":
    case "never":
      return value;
    default:
      throw new Error(`Invalid CAB_DEFAULT_APPROVAL_POLICY: ${value}`);
  }
}

function parseNetworkAccess(value: string | undefined): NetworkAccess {
  switch (value) {
    case undefined:
    case "":
    case "enabled":
      return "enabled";
    case "disabled":
      return "disabled";
    default:
      throw new Error(`Invalid CAB_NETWORK_ACCESS: ${value}`);
  }
}

function parseSlackMode(value: string | undefined): SlackMode {
  switch (value) {
    case undefined:
    case "":
    case "http":
      return "http";
    case "socket":
      return value;
    default:
      throw new Error(`Invalid CAB_SLACK_MODE: ${value}`);
  }
}

function parseMattermostMode(value: string | undefined): MattermostMode {
  switch (value) {
    case undefined:
    case "":
    case "http":
      return "http";
    case "websocket":
      return value;
    default:
      throw new Error(`Invalid MATTERMOST_MODE: ${value}`);
  }
}

function parseMattermostActorMode(value: string | undefined): MattermostActorMode {
  switch (value) {
    case undefined:
    case "":
    case "bot":
      return "bot";
    case "self":
      return value;
    default:
      throw new Error(`Invalid MATTERMOST_ACTOR_MODE: ${value}`);
  }
}

function parseChatProvider(value: string | undefined): ChatIngress {
  switch (value) {
    case undefined:
    case "":
    case "slack":
      return "slack";
    case "mattermost":
      return value;
    default:
      throw new Error(`Invalid CAB_CHAT_PROVIDER: ${value}`);
  }
}

function parseAgentKind(value: string | undefined): AgentKind {
  switch (value) {
    case undefined:
    case "":
    case "codex":
      return "codex";
    case "claude-code":
      return value;
    default:
      throw new Error(`Invalid CAB_AGENT: ${value}`);
  }
}

function parseAllowedDirs(value: string): string[] {
  const items = value
    .split(";")
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .map((entry) => path.resolve(entry));

  if (items.length === 0) {
    throw new Error("CAB_ALLOWED_DIRS must include at least one directory.");
  }

  return items;
}

function parseRepoAliases(
  value: string | undefined,
  allowedDirectories: string[],
): Record<string, string> {
  const aliases: Record<string, string> = {};

  for (const directory of allowedDirectories) {
    const basename = path.basename(directory).toLowerCase();
    if (!(basename in aliases)) {
      aliases[basename] = directory;
    }
  }

  if (!value) {
    return aliases;
  }

  for (const entry of value.split(";")) {
    const trimmed = entry.trim();
    if (trimmed.length === 0) {
      continue;
    }

    const [rawAlias, rawPath] = trimmed.split("=", 2);
    const alias = rawAlias?.trim().toLowerCase();
    const target = rawPath?.trim();
    if (!alias || !target) {
      throw new Error(`Invalid CAB_REPO_ALIASES entry: ${trimmed}`);
    }

    aliases[alias] = path.resolve(target);
  }

  return aliases;
}

function parseInteger(name: string, value: string | undefined, fallback: number): number {
  if (!value) {
    return fallback;
  }

  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    throw new Error(`${name} must be a positive integer.`);
  }

  return parsed;
}

function parseBoolean(value: string | undefined): boolean {
  switch ((value ?? "").trim().toLowerCase()) {
    case "":
    case "0":
    case "false":
    case "no":
      return false;
    case "1":
    case "true":
    case "yes":
      return true;
    default:
      throw new Error(`Invalid boolean value: ${value}`);
  }
}

function readPersistedSettings(settingsFile: string): Record<string, string | number | boolean> {
  if (!fs.existsSync(settingsFile)) {
    return {};
  }

  const raw = fs.readFileSync(settingsFile, "utf8");
  const parsed = JSON.parse(raw);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    return {};
  }

  const result: Record<string, string | number | boolean> = {};
  for (const [key, value] of Object.entries(parsed)) {
    if (
      typeof value === "string" ||
      typeof value === "number" ||
      typeof value === "boolean"
    ) {
      result[key] = value;
    }
  }

  return result;
}

function readSettingString(
  envValue: string | undefined,
  persisted: Record<string, string | number | boolean>,
  key: string,
): string | undefined {
  if (envValue !== undefined && envValue !== "") {
    return envValue;
  }
  const value = persisted[key];
  return typeof value === "string" ? value : undefined;
}

function readSettingInteger(
  envValue: string | undefined,
  persisted: Record<string, string | number | boolean>,
  key: string,
): string | undefined {
  if (envValue !== undefined && envValue !== "") {
    return envValue;
  }
  const value = persisted[key];
  if (typeof value === "number") {
    return String(value);
  }
  return typeof value === "string" ? value : undefined;
}

function readSettingBoolean(
  envValue: string | undefined,
  persisted: Record<string, string | number | boolean>,
  key: string,
): string | undefined {
  if (envValue !== undefined && envValue !== "") {
    return envValue;
  }
  const value = persisted[key];
  if (typeof value === "boolean") {
    return value ? "true" : "false";
  }
  return typeof value === "string" ? value : undefined;
}

export function createAppConfig(env: NodeJS.ProcessEnv): AppConfig {
  const settingsFile = path.resolve(env.CAB_SETTINGS_FILE ?? "./data/admin-settings.json");
  const persisted = readPersistedSettings(settingsFile);
  const allowedDirectories = parseAllowedDirs(readRequired("CAB_ALLOWED_DIRS", env));
  const defaultAllowedDirectory = allowedDirectories[0];
  if (!defaultAllowedDirectory) {
    throw new Error("CAB_ALLOWED_DIRS must include at least one directory.");
  }

  const config: AppConfig = {
    chatProvider: parseChatProvider(readSettingString(env.CAB_CHAT_PROVIDER, persisted, "chatProvider")),
    agentKind: parseAgentKind(readSettingString(env.CAB_AGENT, persisted, "agentKind")),
    allowedDirectories,
    repoAliases: parseRepoAliases(env.CAB_REPO_ALIASES, allowedDirectories),
    defaultApprovalPolicy: parseApprovalPolicy(
      readSettingString(env.CAB_DEFAULT_APPROVAL_POLICY, persisted, "defaultApprovalPolicy"),
    ),
    networkAccess: parseNetworkAccess(
      readSettingString(env.CAB_NETWORK_ACCESS, persisted, "networkAccess"),
    ),
    slackMode: parseSlackMode(readSettingString(env.CAB_SLACK_MODE, persisted, "slackMode")),
    mattermostMode: parseMattermostMode(
      readSettingString(env.MATTERMOST_MODE, persisted, "mattermostMode"),
    ),
    mattermostActorMode: parseMattermostActorMode(
      readSettingString(env.MATTERMOST_ACTOR_MODE, persisted, "mattermostActorMode"),
    ),
    port: parseInteger("CAB_PORT", readSettingInteger(env.CAB_PORT, persisted, "port"), 8787),
    host: readSettingString(env.CAB_HOST, persisted, "host") ?? "0.0.0.0",
    adminPort: parseInteger(
      "CAB_ADMIN_PORT",
      readSettingInteger(env.CAB_ADMIN_PORT, persisted, "adminPort"),
      8788,
    ),
    adminHost:
      readSettingString(env.CAB_ADMIN_HOST, persisted, "adminHost") ??
      readSettingString(env.CAB_HOST, persisted, "host") ??
      "0.0.0.0",
    defaultWorkingDirectory: path.resolve(
      readSettingString(env.CAB_DEFAULT_WORKDIR, persisted, "defaultWorkingDirectory") ??
        defaultAllowedDirectory,
    ),
    settingsFile,
    stateFile: path.resolve(env.CAB_STATE_FILE ?? "./data/state.json"),
    auditLogFile: path.resolve(env.CAB_AUDIT_LOG_FILE ?? "./data/audit.jsonl"),
    codexSessionIndexFile: path.resolve(
      env.CAB_CODEX_SESSION_INDEX_FILE ?? "C:/Users/mail/.codex/session_index.jsonl",
    ),
    claudeSessionIndexFile: path.resolve(
      env.CAB_CLAUDE_SESSION_INDEX_FILE ?? "C:/Users/mail/.config/claude/sessions/sessions.json",
    ),
    codexModel: readSettingString(env.CAB_CODEX_MODEL, persisted, "codexModel") ?? "gpt-5.4",
    codexMaxTurns: parseInteger(
      "CAB_CODEX_MAX_TURNS",
      readSettingInteger(env.CAB_CODEX_MAX_TURNS, persisted, "codexMaxTurns"),
      8,
    ),
    codexCommandTimeoutMs: parseInteger(
      "CAB_CODEX_COMMAND_TIMEOUT_MS",
      readSettingInteger(
        env.CAB_CODEX_COMMAND_TIMEOUT_MS,
        persisted,
        "codexCommandTimeoutMs",
      ),
      600000,
    ),
    codexMaxRuntimeMs: parseInteger(
      "CAB_CODEX_MAX_RUNTIME_MS",
      readSettingInteger(env.CAB_CODEX_MAX_RUNTIME_MS, persisted, "codexMaxRuntimeMs"),
      3600000,
    ),
    codexMaxOutputLength: parseInteger(
      "CAB_CODEX_MAX_OUTPUT_LENGTH",
      readSettingInteger(
        env.CAB_CODEX_MAX_OUTPUT_LENGTH,
        persisted,
        "codexMaxOutputLength",
      ),
      12000,
    ),
    claudeModel: readSettingString(env.CAB_CLAUDE_MODEL, persisted, "claudeModel") ?? "sonnet",
    claudeMaxTurns: parseInteger(
      "CAB_CLAUDE_MAX_TURNS",
      readSettingInteger(env.CAB_CLAUDE_MAX_TURNS, persisted, "claudeMaxTurns"),
      8,
    ),
    claudeCommandTimeoutMs: parseInteger(
      "CAB_CLAUDE_COMMAND_TIMEOUT_MS",
      readSettingInteger(
        env.CAB_CLAUDE_COMMAND_TIMEOUT_MS,
        persisted,
        "claudeCommandTimeoutMs",
      ),
      600000,
    ),
    claudeMaxRuntimeMs: parseInteger(
      "CAB_CLAUDE_MAX_RUNTIME_MS",
      readSettingInteger(env.CAB_CLAUDE_MAX_RUNTIME_MS, persisted, "claudeMaxRuntimeMs"),
      3600000,
    ),
    autoSplitThreadUserMessageCount: parseInteger(
      "CAB_AUTO_SPLIT_THREAD_USER_MESSAGE_COUNT",
      readSettingInteger(
        env.CAB_AUTO_SPLIT_THREAD_USER_MESSAGE_COUNT,
        persisted,
        "autoSplitThreadUserMessageCount",
      ),
      30,
    ),
  };

  {
    const value = readSettingString(env.SLACK_BOT_TOKEN, persisted, "slackBotToken");
    if (value) {
      config.slackBotToken = value;
    }
  }

  {
    const value = readSettingString(env.SLACK_APP_TOKEN, persisted, "slackAppToken");
    if (value) {
      config.slackAppToken = value;
    }
  }

  {
    const value = readSettingString(
      env.SLACK_SIGNING_SECRET,
      persisted,
      "slackSigningSecret",
    );
    if (value) {
      config.slackSigningSecret = value;
    }
  }

  {
    const value = readSettingString(
      env.MATTERMOST_SERVER_URL,
      persisted,
      "mattermostServerUrl",
    );
    if (value) {
      config.mattermostServerUrl = value;
    }
  }

  {
    const value = readSettingString(
      env.MATTERMOST_BOT_TOKEN,
      persisted,
      "mattermostBotToken",
    );
    if (value) {
      config.mattermostBotToken = value;
    }
  }

  {
    const value = readSettingString(
      env.MATTERMOST_WEBHOOK_TOKEN,
      persisted,
      "mattermostWebhookToken",
    );
    if (value) {
      config.mattermostWebhookToken = value;
    }
  }

  {
    const value = readSettingString(
      env.MATTERMOST_ACTION_BASE_URL,
      persisted,
      "mattermostActionBaseUrl",
    );
    if (value) {
      config.mattermostActionBaseUrl = value;
    }
  }

  {
    const value = readSettingBoolean(env.MATTERMOST_DM_ONLY, persisted, "mattermostDmOnly");
    if (value) {
      config.mattermostDmOnly = parseBoolean(value);
      if (config.mattermostDmOnly && !readSettingString(env.MATTERMOST_ACTOR_MODE, persisted, "mattermostActorMode")) {
        config.mattermostActorMode = "self";
      }
    }
  }

  return config;
}
