import crypto from "node:crypto";
import path from "node:path";
import { assertPathAllowed } from "./paths.js";
import { AgentKind, ChatThreadRef, SessionBinding } from "./types.js";
import { StateStore } from "../state/state-store.js";

export class SessionManager {
  constructor(private readonly stateStore: StateStore) {}

  async getByThread(thread: ChatThreadRef): Promise<SessionBinding | undefined> {
    return this.stateStore.findSessionByThread(thread);
  }

  async getOrCreate(input: {
    agent: AgentKind;
    thread: ChatThreadRef;
    workingDirectory: string;
    allowedDirectories: string[];
  }): Promise<SessionBinding> {
    const existing = await this.getByThread(input.thread);
    const normalizedWorkingDirectory = assertPathAllowed(
      input.workingDirectory,
      input.allowedDirectories,
    );

    if (existing) {
      if (path.resolve(existing.workingDirectory) !== normalizedWorkingDirectory) {
        throw new Error(
          `Thread ${input.thread.threadId} is already bound to ${existing.workingDirectory}.`,
        );
      }

      const updated: SessionBinding = {
        ...existing,
        updatedAt: new Date().toISOString(),
      };
      await this.stateStore.upsertSession(updated);
      return updated;
    }

    const now = new Date().toISOString();
    const created: SessionBinding = {
      sessionId: crypto.randomUUID(),
      thread: input.thread,
      agent: input.agent,
      workingDirectory: normalizedWorkingDirectory,
      createdAt: now,
      updatedAt: now,
    };

    await this.stateStore.upsertSession(created);
    return created;
  }
}
