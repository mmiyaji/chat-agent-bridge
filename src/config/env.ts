import path from "node:path";
import { ApprovalPolicy, AppConfig, NetworkAccess, SlackMode } from "../core/types.js";

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

export function createAppConfig(env: NodeJS.ProcessEnv): AppConfig {
  const allowedDirectories = parseAllowedDirs(readRequired("CAB_ALLOWED_DIRS", env));
  const defaultAllowedDirectory = allowedDirectories[0];
  if (!defaultAllowedDirectory) {
    throw new Error("CAB_ALLOWED_DIRS must include at least one directory.");
  }

  const config: AppConfig = {
    allowedDirectories,
    repoAliases: parseRepoAliases(env.CAB_REPO_ALIASES, allowedDirectories),
    defaultApprovalPolicy: parseApprovalPolicy(env.CAB_DEFAULT_APPROVAL_POLICY),
    networkAccess: parseNetworkAccess(env.CAB_NETWORK_ACCESS),
    slackMode: parseSlackMode(env.CAB_SLACK_MODE),
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

  return config;
}
