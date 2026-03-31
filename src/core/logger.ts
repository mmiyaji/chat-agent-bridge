type LogLevel = "info" | "warn" | "error" | "debug";

interface LogEntry {
  level: LogLevel;
  msg: string;
  [key: string]: unknown;
}

function emit(entry: LogEntry): void {
  const record = {
    ts: new Date().toISOString(),
    ...entry,
  };
  const line = JSON.stringify(record);
  if (entry.level === "error") {
    process.stderr.write(`${line}\n`);
  } else {
    process.stdout.write(`${line}\n`);
  }
}

export const logger = {
  info(msg: string, data?: Record<string, unknown>): void {
    emit({ level: "info", msg, ...data });
  },
  warn(msg: string, data?: Record<string, unknown>): void {
    emit({ level: "warn", msg, ...data });
  },
  error(msg: string, data?: Record<string, unknown>): void {
    emit({ level: "error", msg, ...data });
  },
  debug(msg: string, data?: Record<string, unknown>): void {
    if (process.env.CAB_LOG_LEVEL === "debug") {
      emit({ level: "debug", msg, ...data });
    }
  },
};
