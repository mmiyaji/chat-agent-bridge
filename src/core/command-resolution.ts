import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

export type CommandResolutionSource =
  | "explicit-path"
  | "path"
  | "windowsapps-shim"
  | "desktop-app"
  | "unknown";

export interface CommandResolution {
  command: string;
  executable: string;
  resolvedPath?: string;
  status: "resolved" | "not_found";
  source?: CommandResolutionSource;
}

export function extractExecutable(command: string): string {
  const trimmed = command.trim();
  if (trimmed.length === 0) {
    return "";
  }

  if (trimmed.startsWith('"')) {
    const closingQuote = trimmed.indexOf('"', 1);
    if (closingQuote > 1) {
      return trimmed.slice(1, closingQuote);
    }
  }

  const firstWhitespace = trimmed.search(/\s/);
  return firstWhitespace === -1 ? trimmed : trimmed.slice(0, firstWhitespace);
}

function isPathLike(executable: string): boolean {
  return (
    executable.includes("\\") ||
    executable.includes("/") ||
    /^[A-Za-z]:/.test(executable) ||
    executable.startsWith(".")
  );
}

function classifyResolvedPath(
  resolvedPath: string,
  explicit: boolean,
): CommandResolutionSource {
  if (explicit) {
    return "explicit-path";
  }

  const normalized = resolvedPath.replaceAll("/", "\\").toLowerCase();
  if (normalized.includes("\\windowsapps\\")) {
    return "windowsapps-shim";
  }
  if (
    normalized.includes("\\appdata\\local\\programs\\") ||
    normalized.includes("\\resources\\app\\")
  ) {
    return "desktop-app";
  }

  return "path";
}

export function resolveCommand(command: string, cwd: string): CommandResolution {
  const executable = extractExecutable(command);
  if (executable.length === 0) {
    return {
      command,
      executable,
      status: "not_found",
    };
  }

  const explicit = isPathLike(executable);
  if (explicit) {
    const absolutePath = path.isAbsolute(executable)
      ? executable
      : path.resolve(cwd, executable);
    if (!fs.existsSync(absolutePath)) {
      return {
        command,
        executable,
        status: "not_found",
        source: "explicit-path",
      };
    }

    const resolvedPath = fs.realpathSync.native(absolutePath);
    return {
      command,
      executable,
      resolvedPath,
      status: "resolved",
      source: classifyResolvedPath(resolvedPath, true),
    };
  }

  try {
    const stdout = execFileSync("where.exe", [executable], {
      cwd,
      stdio: ["ignore", "pipe", "ignore"],
      encoding: "utf8",
      windowsHide: true,
    });
    const firstMatch = stdout
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find((line) => line.length > 0);

    if (!firstMatch) {
      return {
        command,
        executable,
        status: "not_found",
      };
    }

    const resolvedPath = fs.realpathSync.native(firstMatch);
    return {
      command,
      executable,
      resolvedPath,
      status: "resolved",
      source: classifyResolvedPath(resolvedPath, false),
    };
  } catch {
    return {
      command,
      executable,
      status: "not_found",
      source: "unknown",
    };
  }
}
