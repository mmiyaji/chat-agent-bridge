import path from "node:path";
import { assertPathAllowed } from "./paths.js";
import { ApprovalPolicy, SessionBinding } from "./types.js";

export type BridgeCommand =
  | { kind: "help" }
  | { kind: "status" }
  | { kind: "history"; limit: number }
  | { kind: "cancel" }
  | { kind: "list-roots" }
  | { kind: "set-workdir"; rawPath: string }
  | { kind: "set-repo"; alias: string }
  | { kind: "attach"; sessionId: string }
  | { kind: "attach-last" }
  | { kind: "detach" }
  | { kind: "set-mode"; approvalPolicy: ApprovalPolicy }
  | { kind: "new-session"; approvalPolicy?: ApprovalPolicy }
  | { kind: "restart-bridge" };

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

  const newSessionMatch = /^new(?:\s+(.+))?$/i.exec(normalized);
  if (newSessionMatch) {
    const approvalPolicy = newSessionMatch[1]
      ? parseApprovalPolicy(newSessionMatch[1])
      : undefined;
    if (newSessionMatch[1] && !approvalPolicy) {
      return undefined;
    }

    return approvalPolicy
      ? { kind: "new-session", approvalPolicy }
      : { kind: "new-session" };
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

  if (
    lower === "restart" ||
    lower === "restart bridge" ||
    lower === "bridge restart" ||
    lower === "再起動" ||
    lower === "ブリッジ再起動"
  ) {
    return { kind: "restart-bridge" };
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
