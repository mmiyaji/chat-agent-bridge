import fs from "node:fs";
import path from "node:path";
import type { AppConfig, JsonValue } from "./types.js";

type Metadata = Record<string, JsonValue | undefined>;

function toJsonValue(input: unknown): JsonValue {
  if (
    input === null ||
    typeof input === "string" ||
    typeof input === "number" ||
    typeof input === "boolean"
  ) {
    return input;
  }

  return String(input);
}

function writeAuditRecordSync(filePath: string, type: string, metadata?: Metadata): void {
  try {
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    const record = JSON.stringify({
      timestamp: new Date().toISOString(),
      type,
      ...(metadata ?? {}),
    });
    fs.appendFileSync(filePath, `${record}\n`, "utf8");
  } catch {
    // Best-effort only. Never let diagnostics crash the bridge.
  }
}

export function writeProcessAudit(filePath: string, type: string, metadata?: Metadata): void {
  writeAuditRecordSync(filePath, type, metadata);
}

export function registerProcessAuditHooks(config: AppConfig): void {
  const auditFile = config.auditLogFile;
  const baseMetadata: Metadata = {
    pid: process.pid,
    ppid: process.ppid,
    node: process.version,
    platform: process.platform,
    arch: process.arch,
    provider: config.chatProvider,
    slackMode: config.slackMode,
    mattermostMode: config.mattermostMode,
  };

  writeAuditRecordSync(auditFile, "bridge.process.start", baseMetadata);

  process.on("warning", (warning) => {
    writeAuditRecordSync(auditFile, "bridge.process.warning", {
      ...baseMetadata,
      name: warning.name,
      message: warning.message,
      stack: warning.stack,
    });
  });

  process.on("uncaughtExceptionMonitor", (error, origin) => {
    writeAuditRecordSync(auditFile, "bridge.process.uncaught-exception", {
      ...baseMetadata,
      origin,
      name: error.name,
      message: error.message,
      stack: error.stack,
    });
  });

  process.on("unhandledRejection", (reason) => {
    writeAuditRecordSync(auditFile, "bridge.process.unhandled-rejection", {
      ...baseMetadata,
      reason: toJsonValue(reason),
    });
  });

  process.on("beforeExit", (code) => {
    writeAuditRecordSync(auditFile, "bridge.process.before-exit", {
      ...baseMetadata,
      code,
    });
  });

  process.on("exit", (code) => {
    writeAuditRecordSync(auditFile, "bridge.process.exit", {
      ...baseMetadata,
      code,
    });
  });

  for (const signal of ["SIGINT", "SIGTERM", "SIGHUP"] as const) {
    process.on(signal, () => {
      writeAuditRecordSync(auditFile, "bridge.process.signal", {
        ...baseMetadata,
        signal,
      });
    });
  }
}
