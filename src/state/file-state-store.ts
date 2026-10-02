import fs from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";
import path from "node:path";
import { ChatThreadRef, ExecutionRecord, SessionBinding, StateSnapshot } from "../core/types.js";
import { StateStore } from "./state-store.js";

const MAX_EXECUTION_HISTORY = 500;
const ARCHIVE_THRESHOLD = 600;
function channelDefaultKey(provider: string, channelId: string): string {
  return `${provider}:${channelId}`;
}

export class FileStateStore implements StateStore {
  private readonly corruptFilePrefix: string;

  private operations: Promise<void> = Promise.resolve();

  private lastKnownSnapshot: StateSnapshot = { sessions: [], executionHistory: [] };

  constructor(private readonly filePath: string) {
    this.corruptFilePrefix = `${this.filePath}.corrupt`;
  }

  async initialize(): Promise<void> {
    return this.serialize(async () => {
      await fs.mkdir(path.dirname(this.filePath), { recursive: true });
      try {
        await fs.access(this.filePath);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
        await this.writeSnapshot({ sessions: [], executionHistory: [] });
      }
    });
  }

  async findSessionByThread(thread: ChatThreadRef): Promise<SessionBinding | undefined> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      return snapshot.sessions.find((session) => {
        return (
          session.thread.provider === thread.provider &&
          session.thread.channelId === thread.channelId &&
          session.thread.threadId === thread.threadId
        );
      });
    });
  }

  async upsertSession(session: SessionBinding): Promise<void> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      const nextSessions = snapshot.sessions.filter(
        (candidate) => candidate.sessionId !== session.sessionId,
      );
      nextSessions.push(session);

      await this.writeSnapshot({
        ...snapshot,
        sessions: nextSessions,
      });
    });
  }

  async listSessions(): Promise<SessionBinding[]> {
    return this.serialize(async () => (await this.readSnapshot()).sessions);
  }

  async upsertExecutionRecord(record: ExecutionRecord): Promise<void> {
    return this.serialize(async () => {
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
    });
  }

  async getChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
  ): Promise<string | undefined> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      return snapshot.channelDefaults?.[channelDefaultKey(provider, channelId)];
    });
  }

  async setChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
    workingDirectory: string,
  ): Promise<void> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      await this.writeSnapshot({
        ...snapshot,
        channelDefaults: {
          ...(snapshot.channelDefaults ?? {}),
          [channelDefaultKey(provider, channelId)]: workingDirectory,
        },
      });
    });
  }

  async clearChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
  ): Promise<void> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      if (!snapshot.channelDefaults) {
        return;
      }
      const nextChannelDefaults = { ...snapshot.channelDefaults };
      delete nextChannelDefaults[channelDefaultKey(provider, channelId)];
      await this.writeSnapshot({
        ...snapshot,
        channelDefaults: nextChannelDefaults,
      });
    });
  }

  async listChannelDefaultWorkingDirectories(): Promise<Record<string, string>> {
    return this.serialize(async () => {
      const snapshot = await this.readSnapshot();
      return { ...(snapshot.channelDefaults ?? {}) };
    });
  }

  async listExecutionRecords(input?: {
    sessionId?: string;
    limit?: number;
  }): Promise<ExecutionRecord[]> {
    return this.serialize(async () => {
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
    });
  }

  private serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.operations.then(operation);
    // Keep the queue usable after a rejected operation while still returning
    // the original rejection to the caller that issued it.
    this.operations = result.then(() => undefined, () => undefined);
    return result;
  }

  private async readSnapshot(): Promise<StateSnapshot> {
    try {
      const raw = await fs.readFile(this.filePath, "utf8");
      const parsed = JSON.parse(raw) as Partial<StateSnapshot>;
      const channelDefaults =
        parsed.channelDefaults && typeof parsed.channelDefaults === "object"
          ? parsed.channelDefaults
          : {};
      const snapshot: StateSnapshot = {
        sessions: Array.isArray(parsed.sessions) ? parsed.sessions : [],
        executionHistory: Array.isArray(parsed.executionHistory) ? parsed.executionHistory : [],
        channelDefaults,
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
    const tempFilePath = `${this.filePath}.${process.pid}.${randomUUID()}.tmp`;
    try {
      const serialized = JSON.stringify(snapshot, null, 2);
      await fs.writeFile(tempFilePath, serialized, { encoding: "utf8", flag: "wx" });
      // Windows scanners can briefly hold the destination without delete sharing.
      // Keep the previous snapshot intact and retry the atomic rename, never unlink it.
      for (let attempt = 0; ; attempt++) {
        try { await fs.rename(tempFilePath, this.filePath); break; }
        catch (error) {
          const code = (error as NodeJS.ErrnoException).code;
          if (process.platform !== "win32" || attempt >= 5 || !["EPERM", "EACCES", "EBUSY"].includes(code ?? "")) throw error;
          await delay(20 * (attempt + 1));
        }
      }
      this.lastKnownSnapshot = snapshot;
    } finally {
      await fs.rm(tempFilePath, { force: true }).catch(() => undefined);
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
