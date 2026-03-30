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
}
