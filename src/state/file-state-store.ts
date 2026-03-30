import fs from "node:fs/promises";
import path from "node:path";
import { ChatThreadRef, ExecutionRecord, SessionBinding, StateSnapshot } from "../core/types.js";
import { StateStore } from "./state-store.js";

export class FileStateStore implements StateStore {
  constructor(private readonly filePath: string) {}

  async initialize(): Promise<void> {
    await fs.mkdir(path.dirname(this.filePath), { recursive: true });

    try {
      await fs.access(this.filePath);
    } catch {
      await this.writeSnapshot({ sessions: [], executionHistory: [] });
    }
  }

  async findSessionByThread(thread: ChatThreadRef): Promise<SessionBinding | undefined> {
    const snapshot = await this.readSnapshot();

    return snapshot.sessions.find((session) => {
      return (
        session.thread.provider === thread.provider &&
        session.thread.channelId === thread.channelId &&
        session.thread.threadId === thread.threadId
      );
    });
  }

  async upsertSession(session: SessionBinding): Promise<void> {
    const snapshot = await this.readSnapshot();
    const nextSessions = snapshot.sessions.filter(
      (candidate) => candidate.sessionId !== session.sessionId,
    );
    nextSessions.push(session);

    await this.writeSnapshot({ sessions: nextSessions });
  }

  async listSessions(): Promise<SessionBinding[]> {
    const snapshot = await this.readSnapshot();
    return snapshot.sessions;
  }

  async upsertExecutionRecord(record: ExecutionRecord): Promise<void> {
    const snapshot = await this.readSnapshot();
    const nextHistory = snapshot.executionHistory.filter(
      (candidate) => candidate.executionId !== record.executionId,
    );
    nextHistory.push(record);

    await this.writeSnapshot({
      ...snapshot,
      executionHistory: nextHistory,
    });
  }

  async listExecutionRecords(input?: {
    sessionId?: string;
    limit?: number;
  }): Promise<ExecutionRecord[]> {
    const snapshot = await this.readSnapshot();
    const filtered = input?.sessionId
      ? snapshot.executionHistory.filter((record) => record.sessionId === input.sessionId)
      : snapshot.executionHistory;

    const sorted = [...filtered].sort((left, right) => {
      return right.createdAt.localeCompare(left.createdAt);
    });

    if (!input?.limit || input.limit < 1) {
      return sorted;
    }

    return sorted.slice(0, input.limit);
  }

  private async readSnapshot(): Promise<StateSnapshot> {
    const raw = await fs.readFile(this.filePath, "utf8");
    const parsed = JSON.parse(raw) as Partial<StateSnapshot>;

    return {
      sessions: Array.isArray(parsed.sessions) ? parsed.sessions : [],
      executionHistory: Array.isArray(parsed.executionHistory) ? parsed.executionHistory : [],
    };
  }

  private async writeSnapshot(snapshot: StateSnapshot): Promise<void> {
    await fs.writeFile(this.filePath, JSON.stringify(snapshot, null, 2), "utf8");
  }
}
