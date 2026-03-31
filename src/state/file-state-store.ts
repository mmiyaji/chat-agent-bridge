import fs from "node:fs/promises";
import path from "node:path";
import { ChatThreadRef, ExecutionRecord, SessionBinding, StateSnapshot } from "../core/types.js";
import { StateStore } from "./state-store.js";

const MAX_EXECUTION_HISTORY = 500;
const ARCHIVE_THRESHOLD = 600;
const DEBOUNCE_MS = 100;

export class FileStateStore implements StateStore {
  private readonly tempFilePath: string;

  private readonly corruptFilePrefix: string;

  private pendingWrite: Promise<void> = Promise.resolve();

  private lastKnownSnapshot: StateSnapshot = { sessions: [], executionHistory: [] };

  private debounceTimer: NodeJS.Timeout | undefined;
  private debouncedSnapshot: StateSnapshot | undefined;
  private debounceResolvers: Array<() => void> = [];

  constructor(private readonly filePath: string) {
    this.tempFilePath = `${this.filePath}.tmp`;
    this.corruptFilePrefix = `${this.filePath}.corrupt`;
  }

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

    await this.writeSnapshot({
      ...snapshot,
      sessions: nextSessions,
    });
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

    const trimmed =
      nextHistory.length > ARCHIVE_THRESHOLD
        ? nextHistory
            .slice()
            .sort((a, b) => a.createdAt.localeCompare(b.createdAt))
            .slice(nextHistory.length - MAX_EXECUTION_HISTORY)
        : nextHistory;

    await this.writeSnapshot({
      ...snapshot,
      executionHistory: trimmed,
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
    try {
      const raw = await fs.readFile(this.filePath, "utf8");
      const parsed = JSON.parse(raw) as Partial<StateSnapshot>;
      const snapshot = {
        sessions: Array.isArray(parsed.sessions) ? parsed.sessions : [],
        executionHistory: Array.isArray(parsed.executionHistory) ? parsed.executionHistory : [],
      };
      this.lastKnownSnapshot = snapshot;
      return snapshot;
    } catch (error) {
      const nodeError = error as NodeJS.ErrnoException;
      if (nodeError.code === "ENOENT") {
        return this.lastKnownSnapshot;
      }

      if (error instanceof SyntaxError) {
        await this.quarantineCorruptFile();
        await this.writeSnapshot(this.lastKnownSnapshot);
        return this.lastKnownSnapshot;
      }

      throw error;
    }
  }

  private async writeSnapshot(snapshot: StateSnapshot): Promise<void> {
    this.lastKnownSnapshot = snapshot;
    this.debouncedSnapshot = snapshot;

    return new Promise<void>((resolve) => {
      this.debounceResolvers.push(resolve);
      if (this.debounceTimer) {
        clearTimeout(this.debounceTimer);
      }
      this.debounceTimer = setTimeout(() => {
        this.debounceTimer = undefined;
        void this.flushWrite();
      }, DEBOUNCE_MS);
    });
  }

  private async flushWrite(): Promise<void> {
    const snapshot = this.debouncedSnapshot;
    const resolvers = this.debounceResolvers;
    this.debouncedSnapshot = undefined;
    this.debounceResolvers = [];
    if (!snapshot) {
      for (const resolve of resolvers) resolve();
      return;
    }

    const writeOperation = this.pendingWrite.catch(() => undefined).then(async () => {
      const serialized = JSON.stringify(snapshot, null, 2);
      await fs.writeFile(this.tempFilePath, serialized, "utf8");
      await fs.rename(this.tempFilePath, this.filePath);
      this.lastKnownSnapshot = snapshot;
    });

    this.pendingWrite = writeOperation;
    try {
      await writeOperation;
    } finally {
      for (const resolve of resolvers) resolve();
    }
  }

  private async quarantineCorruptFile(): Promise<void> {
    const corruptPath = `${this.corruptFilePrefix}.${Date.now()}.json`;

    try {
      await fs.rename(this.filePath, corruptPath);
    } catch (error) {
      const nodeError = error as NodeJS.ErrnoException;
      if (nodeError.code === "ENOENT") {
        return;
      }

      throw error;
    }
  }
}
