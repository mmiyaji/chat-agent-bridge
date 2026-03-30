import fs from "node:fs/promises";

export interface ClaudeStoredSession {
  id: string;
  updatedAt: string;
  summary?: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseSessionRecord(value: unknown): ClaudeStoredSession | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const sessionId = value.sessionId;
  const updatedAt = value.updatedAt;
  const summary = value.summary;
  if (typeof sessionId !== "string" || sessionId.length === 0) {
    return undefined;
  }

  if (typeof updatedAt !== "string" || updatedAt.length === 0) {
    return undefined;
  }

  const session: ClaudeStoredSession = {
    id: sessionId,
    updatedAt,
  };
  if (typeof summary === "string" && summary.length > 0) {
    session.summary = summary;
  }

  return session;
}

function readSessionCandidates(payload: unknown): unknown[] {
  if (Array.isArray(payload)) {
    return payload;
  }

  if (!isRecord(payload)) {
    return [];
  }

  const sessions = payload.sessions;
  if (Array.isArray(sessions)) {
    return sessions;
  }

  return Object.values(payload);
}

export async function readRecentClaudeSessions(
  sessionIndexFile: string,
  limit = 5,
): Promise<ClaudeStoredSession[]> {
  const raw = await fs.readFile(sessionIndexFile, "utf8");
  const payload = JSON.parse(raw) as unknown;
  return readSessionCandidates(payload)
    .map((value) => parseSessionRecord(value))
    .filter((session): session is ClaudeStoredSession => Boolean(session))
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
    .slice(0, limit);
}

export async function readLatestClaudeSession(
  sessionIndexFile: string,
): Promise<ClaudeStoredSession | undefined> {
  const sessions = await readRecentClaudeSessions(sessionIndexFile, 1);
  return sessions[0];
}
