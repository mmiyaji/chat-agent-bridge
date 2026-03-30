import fs from "node:fs/promises";

export interface CodexStoredSession {
  id: string;
  updatedAt: string;
  threadName?: string;
}

function parseLine(line: string): CodexStoredSession | undefined {
  const idMatch = /"id":"([^"]+)"/.exec(line);
  const updatedAtMatch = /"updated_at":"([^"]+)"/.exec(line);
  const threadNameMatch = /"thread_name":"(.*)","updated_at":/.exec(line);

  if (!idMatch?.[1] || !updatedAtMatch?.[1]) {
    return undefined;
  }

  const session: CodexStoredSession = {
    id: idMatch[1],
    updatedAt: updatedAtMatch[1],
  };

  if (threadNameMatch?.[1]) {
    session.threadName = threadNameMatch[1];
  }

  return session;
}

export async function readLatestCodexSession(sessionIndexFile: string): Promise<CodexStoredSession | undefined> {
  const sessions = await readRecentCodexSessions(sessionIndexFile);
  return sessions[0];
}

export async function readRecentCodexSessions(
  sessionIndexFile: string,
  limit = 5,
): Promise<CodexStoredSession[]> {
  const raw = await fs.readFile(sessionIndexFile, "utf8");
  return raw
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => parseLine(line))
    .filter((session): session is CodexStoredSession => Boolean(session))
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
    .slice(0, limit);
}
