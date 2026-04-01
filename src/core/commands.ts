import path from "node:path";
import { assertPathAllowed } from "./paths.js";
import { AgentKind, ApprovalPolicy, SessionBinding } from "./types.js";

export type BridgeCommand =
  | { kind: "help" }
  | { kind: "status" }
  | { kind: "history"; limit: number }
  | { kind: "cancel" }
  | { kind: "new-thread" }
  | { kind: "fork-session"; approvalPolicy?: ApprovalPolicy; agent?: AgentKind }
  | { kind: "list-sessions" }
  | { kind: "list-approvals" }
  | { kind: "list-failures"; limit: number }
  | { kind: "list-roots" }
  | { kind: "set-workdir"; rawPath: string }
  | { kind: "set-repo"; alias: string }
  | { kind: "set-agent"; agent: AgentKind }
  | { kind: "attach"; sessionId: string }
  | { kind: "attach-last" }
  | { kind: "detach" }
  | { kind: "set-mode"; approvalPolicy: ApprovalPolicy }
  | { kind: "new-session"; approvalPolicy?: ApprovalPolicy; agent?: AgentKind }
  | { kind: "restart-bridge"; rebuild: boolean };

function parseApprovalPolicy(value: string): ApprovalPolicy | undefined {
  const normalized = value.trim().toLowerCase();
  if (normalized === "allow" || normalized === "never") {
    return "never";
  }

  if (normalized === "manual" || normalized === "readonly" || normalized === "read-only") {
    return "manual";
  }

  return undefined;
}

function parseAgentKind(value: string): AgentKind | undefined {
  const normalized = value.trim().toLowerCase();
  if (normalized === "codex") {
    return "codex";
  }

  if (normalized === "claude" || normalized === "claude-code") {
    return "claude-code";
  }

  if (normalized === "cursor") {
    return "cursor";
  }

  if (normalized === "gemini") {
    return "gemini";
  }

  return undefined;
}

function parseSessionInitOptions(
  raw: string | undefined,
): { approvalPolicy?: ApprovalPolicy; agent?: AgentKind } | undefined {
  let approvalPolicy: ApprovalPolicy | undefined;
  let agent: AgentKind | undefined;

  if (!raw) {
    return {};
  }

  for (const part of raw.split(/\s+/)) {
    const parsedApprovalPolicy = parseApprovalPolicy(part);
    if (parsedApprovalPolicy) {
      if (approvalPolicy) {
        return undefined;
      }
      approvalPolicy = parsedApprovalPolicy;
      continue;
    }

    const parsedAgent = parseAgentKind(part);
    if (parsedAgent) {
      if (agent) {
        return undefined;
      }
      agent = parsedAgent;
      continue;
    }

    return undefined;
  }

  return {
    ...(approvalPolicy ? { approvalPolicy } : {}),
    ...(agent ? { agent } : {}),
  };
}

export function parseBridgeCommand(text: string): BridgeCommand | undefined {
  const normalized = text.trim();
  if (normalized.length === 0) {
    return undefined;
  }

  const lower = normalized.toLowerCase();
  if (lower === "help") {
    return { kind: "help" };
  }

  if (lower === "status") {
    return { kind: "status" };
  }

  if (lower === "cancel" || lower === "stop") {
    return { kind: "cancel" };
  }

  if (lower === "new-thread" || lower === "split") {
    return { kind: "new-thread" };
  }

  const forkMatch = /^fork(?:\s+(.+))?$/i.exec(normalized);
  if (forkMatch) {
    const options = parseSessionInitOptions(forkMatch[1]);
    if (!options) {
      return undefined;
    }

    return {
      kind: "fork-session",
      ...options,
    };
  }

  if (lower === "sessions") {
    return { kind: "list-sessions" };
  }

  if (lower === "approvals" || lower === "pending") {
    return { kind: "list-approvals" };
  }

  const failuresMatch = /^failures(?:\s+(\d+))?$/i.exec(normalized);
  if (failuresMatch) {
    const limit = failuresMatch[1] ? Number.parseInt(failuresMatch[1], 10) : 5;
    if (!Number.isFinite(limit) || limit < 1) {
      return undefined;
    }

    return {
      kind: "list-failures",
      limit: Math.min(limit, 20),
    };
  }

  const historyMatch = /^history(?:\s+(\d+))?$/i.exec(normalized);
  if (historyMatch) {
    const limit = historyMatch[1] ? Number.parseInt(historyMatch[1], 10) : 5;
    if (!Number.isFinite(limit) || limit < 1) {
      return undefined;
    }

    return {
      kind: "history",
      limit: Math.min(limit, 20),
    };
  }

  if (lower === "repos" || lower === "roots") {
    return { kind: "list-roots" };
  }

  if (lower === "attach-last") {
    return { kind: "attach-last" };
  }

  const setAgentMatch = /^agent\s+(.+)$/i.exec(normalized);
  if (setAgentMatch?.[1]) {
    const agent = parseAgentKind(setAgentMatch[1]);
    if (!agent) {
      return undefined;
    }

    return {
      kind: "set-agent",
      agent,
    };
  }

  const newSessionMatch = /^new(?:\s+(.+))?$/i.exec(normalized);
  if (newSessionMatch) {
    const options = parseSessionInitOptions(newSessionMatch[1]);
    if (!options) {
      return undefined;
    }

    return {
      kind: "new-session",
      ...options,
    };
  }

  if (lower === "detach") {
    return { kind: "detach" };
  }

  const modeMatch = /^mode\s+(.+)$/i.exec(normalized);
  if (modeMatch?.[1]) {
    const approvalPolicy = parseApprovalPolicy(modeMatch[1]);
    if (!approvalPolicy) {
      return undefined;
    }

    return { kind: "set-mode", approvalPolicy };
  }

  const restartMatch =
    /^(?:restart(?:\s+bridge)?|bridge\s+restart|再起動|ブリッジ再起動)(?:\s+(rebuild|build|with-build|with-rebuild))?$/i.exec(
      normalized,
    );
  if (restartMatch) {
    return {
      kind: "restart-bridge",
      rebuild: Boolean(restartMatch[1]),
    };
  }

  const attachMatch = /^attach\s+(.+)$/i.exec(normalized);
  if (attachMatch?.[1]) {
    return {
      kind: "attach",
      sessionId: attachMatch[1].trim(),
    };
  }

  const repoMatch = /^repo\s+(.+)$/i.exec(normalized);
  if (repoMatch?.[1]) {
    return {
      kind: "set-repo",
      alias: repoMatch[1].trim().toLowerCase(),
    };
  }

  const workdirMatch = /^(?:cwd|workdir)\s+(.+)$/i.exec(normalized);
  if (!workdirMatch?.[1]) {
    return undefined;
  }

  return {
    kind: "set-workdir",
    rawPath: workdirMatch[1].trim(),
  };
}

export function resolveRequestedWorkingDirectory(input: {
  rawPath: string;
  session?: SessionBinding;
  defaultWorkingDirectory: string;
  allowedDirectories: string[];
}): string {
  const baseDirectory = input.session?.workingDirectory ?? input.defaultWorkingDirectory;
  const candidate = path.isAbsolute(input.rawPath)
    ? input.rawPath
    : path.resolve(baseDirectory, input.rawPath);

  return assertPathAllowed(candidate, input.allowedDirectories);
}

export function formatRepoAliases(repoAliases: Record<string, string>): string {
  const aliases = Object.entries(repoAliases)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([alias, repoPath]) => `- \`${alias}\` -> \`${repoPath}\``);

  return aliases.join("\n");
}
