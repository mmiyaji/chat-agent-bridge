import fs from "node:fs/promises";
import path from "node:path";
import { JsonValue } from "./types.js";

export interface AuditEvent {
  type: string;
  [key: string]: JsonValue | undefined;
}

export interface AuditLogger {
  log(event: AuditEvent): Promise<void>;
}

export class NoopAuditLogger implements AuditLogger {
  async log(_event: AuditEvent): Promise<void> {}
}

export class FileAuditLogger implements AuditLogger {
  constructor(private readonly filePath: string) {}

  async log(event: AuditEvent): Promise<void> {
    await fs.mkdir(path.dirname(this.filePath), { recursive: true });
    const record = JSON.stringify({
      timestamp: new Date().toISOString(),
      ...event,
    });
    await fs.appendFile(this.filePath, `${record}\n`, "utf8");
  }
}
