import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface } from "node:readline";
import type { Readable, Writable } from "node:stream";

export type AppServerMessage = {
  id?: string | number;
  method?: string;
  params?: Record<string, unknown>;
  result?: unknown;
  error?: { code?: number; message?: string; data?: unknown };
};

type ProcessLike = {
  stdin: Writable;
  stdout: Readable;
  stderr: Readable;
  kill(signal?: NodeJS.Signals | number): boolean;
  once(event: string, listener: (...args: any[]) => void): any;
  on(event: string, listener: (...args: any[]) => void): any;
};

export type AppServerClientOptions = {
  command: string;
  args?: string[];
  cwd: string;
  env?: NodeJS.ProcessEnv;
  requestTimeoutMs: number;
  spawnProcess?: (command: string, args: string[], options: object) => ProcessLike;
};

export class CodexAppServerClient {
  private readonly child: ProcessLike;
  private readonly pending = new Map<string | number, {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
    timer: NodeJS.Timeout;
  }>();
  private nextId = 1;
  private closed = false;
  private stdoutBuffer = "";
  private stderrBuffer = "";
  private readonly closePromise: Promise<{ code: number | null; signal: NodeJS.Signals | null }>;
  private closeResolve!: (value: { code: number | null; signal: NodeJS.Signals | null }) => void;
  private notificationHandler?: (method: string, params: Record<string, unknown>) => void;
  private requestHandler?: (message: AppServerMessage) => Promise<unknown>;

  constructor(private readonly options: AppServerClientOptions) {
    this.child = (options.spawnProcess ?? spawn)(options.command, options.args ?? ["app-server"], {
      cwd: options.cwd,
      env: options.env ?? process.env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      shell: false,
    }) as unknown as ProcessLike;
    this.closePromise = new Promise((resolve) => { this.closeResolve = resolve; });
    const lines = createInterface({ input: this.child.stdout });
    lines.on("line", (line) => this.receive(line));
    this.child.stderr.on("data", (chunk: Buffer | string) => {
      this.stderrBuffer = (this.stderrBuffer + chunk.toString()).slice(-8_000);
    });
    this.child.once("error", (error: Error) => {
      this.closed = true;
      this.closeResolve({ code: null, signal: null });
      this.fail(error);
    });
    this.child.stdin.once("error", (error: Error) => {
      this.closed = true;
      this.closeResolve({ code: null, signal: null });
      this.fail(error);
      this.child.kill();
    });
    this.child.once("close", (code: number | null, signal: NodeJS.Signals | null) => {
      this.closed = true;
      this.closeResolve({ code, signal });
      this.fail(new Error(`Codex App Server exited (${code ?? signal ?? "unknown"}).${this.stderrBuffer ? ` ${this.stderrBuffer.trim()}` : ""}`));
    });
  }

  onNotification(handler: (method: string, params: Record<string, unknown>) => void): void {
    this.notificationHandler = handler;
  }

  onRequest(handler: (message: AppServerMessage) => Promise<unknown>): void {
    this.requestHandler = handler;
  }

  request(method: string, params: Record<string, unknown> = {}): Promise<unknown> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Codex App Server request ${method} timed out after ${this.options.requestTimeoutMs}ms.`));
      }, this.options.requestTimeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.write({ id, method, params });
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  notify(method: string, params: Record<string, unknown> = {}): void {
    this.write({ method, params });
  }

  async waitForClose(timeoutMs: number): Promise<{ code: number | null; signal: NodeJS.Signals | null } | undefined> {
    if (this.closed) return this.closePromise;
    let timer: NodeJS.Timeout | undefined;
    const timeout = new Promise<undefined>((resolve) => {
      const closeTimer = setTimeout(() => resolve(undefined), timeoutMs);
      timer = closeTimer;
      closeTimer.unref?.();
    });
    const result = await Promise.race([this.closePromise, timeout]);
    if (timer) clearTimeout(timer);
    return result;
  }

  whenClosed(): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
    return this.closePromise;
  }

  async dispose(): Promise<void> {
    if (!this.closed) {
      this.child.stdin.end();
      const graceful = await this.waitForClose(500);
      if (!graceful && !this.closed) {
        this.child.kill("SIGKILL");
        await this.waitForClose(1_000);
      }
    }
    this.fail(new Error("Codex App Server connection closed."));
  }

  private write(message: Record<string, unknown>): void {
    if (this.closed) throw new Error("Codex App Server connection is closed.");
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  private receive(line: string): void {
    let message: AppServerMessage;
    try { message = JSON.parse(line) as AppServerMessage; }
    catch { this.stderrBuffer = `${this.stderrBuffer}\nInvalid App Server JSON: ${line}`.slice(-8_000); return; }

    if (message.id !== undefined && !message.method) {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject(new Error(`Codex App Server error ${message.error.code ?? ""}: ${message.error.message ?? "unknown error"}`));
      else pending.resolve(message.result);
      return;
    }
    if (!message.method) return;
    if (message.id !== undefined) {
      void Promise.resolve(this.requestHandler?.(message) ?? Promise.reject(new Error("Unsupported server request.")))
        .then((result) => { try { this.write({ id: message.id, result }); } catch { /* The peer already closed. */ } })
        .catch((error) => {
          try { this.write({ id: message.id, error: { code: -32000, message: error instanceof Error ? error.message : String(error) } }); }
          catch { /* The peer already closed. */ }
        });
      return;
    }
    this.notificationHandler?.(message.method, message.params ?? {});
  }

  private fail(error: Error): void {
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
      this.pending.delete(id);
    }
  }
}
