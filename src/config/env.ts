import path from "node:path";
import {
  AgentKind,
  ApprovalPolicy,
  AppConfig,
  ChatIngress,
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

export function createAppConfig(env: NodeJS.ProcessEnv): AppConfig {
  const allowedDirectories = parseAllowedDirs(readRequired("CAB_ALLOWED_DIRS", env));
  const defaultAllowedDirectory = allowedDirectories[0];
  if (!defaultAllowedDirectory) {
    throw new Error("CAB_ALLOWED_DIRS must include at least one directory.");
  }

  const config: AppConfig = {
    chatProvider: parseChatProvider(env.CAB_CHAT_PROVIDER),
    agentKind: parseAgentKind(env.CAB_AGENT),
    allowedDirectories,
    repoAliases: parseRepoAliases(env.CAB_REPO_ALIASES, allowedDirectories),
    defaultApprovalPolicy: parseApprovalPolicy(env.CAB_DEFAULT_APPROVAL_POLICY),
    networkAccess: parseNetworkAccess(env.CAB_NETWORK_ACCESS),
    slackMode: parseSlackMode(env.CAB_SLACK_MODE),
    mattermostMode: parseMattermostMode(env.MATTERMOST_MODE),
    port: parseInteger("CAB_PORT", env.CAB_PORT, 8787),
    host: env.CAB_HOST ?? "0.0.0.0",
    defaultWorkingDirectory: path.resolve(
      env.CAB_DEFAULT_WORKDIR ?? defaultAllowedDirectory,
    ),
    stateFile: path.resolve(env.CAB_STATE_FILE ?? "./data/state.json"),
    auditLogFile: path.resolve(env.CAB_AUDIT_LOG_FILE ?? "./data/audit.jsonl"),
    codexSessionIndexFile: path.resolve(
      env.CAB_CODEX_SESSION_INDEX_FILE ?? "C:/Users/mail/.codex/session_index.jsonl",
    ),
    claudeSessionIndexFile: path.resolve(
      env.CAB_CLAUDE_SESSION_INDEX_FILE ?? "C:/Users/mail/.config/claude/sessions/sessions.json",
    ),
    codexModel: env.CAB_CODEX_MODEL ?? "gpt-5.4",
    codexMaxTurns: parseInteger("CAB_CODEX_MAX_TURNS", env.CAB_CODEX_MAX_TURNS, 8),
    codexCommandTimeoutMs: parseInteger(
      "CAB_CODEX_COMMAND_TIMEOUT_MS",
      env.CAB_CODEX_COMMAND_TIMEOUT_MS,
      600000,
    ),
    codexMaxRuntimeMs: parseInteger(
      "CAB_CODEX_MAX_RUNTIME_MS",
      env.CAB_CODEX_MAX_RUNTIME_MS,
      3600000,
    ),
    codexMaxOutputLength: parseInteger(
      "CAB_CODEX_MAX_OUTPUT_LENGTH",
      env.CAB_CODEX_MAX_OUTPUT_LENGTH,
      12000,
    ),
    claudeModel: env.CAB_CLAUDE_MODEL ?? "sonnet",
    claudeMaxTurns: parseInteger("CAB_CLAUDE_MAX_TURNS", env.CAB_CLAUDE_MAX_TURNS, 8),
    claudeCommandTimeoutMs: parseInteger(
      "CAB_CLAUDE_COMMAND_TIMEOUT_MS",
      env.CAB_CLAUDE_COMMAND_TIMEOUT_MS,
      600000,
    ),
    claudeMaxRuntimeMs: parseInteger(
      "CAB_CLAUDE_MAX_RUNTIME_MS",
      env.CAB_CLAUDE_MAX_RUNTIME_MS,
      3600000,
    ),
  };

  if (env.SLACK_BOT_TOKEN) {
    config.slackBotToken = env.SLACK_BOT_TOKEN;
  }

  if (env.SLACK_APP_TOKEN) {
    config.slackAppToken = env.SLACK_APP_TOKEN;
  }

  if (env.SLACK_SIGNING_SECRET) {
    config.slackSigningSecret = env.SLACK_SIGNING_SECRET;
  }

  if (env.MATTERMOST_SERVER_URL) {
    config.mattermostServerUrl = env.MATTERMOST_SERVER_URL;
  }

  if (env.MATTERMOST_BOT_TOKEN) {
    config.mattermostBotToken = env.MATTERMOST_BOT_TOKEN;
  }

  if (env.MATTERMOST_WEBHOOK_TOKEN) {
    config.mattermostWebhookToken = env.MATTERMOST_WEBHOOK_TOKEN;
  }

  if (env.MATTERMOST_ACTION_BASE_URL) {
    config.mattermostActionBaseUrl = env.MATTERMOST_ACTION_BASE_URL;
  }

  if (env.MATTERMOST_DM_ONLY) {
    config.mattermostDmOnly = parseBoolean(env.MATTERMOST_DM_ONLY);
  }

  return config;
}
