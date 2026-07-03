import { ChatThreadRef, ExecutionRecord, SessionBinding } from "../core/types.js";

export interface StateStore {
  initialize(): Promise<void>;
  findSessionByThread(thread: ChatThreadRef): Promise<SessionBinding | undefined>;
  upsertSession(session: SessionBinding): Promise<void>;
  listSessions(): Promise<SessionBinding[]>;
  upsertExecutionRecord(record: ExecutionRecord): Promise<void>;
  listExecutionRecords(input?: {
    sessionId?: string;
    limit?: number;
  }): Promise<ExecutionRecord[]>;
  getChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
  ): Promise<string | undefined>;
  setChannelDefaultWorkingDirectory(
    provider: string,
    channelId: string,
    workingDirectory: string,
  ): Promise<void>;
  clearChannelDefaultWorkingDirectory(provider: string, channelId: string): Promise<void>;
  listChannelDefaultWorkingDirectories(): Promise<Record<string, string>>;
}
