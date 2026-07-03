import fs from "node:fs/promises";
import path from "node:path";

export interface ClaudeProjectSession {
  id: string;
  updatedAt: string;
  label?: string;
}

const LABEL_MAX_LENGTH = 80;
const LABEL_READ_BYTES = 64 * 1024;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isErrnoException(error: unknown, code: string): boolean {
  return (
    error instanceof Error &&
    "code" in error &&
    (error as NodeJS.ErrnoException).code === code
  );
}

export function encodeClaudeProjectDir(workingDirectory: string): string {
  return path.resolve(workingDirectory).replace(/[^a-zA-Z0-9]/g, "-");
}

async function resolveProjectFolder(
  projectsRoot: string,
  workingDirectory: string,
): Promise<string | undefined> {
  const encoded = encodeClaudeProjectDir(workingDirectory);
  const exactPath = path.join(projectsRoot, encoded);
  try {
    const stat = await fs.stat(exactPath);
    if (stat.isDirectory()) {
      return exactPath;
    }
  } catch (error) {
    if (!isErrnoException(error, "ENOENT")) {
      throw error;
    }
  }

  let entries: import("node:fs").Dirent[];
  try {
    entries = await fs.readdir(projectsRoot, { withFileTypes: true });
  } catch (error) {
    if (isErrnoException(error, "ENOENT")) {
      return undefined;
    }
    throw error;
  }

  const lowerEncoded = encoded.toLowerCase();
  const match = entries.find(
    (entry) => entry.isDirectory() && entry.name.toLowerCase() === lowerEncoded,
  );
  return match ? path.join(projectsRoot, match.name) : undefined;
}

function normalizeLabel(text: string): string {
  const normalized = text.replace(/\s+/g, " ").trim();
  if (normalized.length <= LABEL_MAX_LENGTH) {
    return normalized;
  }

  return `${normalized.slice(0, LABEL_MAX_LENGTH - 3)}...`;
}

function extractRecordText(record: Record<string, unknown>): string | undefined {
  const type = record.type;
  if (type === "summary") {
    const summary = record.summary;
    return typeof summary === "string" && summary.trim().length > 0 ? summary : undefined;
  }

  if (type === "user") {
    const message = record.message;
    if (!isRecord(message)) {
      return undefined;
    }

    const content = message.content;
    if (typeof content === "string") {
      return content.trim().length > 0 ? content : undefined;
    }

    if (Array.isArray(content)) {
      for (const block of content) {
        if (isRecord(block) && block.type === "text" && typeof block.text === "string") {
          if (block.text.trim().length > 0) {
            return block.text;
          }
        }
      }
    }
  }

  return undefined;
}

async function readSessionLabel(filePath: string): Promise<string | undefined> {
  let handle: import("node:fs/promises").FileHandle | undefined;
  try {
    handle = await fs.open(filePath, "r");
    const buffer = Buffer.alloc(LABEL_READ_BYTES);
    const { bytesRead } = await handle.read(buffer, 0, LABEL_READ_BYTES, 0);
    const text = buffer.subarray(0, bytesRead).toString("utf8");
    const lines = text.split("\n");
    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed.length === 0) {
        continue;
      }

      let parsed: unknown;
      try {
        parsed = JSON.parse(trimmed);
      } catch {
        continue;
      }

      if (!isRecord(parsed)) {
        continue;
      }

      const extracted = extractRecordText(parsed);
      if (extracted !== undefined) {
        return normalizeLabel(extracted);
      }
    }

    return undefined;
  } finally {
    await handle?.close();
  }
}

export async function readClaudeProjectSessions(
  projectsRoot: string,
  workingDirectory: string,
  limit = 5,
): Promise<ClaudeProjectSession[]> {
  const projectFolder = await resolveProjectFolder(projectsRoot, workingDirectory);
  if (!projectFolder) {
    return [];
  }

  const entries = await fs.readdir(projectFolder, { withFileTypes: true });
  const candidates: Array<{ id: string; filePath: string; mtimeMs: number }> = [];
  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.endsWith(".jsonl")) {
      continue;
    }

    const stem = entry.name.slice(0, -".jsonl".length);
    if (stem.length === 0) {
      continue;
    }

    const filePath = path.join(projectFolder, entry.name);
    const stat = await fs.stat(filePath);
    if (stat.size === 0) {
      continue;
    }

    candidates.push({ id: stem, filePath, mtimeMs: stat.mtimeMs });
  }

  candidates.sort((left, right) => right.mtimeMs - left.mtimeMs);

  const selected = candidates.slice(0, limit);
  const sessions: ClaudeProjectSession[] = [];
  for (const candidate of selected) {
    const label = await readSessionLabel(candidate.filePath);
    sessions.push({
      id: candidate.id,
      updatedAt: new Date(candidate.mtimeMs).toISOString(),
      ...(label ? { label } : {}),
    });
  }

  return sessions;
}
